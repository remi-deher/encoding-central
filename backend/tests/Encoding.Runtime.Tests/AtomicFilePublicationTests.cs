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
}
