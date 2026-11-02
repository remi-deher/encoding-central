namespace Encoding.Contracts.Libraries;

public sealed record CreateLibraryRequest(string Name);

public sealed record LibraryResponse(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    int ConfiguredNodeCount = 0,
    int DirectAccessNodeCount = 0,
    int TransferNodeCount = 0,
    bool IsActive = true, string? FlowName = null, int? FlowVersion = null);

public sealed record UpdateLibraryRequest(string Name);

public sealed record SetLibraryActivationRequest(bool IsActive);

public enum LibraryNodeAccessMode
{
    Direct,
    Unsupported,
    Transfer
}

public sealed record LibraryNodeAccessRequest(
    Guid NodeId,
    LibraryNodeAccessMode Mode,
    string? LocalPath,
    bool CanRead,
    bool CanWrite,
    Guid? TransferSourceNodeId,
    bool IsEnabled = true);

public sealed record ConfigureLibraryNodeAccessRequest(IReadOnlyList<LibraryNodeAccessRequest> Nodes);

public sealed record SetLibraryNodeAccessActivationRequest(bool IsEnabled);

public sealed record LibraryNodeAccessResponse(
    Guid LibraryId,
    Guid NodeId,
    string NodeName,
    LibraryNodeAccessMode Mode,
    bool IsEnabled,
    string? LocalPath,
    bool CanRead,
    bool CanWrite,
    Guid? TransferSourceNodeId,
    string? TransferSourceNodeName,
    DateTimeOffset UpdatedAt);

/// <summary>Définitions de bibliothèques d'un cluster, exportées avant de le quitter (0039). Format 1 : définitions seules, sans chemins d'accès des nœuds.</summary>
public sealed record LibraryExport(int Format, DateTimeOffset ExportedAt, string ClusterName, IReadOnlyList<LibraryResponse> Libraries);

public sealed record LibraryOverviewResponse(
    Guid Id,
    string Name,
    bool IsActive,
    DateTimeOffset CreatedAt,
    string? FlowName,
    int? FlowVersion,
    Guid? FlowId,
    int ConfiguredNodeCount,
    int DirectAccessNodeCount,
    bool WatchEnabled,
    int ScanIntervalSeconds,
    int TotalMediaCount,
    int QueuedExecutionCount,
    int RunningExecutionCount,
    int CompletedExecutionCount,
    int FailedExecutionCount);

