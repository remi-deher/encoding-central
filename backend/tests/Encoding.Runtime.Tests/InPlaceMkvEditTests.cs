using System.Text.Json;
using Encoding.NodeAgent.Runtime;
using Encoding.NodeAgent.Runtime.Media;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>Modification MKV sur place (architecture 0002) sur de vrais MKV synthétiques.</summary>
public sealed class InPlaceMkvEditTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "encoding-in-place-" + Guid.NewGuid().ToString("N"));
    private readonly ProcessToolRunner tools = new();
    private readonly Guid execution = Guid.NewGuid();
    private string Locks => Path.Combine(root, ".locks");
    public InPlaceMkvEditTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    private async Task<(string Path,string Sha)> CreateAsync()
    {
        var path = Path.Combine(root, "episode.mkv");
        await tools.CheckedAsync("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=duration=1:size=64x64:rate=5", "-f", "lavfi", "-i", "sine=duration=1:sample_rate=48000",
            "-c:v", "libx264", "-c:a", "aac", "-metadata:s:a:0", "language=eng", path], root, CancellationToken.None);
        return (path, await SafeMediaPaths.DigestAsync(path, CancellationToken.None));
    }

    private static InPlaceTrackEdit Edit() => new(1, "fra", "Français / AAC", true, false, "Épisode 1");

    private Task<InPlaceEditResult> ApplyAsync(string sha, InPlaceTrackEdit edit, Func<JsonElement, string?>? verify = null, bool keepBackup = false, Func<bool>? authorized = null, Func<CancellationToken, Task<string?>>? beforeWrite = null) =>
        new InPlaceMkvEditor(tools).ApplyAsync(root, Locks, execution, "episode.mkv", sha, edit.Arguments, edit.AlreadyApplied, verify ?? edit.Verify, root, keepBackup, authorized ?? (() => true), CancellationToken.None, beforeWrite);

    private async Task<JsonElement> IdentifyAsync() => JsonDocument.Parse(await tools.CheckedAsync("mkvmerge", ["-J", Path.Combine(root, "episode.mkv")], root, CancellationToken.None)).RootElement;

    [MkvToolsFact]
    public async Task AppliesPropertiesInPlaceAndRemovesTheBackup()
    {
        var (path, sha) = await CreateAsync();
        var result = await ApplyAsync(sha, Edit());
        Assert.Equal("edited", result.Status);
        Assert.NotEqual(sha, result.NewSha256);
        Assert.Equal(result.NewSha256, await SafeMediaPaths.DigestAsync(path, CancellationToken.None));
        var identification = await IdentifyAsync();
        var audio = identification.GetProperty("tracks").EnumerateArray().Single(t => t.GetProperty("id").GetInt32() == 1).GetProperty("properties");
        Assert.Equal("Français / AAC", audio.GetProperty("track_name").GetString());
        Assert.True(audio.GetProperty("default_track").GetBoolean());
        Assert.Equal("Épisode 1", identification.GetProperty("container").GetProperty("properties").GetProperty("title").GetString());
        Assert.False(Directory.Exists(InPlaceMkvEditor.BackupDirectory(root, execution)));
    }

    [MkvToolsFact]
    public async Task KeepsTheBackupWhenRequested()
    {
        var (_, sha) = await CreateAsync();
        await ApplyAsync(sha, Edit(), keepBackup: true);
        var backup = Path.Combine(InPlaceMkvEditor.BackupDirectory(root, execution), InPlaceMkvEditor.BackupName);
        Assert.Equal(sha, await SafeMediaPaths.DigestAsync(backup, CancellationToken.None));
    }

    [MkvToolsFact]
    public async Task WritesNothingWhenPropertiesAreAlreadyInPlace()
    {
        var (path, sha) = await CreateAsync();
        var first = await ApplyAsync(sha, Edit());
        var modified = File.GetLastWriteTimeUtc(path);
        var second = await ApplyAsync(first.NewSha256!, Edit());
        Assert.Equal("unchanged", second.Status);
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
        Assert.False(Directory.Exists(InPlaceMkvEditor.BackupDirectory(root, execution)));
    }

    [MkvToolsFact]
    public async Task RestoresTheOriginalFileWhenTheVerificationFails()
    {
        var (path, sha) = await CreateAsync();
        var exception = await Assert.ThrowsAsync<InPlaceEditException>(() => ApplyAsync(sha, Edit(), _ => "Propriété refusée par le contrôle."));
        Assert.Contains("restauré", exception.Message);
        Assert.Equal(sha, await SafeMediaPaths.DigestAsync(path, CancellationToken.None));
    }

    [MkvToolsFact]
    public async Task RestoresTheOriginalFileWhenMkvpropeditFails()
    {
        var (path, sha) = await CreateAsync();
        var edit = Edit() with { TrackId = 9 };
        await Assert.ThrowsAsync<InPlaceEditException>(() => ApplyAsync(sha, edit));
        Assert.Equal(sha, await SafeMediaPaths.DigestAsync(path, CancellationToken.None));
    }

    [MkvToolsFact]
    public async Task RefusesAChangedSourceAndALostLease()
    {
        var (path, sha) = await CreateAsync();
        await Assert.ThrowsAsync<InPlaceEditException>(() => ApplyAsync(new string('0', 64), Edit()));
        await Assert.ThrowsAsync<InPlaceEditException>(() => ApplyAsync(sha, Edit(), authorized: () => false));
        Assert.Equal(sha, await SafeMediaPaths.DigestAsync(path, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(InPlaceMkvEditor.BackupDirectory(root, execution), InPlaceMkvEditor.BackupName)));
    }

    [MkvToolsFact]
    public async Task RefusesWhenTheLastCheckBeforeWritingSeesAPlayback()
    {
        var (path, sha) = await CreateAsync();
        var error = await Assert.ThrowsAsync<InPlaceEditException>(() => ApplyAsync(sha, Edit(), beforeWrite: _ => Task.FromResult<string?>("Lecture en cours.")));
        Assert.Contains("Lecture en cours", error.Message);
        Assert.Equal(sha, await SafeMediaPaths.DigestAsync(path, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(InPlaceMkvEditor.BackupDirectory(root, execution), InPlaceMkvEditor.BackupName)));
        Assert.Equal("edited", (await ApplyAsync(sha, Edit(), beforeWrite: _ => Task.FromResult<string?>(null))).Status);
    }

    [MkvToolsFact]
    public async Task RefusesAMediaUsedByAnotherProcessing()
    {
        var (path, sha) = await CreateAsync();
        using var held = SafeMediaPaths.Lock(SafeMediaPaths.Resolve(root, "episode.mkv"), Locks);
        await Assert.ThrowsAsync<InPlaceEditException>(() => ApplyAsync(sha, Edit()));
        Assert.Equal(sha, await SafeMediaPaths.DigestAsync(path, CancellationToken.None));
    }
}
