using System.Security.Cryptography;
using Encoding.WorkerAgent.Runtime;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>Chemins média sûrs : confinement à la racine, refus des liens symboliques, copie vérifiée et verrou exclusif. Fichiers temporaires uniquement.</summary>
public sealed class SafeMediaPathsTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("encoding-safe-paths-").FullName;
    public void Dispose() => Directory.Delete(root, true);
    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Fact]
    public void ResolveKeepsRelativePathsInsideTheRoot()
    {
        Assert.Equal(Path.Combine(root, "show", "ep.mkv"), SafeMediaPaths.Resolve(root, "show/ep.mkv"));
        Assert.Equal(Path.Combine(root, "show", "ep.mkv"), SafeMediaPaths.Resolve(root, "show\\ep.mkv"));
    }

    [Theory]
    [InlineData("../outside.mkv")]
    [InlineData("show/../../outside.mkv")]
    [InlineData("/etc/passwd")]
    [InlineData("show//ep.mkv")]
    [InlineData("")]
    public void ResolveRejectsTraversalAndAbsolutePaths(string relative) =>
        Assert.Throws<ArgumentException>(() => SafeMediaPaths.Resolve(root, relative));

    [LinuxFact]
    public void ResolveRejectsSymbolicLinksInThePathAndAsRoot()
    {
        var outside = Directory.CreateTempSubdirectory("encoding-safe-outside-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, "x.mkv"), "synthetic");
            Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside);
            File.CreateSymbolicLink(Path.Combine(root, "file-link.mkv"), Path.Combine(outside, "x.mkv"));
            var rootLink = Path.Combine(outside, "root-link");
            Directory.CreateSymbolicLink(rootLink, root);

            Assert.Throws<IOException>(() => SafeMediaPaths.Resolve(root, "link/ep.mkv"));
            Assert.Throws<IOException>(() => SafeMediaPaths.Resolve(root, "file-link.mkv"));
            Assert.Throws<IOException>(() => SafeMediaPaths.Resolve(rootLink, "ep.mkv"));
        }
        finally { Directory.Delete(outside, true); }
    }

    [Fact]
    public async Task DigestIsTheLowercaseSha256()
    {
        var path = Path.Combine(root, "a.bin"); var bytes = "synthetic"u8.ToArray(); File.WriteAllBytes(path, bytes);

        Assert.Equal(Sha(bytes), await SafeMediaPaths.DigestAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task CopyVerifiedPublishesOnlyAMatchingCopy()
    {
        var source = Path.Combine(root, "in.bin"); var target = Path.Combine(root, "out.bin"); var bytes = "payload"u8.ToArray();
        File.WriteAllBytes(source, bytes);

        await SafeMediaPaths.CopyVerifiedAsync(source, target, Sha(bytes), CancellationToken.None);

        Assert.Equal(bytes, File.ReadAllBytes(target));
        Assert.False(File.Exists(target + ".copy.partial"));
    }

    [Fact]
    public async Task CopyVerifiedWithWrongDigestKeepsExistingTargetAndLeavesNoPartial()
    {
        var source = Path.Combine(root, "in.bin"); var target = Path.Combine(root, "out.bin");
        File.WriteAllText(source, "payload"); File.WriteAllText(target, "previous");

        await Assert.ThrowsAsync<IOException>(() => SafeMediaPaths.CopyVerifiedAsync(source, target, new string('0', 64), CancellationToken.None));

        Assert.Equal("previous", File.ReadAllText(target));
        Assert.False(File.Exists(target + ".copy.partial"));
    }

    [Fact]
    public void LockIsExclusivePerMediaAndReleasedOnDispose()
    {
        if (OperatingSystem.IsMacOS()) return;
        var media = Path.Combine(root, "ep.mkv"); var locks = Path.Combine(root, "locks");
        var first = SafeMediaPaths.Lock(media, locks);

        Assert.Throws<SafeMediaPaths.MediaLockBusyException>(() => SafeMediaPaths.Lock(media, locks));
        using (SafeMediaPaths.Lock(Path.Combine(root, "other.mkv"), locks)) { }
        first.Dispose();
        SafeMediaPaths.Lock(media, locks).Dispose();
    }
}
