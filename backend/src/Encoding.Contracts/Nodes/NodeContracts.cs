
namespace Encoding.Contracts.Nodes;

public enum NodeRole
{
    Server,
    Worker
}

public sealed record WorkerHeartbeatRequest(
    string Name,
    IReadOnlyList<NodeRole> Roles,
    int MaxConcurrentTasks,
    IReadOnlyList<WorkerToolCapability> Capabilities,
    WorkerInventory? Inventory = null,
    string? Version = null,
    string? SessionNonce = null);

/// <summary>
/// Version de l'application. Un worker ne fonctionne que s'il a exactement la version du central (qui embarque son propre worker) :
/// aucune plage de compatibilité. Une version différente ou absente le rend incompatible : il ne reçoit aucune tâche et ne peut pas s'enrôler.
/// La version est fixée à la construction de l'image (<c>APP_VERSION</c>) ; sans elle, elle vaut <c>0.0.0-dev</c>.
/// </summary>
public static class AppVersion
{
    public static string Current { get; } = typeof(AppVersion).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0-dev";

    public static string? MismatchReason(string? workerVersion, string? centralVersion = null)
    {
        var central = centralVersion ?? Current;
        if (string.IsNullOrWhiteSpace(workerVersion)) return $"version du worker inconnue, le central est en {central} : mettez le worker à jour";
        return string.Equals(workerVersion, central, StringComparison.Ordinal)
            ? null
            : $"version {workerVersion} différente de celle du central ({central}) : mettez le worker à jour";
    }
}

public sealed record WorkerInventory(
    string OperatingSystem,
    string Architecture,
    int ProcessorCount,
    long MemoryMb,
    string? ProcessorModel = null,
    IReadOnlyList<WorkerGpu>? Gpus = null,
    IReadOnlyList<string>? FontSets = null,
    string? RuntimeEnvironment = null);

/// <summary>Processeur graphique déclaré par l'agent ; informations matérielles statiques, jamais une mesure de charge.</summary>
public sealed record WorkerGpu(string Name, string? Vendor = null, string? Driver = null, string? Device = null);

public sealed record WorkerToolCapability(
    string ToolId,
    string Version,
    IReadOnlyList<string> MediaTypes,
    IReadOnlyList<string> AccelerationTypes,
    IReadOnlyList<string>? Encoders = null,
    IReadOnlyList<WorkerEncoderCapability>? EncoderChecks = null,
    string? ManifestFingerprint = null,
    string? ToolPackageSha256 = null);

/// <summary>Result of a safe synthetic encode check for a catalogued video encoder.</summary>
public sealed record WorkerEncoderCapability(string EncoderId, string Status);

public enum WorkerPresenceStatus
{
    Online,
    Offline
}

/// <summary>État de cycle de vie affiché d'un worker (architecture 0041). Le plus restrictif l'emporte : quarantaine, incompatible, vidange, puis présence.</summary>
public enum WorkerLifecycle
{
    Online,
    Offline,
    Draining,
    Incompatible,
    Quarantined
}

public sealed record DrainWorkerRequest(bool Draining);

public sealed record WorkerResponse(
    Guid Id,
    string Name,
    IReadOnlyList<NodeRole> Roles,
    int MaxConcurrentTasks,
    IReadOnlyList<WorkerToolCapability> Capabilities,
    WorkerPresenceStatus Status,
    DateTimeOffset LastSeenAt,
    WorkerInventory? Inventory = null,
    string? DisplayName = null,
    IReadOnlyDictionary<string, string?>? Labels = null,
    WorkerLifecycle? Lifecycle = null,
    string? LifecycleReason = null,
    string? MasterUrl = null);

/// <summary>Demande d'un administrateur : mode de fonctionnement voulu pour un worker, appliqué par le worker à son prochain signal de présence (architecture 0036).</summary>

public sealed record WorkerEnrollmentRequest(
    Guid Id,
    string Name,
    IReadOnlyList<NodeRole> Roles,
    int MaxConcurrentTasks,
    IReadOnlyList<WorkerToolCapability> Capabilities,
    string NodeToken,
    WorkerInventory? Inventory = null,
    string? DisplayName = null,
    string? Version = null);

public sealed record CreateWorkerEnrollmentRequest(WorkerEnrollmentRequest Node, string PairingKey);

public sealed record ApproveWorkerEnrollmentRequest(string PairingKey);

public sealed record PendingWorkerEnrollmentResponse(
    Guid Id,
    string Name,
    IReadOnlyList<NodeRole> Roles,
    int MaxConcurrentTasks,
    IReadOnlyList<WorkerToolCapability> Capabilities,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    WorkerInventory? Inventory = null,
    string? VerificationCode = null,
    string? DisplayName = null);

public sealed record WorkerActivityResponse(int Active,int Succeeded,int Failed,int Cancelled,DateTimeOffset? LastActivity,int SourceTasks);

public sealed record WorkerProcessingSettings(int? MaxConcurrentTasks,int EffectiveMaxConcurrentTasks,int Revision,WorkerSchedule Schedule);
public sealed record SaveWorkerProcessingSettings(int? MaxConcurrentTasks,int ExpectedRevision,WorkerSchedule? Schedule=null);
public sealed record RuntimeCapacity(int MaxConcurrentTasks);
