namespace Encoding.Contracts.Nodes;

public sealed record NodeDirectoryBrowserRequest(string? Path);
public sealed record NodeDirectoryBrowserEntry(string Name, string Path);
public sealed record NodeDirectoryBrowseClaim(Guid Id, string Path);
public sealed record NodeDirectoryBrowseCompletion(
    IReadOnlyList<NodeDirectoryBrowserEntry>? Entries,
    bool HasMore,
    string? Error);
public sealed record NodeDirectoryBrowserResponse(
    Guid Id,
    Guid NodeId,
    string Path,
    string State,
    IReadOnlyList<NodeDirectoryBrowserEntry> Entries,
    bool HasMore,
    string? Error);
