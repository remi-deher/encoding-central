using Encoding.Core;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class AtomicFilePublicationTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("atomic-publication-").FullName;

    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public void PublishMovesTemporaryFileToTarget()
    {
        var temporary = Path.Combine(root, "out.tmp");
        var target = Path.Combine(root, "out.mkv");
        File.WriteAllText(temporary, "result");

        AtomicFilePublication.Publish(temporary, target);

        Assert.False(File.Exists(temporary));
        Assert.Equal("result", File.ReadAllText(target));
    }

    [Fact]
    public void PublishNeverOverwritesExistingTarget()
    {
        var temporary = Path.Combine(root, "out.tmp");
        var target = Path.Combine(root, "out.mkv");
        File.WriteAllText(temporary, "new");
        File.WriteAllText(target, "source");

        Assert.Throws<IOException>(() => AtomicFilePublication.Publish(temporary, target));

        Assert.Equal("source", File.ReadAllText(target));
        Assert.Equal("new", File.ReadAllText(temporary));
    }

    [Fact]
    public void PublishRejectsTargetInAnotherDirectory()
    {
        var other = Directory.CreateDirectory(Path.Combine(root, "other")).FullName;
        var temporary = Path.Combine(root, "out.tmp");
        File.WriteAllText(temporary, "result");
        var target = Path.Combine(other, "out.mkv");

        Assert.Throws<IOException>(() => AtomicFilePublication.Publish(temporary, target));

        Assert.True(File.Exists(temporary));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void PublishKeepsTheTargetPathExistingFileUntouchedEvenWhenCreatedConcurrently()
    {
        var temporary = Path.Combine(root, "out.tmp");
        var target = Path.Combine(root, "out.mkv");
        File.WriteAllText(temporary, "result");
        File.WriteAllText(target, "third party created just before publication");

        var failure = Assert.Throws<IOException>(() => AtomicFilePublication.Publish(temporary, target));

        Assert.NotNull(failure);
        Assert.Equal("third party created just before publication", File.ReadAllText(target));
        Assert.True(File.Exists(temporary), "Le temporaire reste présent : l'appelant sait qu'il n'a rien publié.");
    }

    [Fact]
    public void ConfirmSucceedsOnAnExistingPublishedFile()
    {
        var target = Path.Combine(root, "out.mkv");
        File.WriteAllText(target, "result");

        AtomicFilePublication.Confirm(target);

        Assert.Equal("result", File.ReadAllText(target));
    }

    [Fact]
    public void ConfirmFailsWhenTheDirectoryCannotBeSynchronized()
    {
        if (!OperatingSystem.IsLinux()) return;
        var target = Path.Combine(root, "missing-directory", "out.mkv");

        Assert.Throws<IOException>(() => AtomicFilePublication.Confirm(target));
    }

    [Fact]
    public void PublishRejectsTargetInAnotherDirectoryEvenWithSamePrefix()
    {
        var sibling = Directory.CreateDirectory(root + "-sibling").FullName;
        try
        {
            var temporary = Path.Combine(root, "out.tmp");
            File.WriteAllText(temporary, "result");

            Assert.Throws<IOException>(() => AtomicFilePublication.Publish(temporary, Path.Combine(sibling, "out.mkv")));
            Assert.True(File.Exists(temporary));
        }
        finally { Directory.Delete(sibling, true); }
    }
}
