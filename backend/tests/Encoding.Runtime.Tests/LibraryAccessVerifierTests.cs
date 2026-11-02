using Encoding.NodeAgent.Runtime;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class LibraryAccessVerifierTests
{
    [Fact]
    public async Task ChecksEffectiveAccessWithoutChangingMediaOrLeavingProbeFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "encoding-access-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var media = Path.Combine(root, "synthetic.txt");
            await File.WriteAllTextAsync(media, "untouched");
            await LibraryAccessVerifier.VerifyPublicationAsync(root, CancellationToken.None);
            Assert.Equal("untouched", await File.ReadAllTextAsync(media));
            Assert.Single(Directory.EnumerateFileSystemEntries(root));
            Directory.CreateDirectory(Path.Combine(root, ".encoding-results"));
            await LibraryAccessVerifier.VerifyPublicationAsync(root, CancellationToken.None);
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, ".encoding-results")));
        }
        finally { Directory.Delete(root, true); }
    }

    [NonRootUnixFact]
    public async Task RefusesDeclaredAccessWhenProcessCannotWrite()
    {
        var root = Path.Combine(Path.GetTempPath(), "encoding-denied-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            if (OperatingSystem.IsWindows()) return;
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var error = await Assert.ThrowsAsync<LibraryAccessException>(() => LibraryAccessVerifier.VerifyPublicationAsync(root, CancellationToken.None));
            Assert.Contains("création", error.Message);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(root, true);
        }
    }

    [NonRootUnixFact]
    public async Task ReplacementChecksSourceDirectoryEvenWhenPublicationDirectoryIsWritable()
    {
        var root = Path.Combine(Path.GetTempPath(), "encoding-source-access-" + Guid.NewGuid());
        var parent = Path.Combine(root,"nested");
        Directory.CreateDirectory(parent);
        var media = Path.Combine(parent,"synthetic.txt");
        await File.WriteAllTextAsync(media,"untouched");
        try
        {
            if (OperatingSystem.IsWindows()) return;
            File.SetUnixFileMode(parent,UnixFileMode.UserRead | UnixFileMode.UserExecute);
            await LibraryAccessVerifier.VerifyPublicationAsync(root,default);
            await Assert.ThrowsAsync<LibraryAccessException>(() => LibraryAccessVerifier.VerifyPublicationAsync(root,default,"nested/synthetic.txt"));
            Assert.Equal("untouched",await File.ReadAllTextAsync(media));
        }
        finally
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(parent,UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(root,true);
        }
    }

    [Fact]
    public async Task RefusesAnUnavailableMountWithoutCreatingIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "encoding-missing-" + Guid.NewGuid());
        await Assert.ThrowsAsync<LibraryAccessException>(() => LibraryAccessVerifier.VerifyPublicationAsync(root, CancellationToken.None));
        Assert.False(Directory.Exists(root));
    }
}

public sealed class NonRootUnixFactAttribute : FactAttribute
{
    public NonRootUnixFactAttribute()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
            Skip = "Effective Unix denial requires a process without root privileges.";
    }
}
