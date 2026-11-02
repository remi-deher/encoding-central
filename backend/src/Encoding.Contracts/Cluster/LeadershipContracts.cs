namespace Encoding.Contracts.Cluster;

/// <summary>
/// État de gouvernance du cluster : indique quel nœud est le Maître actif et les paramètres de sauvegarde miroir.
/// </summary>
public sealed record LeadershipStatusResponse(
    bool IsMaster,
    Guid? MasterNodeId,
    string? MasterNodeName,
    string? MasterUrl,
    bool IsDraining,
    DateTimeOffset? LastBackupAt,
    int? BackupIntervalMinutes);

/// <summary>
/// Demande d'un administrateur pour transférer le rôle de Maître vers un Worker cible (Cas A : Passation planifiée).
/// </summary>
public sealed record LeadershipTransferRequest(
    Guid TargetNodeId,
    string? TargetNodeUrl = null,
    int DrainTimeoutSeconds = 30);

/// <summary>
/// Résultat d'une passation de pouvoir de Maître.
/// </summary>
public sealed record LeadershipTransferResponse(
    bool Success,
    string Message,
    string? NewMasterUrl = null);

/// <summary>
/// Notification de disponibilité d'une sauvegarde miroir de la base SQLite du Maître (Cas B : Sauvegarde périodique).
/// Le Worker passif télécharge le fichier et le stocke sans ouvrir de connexion SQLite.
/// </summary>
public sealed record ClusterDatabaseBackupNotification(
    string Digest,
    long SizeBytes,
    DateTimeOffset Timestamp,
    string DownloadPath);

/// <summary>
/// Ordre de prise de contrôle envoyé au Worker promu lors d'une passation planifiée.
/// </summary>
public sealed record LeadershipHandoffInstruction(
    Guid TransferId,
    string SnapshotDownloadPath,
    string Digest,
    DateTimeOffset Timestamp);

/// <summary>
/// Demande de promotion d'urgence déclenchée sur un Worker à partir du dernier snapshot disponible (Cas B : Reprise après sinistre).
/// </summary>
public sealed record EmergencyPromotionRequest(
    bool Force = false);

/// <summary>
/// Résultat de la promotion d'urgence sur un Worker.
/// </summary>
public sealed record EmergencyPromotionResponse(
    bool Success,
    string Message,
    Guid? ClusterId = null,
    DateTimeOffset? BackupTimestamp = null);

/// <summary>
/// Trousseau de clés ASP.NET DataProtection exporté pour transmission sécurisée au nœud promu ou standby.
/// </summary>
public sealed record LeadershipKeyBundle(IReadOnlyList<LeadershipKeyEntry> Keys);

/// <summary>
/// Fichier de clé XML de protection de données.
/// </summary>
public sealed record LeadershipKeyEntry(string FileName, string Content);

