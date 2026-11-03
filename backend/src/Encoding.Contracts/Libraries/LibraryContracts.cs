namespace Encoding.Contracts.Libraries;

public sealed record CreateLibraryRequest(string Name);

public sealed record LibraryResponse(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    int ConfiguredWorkerCount = 0,
    int DirectAccessWorkerCount = 0,
    int TransferWorkerCount = 0,
    bool IsActive = true, string? FlowName = null, int? FlowVersion = null);

public sealed record UpdateLibraryRequest(string Name);

public sealed record SetLibraryActivationRequest(bool IsActive);

public enum LibraryWorkerAccessMode
{
    Direct,
    Unsupported,
    Transfer
}

public sealed record LibraryWorkerAccessRequest(
    Guid WorkerId,
    LibraryWorkerAccessMode Mode,
    string? LocalPath,
    bool CanRead,
    bool CanWrite,
    Guid? TransferSourceWorkerId,
    bool IsEnabled = true);

public sealed record ConfigureLibraryWorkerAccessRequest(IReadOnlyList<LibraryWorkerAccessRequest> Workers);

public sealed record SetLibraryWorkerAccessActivationRequest(bool IsEnabled);

public sealed record LibraryWorkerAccessResponse(
    Guid LibraryId,
    Guid WorkerId,
    string WorkerName,
    LibraryWorkerAccessMode Mode,
    bool IsEnabled,
    string? LocalPath,
    bool CanRead,
    bool CanWrite,
    Guid? TransferSourceWorkerId,
    string? TransferSourceWorkerName,
    DateTimeOffset UpdatedAt);

/// <summary>Définitions de bibliothèques d'un central, exportées avant de le quitter (0039). Format 1 : définitions seules, sans chemins d'accès des workers.</summary>
public sealed record LibraryExport(int Format, DateTimeOffset ExportedAt, string CentralName, IReadOnlyList<LibraryResponse> Libraries);

public sealed record LibraryOverviewResponse(
    Guid Id,
    string Name,
    bool IsActive,
    DateTimeOffset CreatedAt,
    string? FlowName,
    int? FlowVersion,
    Guid? FlowId,
    int ConfiguredWorkerCount,
    int DirectAccessWorkerCount,
    bool WatchEnabled,
    int ScanIntervalSeconds,
    int TotalMediaCount,
    int QueuedExecutionCount,
    int RunningExecutionCount,
    int CompletedExecutionCount,
    int FailedExecutionCount);

