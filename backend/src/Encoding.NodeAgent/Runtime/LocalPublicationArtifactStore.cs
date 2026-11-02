using System.Collections.Concurrent;

namespace Encoding.NodeAgent.Runtime;

/// <summary>In-process handoff from a worker to the publisher when both use this node.</summary>
public sealed class LocalPublicationArtifactStore(TimeProvider? time = null) : ILocalPublicationArtifactStore
{
    private sealed record Entry(long Epoch, string Sha256, string Path, DateTimeOffset ExpiresAt);
    private readonly ConcurrentDictionary<Guid, Entry> entries = new();
    private readonly TimeProvider clock = time ?? TimeProvider.System;

    public void Register(Guid executionId, long epoch, string sha256, string path)
    {
        Prune();
        entries[executionId] = new(epoch, sha256, Path.GetFullPath(path), clock.GetUtcNow().AddHours(6));
    }

    public bool TryGet(Guid executionId, long epoch, string sha256, out string path)
    {
        path = string.Empty;
        if (!entries.TryGetValue(executionId, out var entry)) return false;
        if (entry.ExpiresAt <= clock.GetUtcNow() || entry.Epoch != epoch || entry.Sha256 != sha256)
        {
            if (entry.ExpiresAt <= clock.GetUtcNow() || entry.Epoch != epoch) entries.TryRemove(executionId, out _);
            return false;
        }
        path = entry.Path;
        return File.Exists(path);
    }

    public void Remove(Guid executionId, long epoch)
    {
        if (entries.TryGetValue(executionId, out var entry) && entry.Epoch == epoch)
            entries.TryRemove(executionId, out _);
    }

    private void Prune()
    {
        var now = clock.GetUtcNow();
        foreach (var (executionId, entry) in entries)
            if (entry.ExpiresAt <= now) entries.TryRemove(executionId, out _);
        if (entries.Count <= 1024) return;
        foreach (var executionId in entries.Keys.Take(entries.Count - 1024)) entries.TryRemove(executionId, out _);
    }
}
