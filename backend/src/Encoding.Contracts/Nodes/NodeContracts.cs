using Encoding.Contracts.Cluster;

namespace Encoding.Contracts.Nodes;

public enum NodeRole
{
    Server,
    Worker
}

public sealed record NodeHeartbeatRequest(
    string Name,
    IReadOnlyList<NodeRole> Roles,
    int MaxConcurrentTasks,
    IReadOnlyList<NodeToolCapability> Capabilities,
    NodeInventory? Inventory = null,
    int? ProtocolVersion = null,
    string? SessionNonce = null);

/// <summary>
/// Version du protocole entre serveur et worker (heartbeat, invocation, contrats partagés), architecture 0041. Un nœud hors de la plage
/// [<see cref="Minimum"/>, <see cref="Current"/>] est signalé incompatible et ne reçoit plus de tâche. Un agent qui n'envoie rien compte pour 0.
/// </summary>
public static class ClusterProtocol
{
    public const int Current = 1;
    public const int Minimum = 0;

    public static string? IncompatibilityReason(int? version)
    {
        var value = version ?? 0;
        if (value < Minimum) return $"protocole {value} inférieur au minimum {Minimum} de ce cluster : mise à jour du nœud requise";
        if (value > Current) return $"protocole {value} plus récent que celui de ce cluster ({Current}) : mettez d'abord à jour les serveurs";
        return null;
    }
}

public sealed record NodeInventory(
    string OperatingSystem,
    string Architecture,
    int ProcessorCount,
    long MemoryMb,
    string? ProcessorModel = null,
    IReadOnlyList<NodeGpu>? Gpus = null,
    IReadOnlyList<string>? FontSets = null,
    string? RuntimeEnvironment = null);

/// <summary>Processeur graphique déclaré par l'agent ; informations matérielles statiques, jamais une mesure de charge.</summary>
public sealed record NodeGpu(string Name, string? Vendor = null, string? Driver = null, string? Device = null);

public sealed record NodeToolCapability(
    string ToolId,
    string Version,
    IReadOnlyList<string> MediaTypes,
    IReadOnlyList<string> AccelerationTypes,
    IReadOnlyList<string>? Encoders = null,
    IReadOnlyList<NodeEncoderCapability>? EncoderChecks = null,
    string? ManifestFingerprint = null,
    string? ToolPackageSha256 = null);

/// <summary>Result of a safe synthetic encode check for a catalogued video encoder.</summary>
public sealed record NodeEncoderCapability(string EncoderId, string Status);

public enum NodePresenceStatus
{
    Online,
    Offline
}

/// <summary>État de cycle de vie affiché d'un nœud (architecture 0041). Le plus restrictif l'emporte : quarantaine, incompatible, vidange, puis présence.</summary>
public enum NodeLifecycle
{
    Online,
    Offline,
    Draining,
    Incompatible,
    Quarantined
}

public sealed record DrainNodeRequest(bool Draining);

public sealed record NodeResponse(
    Guid Id,
    string Name,
    IReadOnlyList<NodeRole> Roles,
    int MaxConcurrentTasks,
    IReadOnlyList<NodeToolCapability> Capabilities,
    NodePresenceStatus Status,
    DateTimeOffset LastSeenAt,
    NodeInventory? Inventory = null,
    string? DisplayName = null,
    IReadOnlyDictionary<string, string?>? Labels = null,
    IReadOnlyList<NodeRole>? DesiredRoles = null,
    NodeLifecycle? Lifecycle = null,
    string? LifecycleReason = null,
    ClusterJoinBootstrap? ClusterBootstrap = null,
    IReadOnlyList<string>? ClusterServerUrls = null,
    ClusterDatabaseBackupNotification? LatestBackup = null,
    LeadershipHandoffInstruction? LeadershipHandoff = null,
    string? MasterUrl = null);

/// <summary>Demande d'un administrateur : mode de fonctionnement voulu pour un nœud, appliqué par le nœud à son prochain signal de présence (architecture 0036).</summary>
public sealed record ChangeNodeModeRequest(bool Server, bool Worker);

public sealed record NodeEnrollmentRequest(
    Guid Id,
    string Name,
    IReadOnlyList<NodeRole> Roles,
    int MaxConcurrentTasks,
    IReadOnlyList<NodeToolCapability> Capabilities,
    string NodeToken,
    NodeInventory? Inventory = null,
    Guid? ClusterPeerId = null,
    string? ClusterPeerPublicKey = null,
    string? DisplayName = null,
    string? ClusterPeerReconciliationUrl = null);

public sealed record CreateNodeEnrollmentRequest(NodeEnrollmentRequest Node, string PairingKey);

public sealed record ApproveNodeEnrollmentRequest(string PairingKey);

public sealed record PendingNodeEnrollmentResponse(
    Guid Id,
    string Name,
    IReadOnlyList<NodeRole> Roles,
    int MaxConcurrentTasks,
    IReadOnlyList<NodeToolCapability> Capabilities,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    NodeInventory? Inventory = null,
    string? VerificationCode = null,
    string? DisplayName = null);

public sealed record NodeActivityResponse(int Active,int Succeeded,int Failed,int Cancelled,DateTimeOffset? LastActivity,int SourceTasks);

public sealed record NodeProcessingSettings(int? MaxConcurrentTasks,int EffectiveMaxConcurrentTasks,int Revision,NodeSchedule Schedule);
public sealed record SaveNodeProcessingSettings(int? MaxConcurrentTasks,int ExpectedRevision,NodeSchedule? Schedule=null);
public sealed record RuntimeCapacity(int MaxConcurrentTasks);
