namespace Encoding.Contracts.Nodes;

public sealed record WorkerDirectoryBrowserRequest(string? Path);
public sealed record WorkerDirectoryBrowserEntry(string Name, string Path);
public sealed record WorkerDirectoryBrowseClaim(Guid Id, string Path);
public sealed record WorkerDirectoryBrowseCompletion(
    IReadOnlyList<WorkerDirectoryBrowserEntry>? Entries,
    bool HasMore,
    string? Error);
public sealed record WorkerDirectoryBrowserResponse(
    Guid Id,
    Guid NodeId,
    string Path,
    string State,
    IReadOnlyList<WorkerDirectoryBrowserEntry> Entries,
    bool HasMore,
    string? Error);
