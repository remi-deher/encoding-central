using System.Data.Common;
using System.Security.Cryptography;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Nodes;
using Encoding.Contracts.Cluster;
using Encoding.Contracts.Nodes;
using Encoding.Core.Nodes;

namespace Encoding.Api.Cluster;

/// <summary>
/// Service d'orchestration de passation de pouvoir (Cas A) et de sauvegardes miroir périodiques (Cas B).
/// </summary>
public sealed class LeadershipHandoffService(
    DbDataSource dataSource,
    LeadershipRepository repository,
    NodeRepository nodeRepository,
    LocalNodeRegistry localNodeRegistry,
    IConfiguration configuration,
    ILogger<LeadershipHandoffService> logger,
    LauncherControlClient? launcher = null)
{
    private readonly string dataDirectory = Path.GetFullPath(configuration["Node:DataDirectory"] ?? "./data");

    public string DataDirectory => dataDirectory;

    /// <summary>
    /// Crée un instantané SQLite atomique et cohérent sans verrou d'écriture bloquant (commande native VACUUM INTO).
    /// </summary>
    public async Task<(string Digest, long SizeBytes)> CreateSnapshotAsync(string destinationPath, CancellationToken ct)
    {
        var targetDir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(targetDir))
            Directory.CreateDirectory(targetDir);

        if (File.Exists(destinationPath))
            File.Delete(destinationPath);

        await using (var connection = await dataSource.OpenConnectionAsync(ct))
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "VACUUM INTO @destination";
            var param = cmd.CreateParameter();
            param.ParameterName = "destination";
            param.Value = destinationPath;
            cmd.Parameters.Add(param);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var fileInfo = new FileInfo(destinationPath);
        if (!fileInfo.Exists || fileInfo.Length == 0)
            throw new InvalidOperationException($"L'instantané SQLite n'a pas pu être créé à l'emplacement {destinationPath}.");

        string digest;
        await using (var stream = File.OpenRead(destinationPath))
        {
            var hashBytes = await SHA256.HashDataAsync(stream, ct);
            digest = Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        return (digest, fileInfo.Length);
    }

    /// <summary>
    /// Cas A : Déclenche la passation de pouvoir planifiée vers un worker désigné.
    /// Gèle les nouvelles attributions, génère le snapshot atomique et prépare le téléchargement.
    /// </summary>
    public async Task<LeadershipTransferResponse> InitiateTransferAsync(Guid targetNodeId, int drainTimeoutSeconds, string? targetUrl, CancellationToken ct)
    {
        var target = await nodeRepository.FindByIdAsync(targetNodeId, ct);
        if (target is null)
            return new LeadershipTransferResponse(false, "Nœud cible introuvable.");

        if (target.Status != NodePresenceStatus.Online)
            return new LeadershipTransferResponse(false, "Le nœud cible doit être en ligne pour recevoir le rôle de Maître.");

        if (target.Lifecycle is NodeLifecycle.Quarantined or NodeLifecycle.Incompatible)
            return new LeadershipTransferResponse(false, $"Le nœud cible ne peut pas devenir Maître (état: {target.Lifecycle}).");

        var transferTimeout = TimeSpan.FromSeconds(Math.Max(drainTimeoutSeconds, 30) + 60);
        var transferId = await repository.CreateTransferAsync(targetNodeId, transferTimeout, ct);

        // Gèle temporairement l'attribution de nouvelles tâches sur le maître actuel
        var localNodeId = localNodeRegistry.NodeId;
        if (localNodeId.HasValue)
            await nodeRepository.SetDrainingAsync(localNodeId.Value, true, ct);

        var snapshotDir = Path.Combine(dataDirectory, "transfers");
        CleanupStaleTransfers(snapshotDir);
        var snapshotPath = Path.Combine(snapshotDir, $"transfer-{transferId:N}.db");

        logger.LogInformation("Création de l'instantané SQLite pour le transfert de gouvernance {TransferId} vers {TargetNodeName} ({TargetNodeId})...",
            transferId, target.Name, targetNodeId);

        var (digest, sizeBytes) = await CreateSnapshotAsync(snapshotPath, ct);

        await repository.UpdateTransferSnapshotAsync(transferId, snapshotPath, digest, sizeBytes, ct);

        if (!string.IsNullOrWhiteSpace(targetUrl))
            await repository.SetMasterUrlAsync(targetUrl, ct);

        logger.LogInformation("Instantané SQLite {TransferId} prêt ({SizeBytes} octets, sha256: {Digest}). En attente de récupération par le worker.",
            transferId, sizeBytes, digest);

        return new LeadershipTransferResponse(true, "Passation de pouvoir initiée. L'instantané est prêt pour le worker.", targetUrl);
    }

    private void CleanupStaleTransfers(string snapshotDir)
    {
        try
        {
            if (!Directory.Exists(snapshotDir)) return;
            var cutoff = DateTimeOffset.UtcNow.AddHours(-1);
            foreach (var file in Directory.EnumerateFiles(snapshotDir, "transfer-*.db"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime)
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Erreur non bloquante lors du nettoyage des anciens transferts.");
        }
    }

    /// <summary>
    /// Appelée lorsque le worker promu a téléchargé et validé l'instantané de base de données.
    /// L'ancien maître valide le transfert et rétrograde son mode local en worker pur.
    /// </summary>
    public async Task<bool> AcknowledgeAdoptionAsync(Guid transferId, CancellationToken ct)
    {
        var record = await repository.GetTransferByIdAsync(transferId, ct);
        if (record is null) return false;

        logger.LogInformation("Le worker {TargetNodeId} a confirmé l'adoption de la base. Finalisation du transfert {TransferId}...",
            record.TargetNodeId, transferId);

        await repository.CompleteTransferAsync(transferId, ct);

        // Suppression différée sécurisée du snapshot temporaire (pour tolérer les retentatives et requêtes trailing)
        if (!string.IsNullOrEmpty(record.SnapshotPath) && File.Exists(record.SnapshotPath))
        {
            var snapshotPath = record.SnapshotPath;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(5));
                    if (File.Exists(snapshotPath)) File.Delete(snapshotPath);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Impossible de supprimer le snapshot temporaire de transfert après délai de grâce.");
                }
            });
        }

        // Rétrogradation de l'ancien maître en worker pur via le Launcher
        if (launcher is not null)
        {
            try
            {
                logger.LogInformation("Rétrogradation de ce nœud en Worker pur (server=false, worker=true)...");
                await launcher.SetAsync(new NodeMode(Server: false, Worker: true), ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Échec de la rétrogradation automatique via le lanceur.");
            }
        }

        return true;
    }

    /// <summary>
    /// Cas B : Crée une sauvegarde miroir périodique de la base de données.
    /// </summary>
    public async Task<DatabaseBackupRecord> CreatePeriodicBackupAsync(CancellationToken ct)
    {
        var backupDir = Path.Combine(dataDirectory, "backups");
        var timestamp = DateTimeOffset.UtcNow;
        var backupPath = Path.Combine(backupDir, $"backup-{timestamp:yyyyMMdd-HHmmss}.db");

        logger.LogInformation("Génération de la sauvegarde miroir périodique SQLite vers {Path}...", backupPath);
        var (digest, sizeBytes) = await CreateSnapshotAsync(backupPath, ct);

        await repository.SaveBackupAsync(digest, sizeBytes, backupPath, ct);

        // Conservation des 5 dernières sauvegardes et nettoyage des plus anciennes
        var prunedPaths = await repository.PruneOldBackupsAsync(keepCount: 5, ct);
        foreach (var path in prunedPaths)
        {
            if (File.Exists(path))
            {
                try { File.Delete(path); }
                catch (Exception ex) { logger.LogDebug(ex, "Impossible de purger l'ancienne sauvegarde {Path}.", path); }
            }
        }

        logger.LogInformation("Sauvegarde miroir périodique SQLite terminée ({SizeBytes} octets, sha256: {Digest}).", sizeBytes, digest);
        return new DatabaseBackupRecord(Guid.NewGuid(), digest, sizeBytes, backupPath, timestamp);
    }

    /// <summary>
    /// Exporte l'ensemble des clés XML DataProtection locales pour synchronisation sécurisée vers un nœud promu ou récepteur miroir.
    /// </summary>
    public LeadershipKeyBundle GetKeyBundle()
    {
        var keyDir = ClusterPeerIdentityStore.ResolveKeyDirectory(configuration);
        if (!Directory.Exists(keyDir))
            return new LeadershipKeyBundle([]);

        var files = Directory.GetFiles(keyDir, "key-*.xml");
        var entries = new List<LeadershipKeyEntry>();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var content = File.ReadAllText(file);
            entries.Add(new LeadershipKeyEntry(name, content));
        }
        return new LeadershipKeyBundle(entries);
    }
}
