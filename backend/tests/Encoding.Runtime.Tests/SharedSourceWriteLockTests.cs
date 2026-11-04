using System.Diagnostics;
using System.Text.Json;
using Encoding.WorkerAgent.Runtime;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>Verrou d'écriture de source : un verrou orphelin est détecté et signalé, jamais retiré silencieusement ni confondu avec un verrou vivant.</summary>
public sealed class SharedSourceWriteLockTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("encoding-write-lock-").FullName;
    private string Media => Path.Combine(root, "Film.mkv");
    private string LockFile => SharedSourceWriteLock.LockPath(Media);
    public void Dispose() => Directory.Delete(root, true);

    private void WriteLock(string owner, string host, int pid, long startTicks) =>
        File.WriteAllText(LockFile, JsonSerializer.Serialize(new SourceWriteLockInfo(owner, host, pid, startTicks, Guid.NewGuid(), DateTimeOffset.UtcNow)));
    private static long CurrentStartTicks() { using var process = Process.GetCurrentProcess(); return process.StartTime.ToUniversalTime().Ticks; }

    [Fact]
    public void AcquireIsSeenAsLiveAndDisposeRemovesTheLock()
    {
        var held = SharedSourceWriteLock.Acquire(Media, Guid.NewGuid());

        var status = SharedSourceWriteLock.Inspect(Media);
        Assert.Equal(SourceWriteLockState.Held, status.State);
        held.Dispose();
        Assert.False(File.Exists(LockFile));
        Assert.Equal(SourceWriteLockState.Free, SharedSourceWriteLock.Inspect(Media).State);
    }

    [Fact]
    public void SecondAcquireOfALiveLockIsBusyAndNotOrphan()
    {
        using var held = SharedSourceWriteLock.Acquire(Media);

        var exception = Assert.Throws<SafeMediaPaths.MediaLockBusyException>(() => SharedSourceWriteLock.Acquire(Media));

        Assert.IsNotType<OrphanSourceWriteLockException>(exception);
    }

    [Fact]
    public void OwnerProcessGoneOnThisHostIsReportedOrphanAndNeverRemovedByAcquire()
    {
        WriteLock("dead", Environment.MachineName, int.MaxValue, 1);

        Assert.Equal(SourceWriteLockState.Orphaned, SharedSourceWriteLock.Inspect(Media).State);
        var exception = Assert.Throws<OrphanSourceWriteLockException>(() => SharedSourceWriteLock.Acquire(Media));

        Assert.Equal(SourceWriteLockState.Orphaned, exception.Status.State);
        Assert.Contains("orphelin", exception.Message);
        Assert.True(File.Exists(LockFile));
    }

    [Fact]
    public void ReusedProcessIdWithDifferentStartTimeIsOrphan()
    {
        WriteLock("old", Environment.MachineName, Environment.ProcessId, CurrentStartTicks() - TimeSpan.FromDays(3).Ticks);

        Assert.Equal(SourceWriteLockState.Orphaned, SharedSourceWriteLock.Inspect(Media).State);
    }

    [Fact]
    public void LiveOwnerProcessOnThisHostIsNotOrphan()
    {
        WriteLock("me", Environment.MachineName, Environment.ProcessId, CurrentStartTicks());

        Assert.Equal(SourceWriteLockState.Held, SharedSourceWriteLock.Inspect(Media).State);
        Assert.IsNotType<OrphanSourceWriteLockException>(Assert.Throws<SafeMediaPaths.MediaLockBusyException>(() => SharedSourceWriteLock.Acquire(Media)));
    }

    [Fact]
    public void LockFromAnotherHostOrUnknownFormatCannotBeVerifiedAndIsRespected()
    {
        WriteLock("remote", "another-host", int.MaxValue, 1);
        Assert.Equal(SourceWriteLockState.Unverifiable, SharedSourceWriteLock.Inspect(Media).State);
        Assert.IsNotType<OrphanSourceWriteLockException>(Assert.Throws<SafeMediaPaths.MediaLockBusyException>(() => SharedSourceWriteLock.Acquire(Media)));

        File.WriteAllText(LockFile, "0123456789abcdef"); // ancien format : identifiant brut
        Assert.Equal(SourceWriteLockState.Unverifiable, SharedSourceWriteLock.Inspect(Media).State);
        Assert.False(SharedSourceWriteLock.ReleaseOrphan(Media, "0123456789abcdef"));
        Assert.True(File.Exists(LockFile));
    }

    [Fact]
    public void ReleaseOrphanRequiresAProvenOrphanAndTheMatchingOwner()
    {
        WriteLock("dead", Environment.MachineName, int.MaxValue, 1);
        Assert.False(SharedSourceWriteLock.ReleaseOrphan(Media, "someone-else"));
        Assert.True(File.Exists(LockFile));
        Assert.True(SharedSourceWriteLock.ReleaseOrphan(Media, "dead"));
        Assert.False(File.Exists(LockFile));

        WriteLock("me", Environment.MachineName, Environment.ProcessId, CurrentStartTicks());
        Assert.False(SharedSourceWriteLock.ReleaseOrphan(Media, "me"));
        Assert.True(File.Exists(LockFile));
    }

    [Fact]
    public void DisposeDoesNotDeleteALockNowOwnedBySomeoneElse()
    {
        var held = SharedSourceWriteLock.Acquire(Media);
        held.Dispose();
        WriteLock("other", "another-host", 1, 1);

        held.Dispose();

        Assert.True(File.Exists(LockFile));
    }

    [Fact]
    public void InspectNeverModifiesAnything()
    {
        Assert.Equal(SourceWriteLockState.Free, SharedSourceWriteLock.Inspect(Media).State);
        Assert.False(File.Exists(LockFile));
    }

    private void WriteUnreadable(string content, TimeSpan age)
    {
        File.WriteAllText(LockFile, content);
        File.SetLastWriteTimeUtc(LockFile, DateTime.UtcNow - age);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"Owner\":\"ab")]
    public void OldUnlockedEmptyOrTruncatedLockIsProbablyOrphanButNeverReleased(string content)
    {
        WriteUnreadable(content, SharedSourceWriteLock.MinimumUnreadableAge + TimeSpan.FromMinutes(1));

        Assert.Equal(SourceWriteLockState.ProbablyOrphaned, SharedSourceWriteLock.Inspect(Media).State);
        Assert.Throws<OrphanSourceWriteLockException>(() => SharedSourceWriteLock.Acquire(Media));
        Assert.False(SharedSourceWriteLock.ReleaseOrphan(Media, ""));
        Assert.True(File.Exists(LockFile));
    }

    [Fact]
    public void RecentEmptyLockIsNotClassifiedOrphan()
    {
        WriteUnreadable("", TimeSpan.Zero);

        Assert.Equal(SourceWriteLockState.Unverifiable, SharedSourceWriteLock.Inspect(Media).State);
    }

    [Fact]
    public void OldEmptyLockStillOpenedByItsOwnerIsHeld()
    {
        WriteUnreadable("", TimeSpan.FromHours(1));
        using var owner = new FileStream(LockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal(SourceWriteLockState.Held, SharedSourceWriteLock.Inspect(Media).State);
    }

    [NonRootUnixFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void UnreadableLockIsUnverifiableWithoutThrowing()
    {
        WriteLock("x", Environment.MachineName, int.MaxValue, 1);
        File.SetUnixFileMode(LockFile, UnixFileMode.None);

        Assert.Equal(SourceWriteLockState.Unverifiable, SharedSourceWriteLock.Inspect(Media).State);
    }
}
