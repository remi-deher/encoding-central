using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Contracts.Cluster;
using Encoding.Core.Nodes;

namespace Encoding.NodeAgent.Services;

/// <summary>
/// Service côté Worker pour la réception des sauvegardes miroir (Cas B), la passation planifiée (Cas A)
/// et la promotion d'urgence en tant que nouveau Maître.
/// </summary>
public sealed class LeadershipReceiverService(
    NodeAgentOptions options,
    ILogger<LeadershipReceiverService> logger,
    LauncherControlClient? launcher = null,
    HttpClient? httpClient = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient = httpClient ?? new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly SemaphoreSlim syncGate = new(1, 1);

    public string BackupDirectory => Path.Combine(options.DataDirectory, "backup");
    public string BackupFilePath => Path.Combine(BackupDirectory, "cluster-latest.db");
    public string BackupInfoFilePath => Path.Combine(BackupDirectory, "backup-info.json");
    public string DatabaseFilePath => Path.Combine(options.DataDirectory, "cluster.db");
    public string PluginKeysDirectory => Path.Combine(options.DataDirectory, "plugin-keys");

    public async Task<LocalBackupInfo?> GetLocalBackupInfoAsync(CancellationToken ct)
    {
        if (!File.Exists(BackupInfoFilePath)) return null;
        try
        {
            await using var stream = File.OpenRead(BackupInfoFilePath);
            return await JsonSerializer.DeserializeAsync<LocalBackupInfo>(stream, JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Impossible de lire le fichier de métadonnées de sauvegarde locale.");
            return null;
        }
    }

    /// <summary>
    /// Cas B : Réception en tâche de fond d'une sauvegarde miroir poussée par le Maître.
    /// Stockage passif sur disque sans aucune connexion SQLite ouverte.
    /// </summary>
    public async Task HandleBackupNotificationAsync(ClusterDatabaseBackupNotification notification, string masterUrl, string nodeToken, CancellationToken ct)
    {
        await syncGate.WaitAsync(ct);
        try
        {
            var existing = await GetLocalBackupInfoAsync(ct);
            if (existing is not null && string.Equals(existing.Digest, notification.Digest, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug("La sauvegarde miroir locale est déjà à jour (digest: {Digest}).", notification.Digest);
                return;
            }

            Directory.CreateDirectory(BackupDirectory);
            var tempPath = BackupFilePath + ".tmp";

            var downloadUri = new Uri(new Uri(masterUrl), notification.DownloadPath);
            logger.LogInformation("Téléchargement de la nouvelle sauvegarde miroir SQLite depuis {Uri} ({SizeBytes} octets)...", downloadUri, notification.SizeBytes);

            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUri);
            request.Headers.Add("X-Node-Token", nodeToken);

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await response.Content.CopyToAsync(fileStream, ct);
                await fileStream.FlushAsync(ct);
            }

            // Vérification de l'intégrité SHA-256
            string computedDigest;
            await using (var verifyStream = File.OpenRead(tempPath))
            {
                var hash = await SHA256.HashDataAsync(verifyStream, ct);
                computedDigest = Convert.ToHexString(hash).ToLowerInvariant();
            }

            if (!string.Equals(computedDigest, notification.Digest, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
                throw new InvalidOperationException($"Empreinte SHA-256 invalide pour la sauvegarde miroir (reçu: {computedDigest}, attendu: {notification.Digest}).");
            }

            File.Move(tempPath, BackupFilePath, overwrite: true);

            var info = new LocalBackupInfo(computedDigest, new FileInfo(BackupFilePath).Length, notification.Timestamp, DateTimeOffset.UtcNow);
            await using (var infoStream = File.Create(BackupInfoFilePath))
            {
                await JsonSerializer.SerializeAsync(infoStream, info, JsonOptions, ct);
            }

            logger.LogInformation("Sauvegarde miroir SQLite {Digest} ({SizeBytes} octets) enregistrée avec succès.", computedDigest, info.SizeBytes);

            // Synchroniser les clés DataProtection du Maître avec la sauvegarde
            await SyncKeysAsync(masterUrl, nodeToken, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Échec de la synchronisation de la sauvegarde miroir SQLite depuis le Maître.");
        }
        finally
        {
            syncGate.Release();
        }
    }

    /// <summary>
    /// Cas A : Réception de l'ordre de passation planifiée. Télécharge l'instantané BDD, l'installe
    /// en tant que cluster.db, accuse réception au Maître cédant, et démarre le rôle serveur.
    /// </summary>
    public async Task HandleHandoffAsync(LeadershipHandoffInstruction instruction, string masterUrl, string nodeToken, CancellationToken ct)
    {
        await syncGate.WaitAsync(ct);
        try
        {
            var downloadUri = new Uri(new Uri(masterUrl), instruction.SnapshotDownloadPath);
            logger.LogInformation("Ordre de passation de Maître reçu ({TransferId}). Téléchargement de l'instantané depuis {Uri}...", instruction.TransferId, downloadUri);

            var tempPath = DatabaseFilePath + ".transfer";
            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUri);
            request.Headers.Add("X-Node-Token", nodeToken);

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await response.Content.CopyToAsync(fileStream, ct);
                await fileStream.FlushAsync(ct);
            }

            // Vérification de l'intégrité
            string computedDigest;
            await using (var verifyStream = File.OpenRead(tempPath))
            {
                var hash = await SHA256.HashDataAsync(verifyStream, ct);
                computedDigest = Convert.ToHexString(hash).ToLowerInvariant();
            }

            if (!string.Equals(computedDigest, instruction.Digest, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
                throw new InvalidOperationException($"Empreinte SHA-256 invalide pour l'instantané de passation (reçu: {computedDigest}, attendu: {instruction.Digest}).");
            }

            // Arrêt propre du serveur local avant remplacement du fichier SQLite (pour fermer tous les descripteurs ouverts)
            if (launcher is not null)
            {
                try
                {
                    logger.LogInformation("Arrêt du serveur API local avant installation de la nouvelle base de données...");
                    await launcher.SetAsync(new NodeMode(Server: false, Worker: true), ct);
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Impossible de demander l'arrêt temporaire du serveur au lanceur.");
                }
            }

            // Suppression des journaux WAL et SHM résiduels de l'ancienne base
            var walPath = DatabaseFilePath + "-wal";
            var shmPath = DatabaseFilePath + "-shm";
            if (File.Exists(walPath)) { try { File.Delete(walPath); } catch { } }
            if (File.Exists(shmPath)) { try { File.Delete(shmPath); } catch { } }

            // Déplacement atomique vers cluster.db
            File.Move(tempPath, DatabaseFilePath, overwrite: true);
            logger.LogInformation("Instantané de passation validé et installé dans {DatabaseFilePath}.", DatabaseFilePath);

            // Accuser réception auprès du maître cédant
            try
            {
                var ackUri = new Uri(new Uri(masterUrl), $"api/v1/cluster/leadership/transfer/{instruction.TransferId:D}/ack");
                using var ackReq = new HttpRequestMessage(HttpMethod.Post, ackUri);
                ackReq.Headers.Add("X-Node-Token", nodeToken);
                using var ackResp = await httpClient.SendAsync(ackReq, ct);
                logger.LogInformation("Accusé de réception de passation envoyé au Maître cédant (HTTP {Status}).", (int)ackResp.StatusCode);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Impossible de joindre le Maître cédant pour l'accusé de réception (la promotion continue).");
            }

            // Synchroniser les clés DataProtection du Maître avant de démarrer le rôle serveur
            await SyncKeysAsync(masterUrl, nodeToken, ct);

            // Démarrer la fonction serveur locale via le lanceur
            if (launcher is not null)
            {
                logger.LogInformation("Activation de la fonction Maître locale via le lanceur (Server=true, Worker=true)...");
                await launcher.SetAsync(new NodeMode(Server: true, Worker: true), ct);
            }
            else
            {
                logger.LogWarning("Le lanceur n'est pas disponible pour ce nœud ; veuillez redémarrer avec le rôle serveur.");
            }
        }
        finally
        {
            syncGate.Release();
        }
    }

    /// <summary>
    /// Synchronise les clés XML ASP.NET DataProtection depuis le Maître pour assurer le déchiffrement des configurations et snapshots.
    /// </summary>
    public async Task SyncKeysAsync(string masterUrl, string nodeToken, CancellationToken ct)
    {
        try
        {
            var keysUri = new Uri(new Uri(masterUrl), "api/v1/cluster/leadership/keys");
            using var request = new HttpRequestMessage(HttpMethod.Get, keysUri);
            request.Headers.Add("X-Node-Token", nodeToken);
            using var response = await httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("Impossible de récupérer les clés DataProtection depuis le Maître (HTTP {Status}).", (int)response.StatusCode);
                return;
            }

            var bundle = await response.Content.ReadFromJsonAsync<LeadershipKeyBundle>(JsonOptions, ct);
            if (bundle is null || bundle.Keys.Count == 0) return;

            Directory.CreateDirectory(PluginKeysDirectory);
            foreach (var key in bundle.Keys)
            {
                if (string.IsNullOrWhiteSpace(key.FileName) || !key.FileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    continue;

                var safeFileName = Path.GetFileName(key.FileName);
                var localPath = Path.Combine(PluginKeysDirectory, safeFileName);
                if (!File.Exists(localPath))
                {
                    await File.WriteAllTextAsync(localPath, key.Content, ct);
                    logger.LogInformation("Clé DataProtection {KeyFile} synchronisée depuis le Maître.", safeFileName);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Impossible de synchroniser les clés DataProtection depuis le Maître.");
        }
    }

    /// <summary>
    /// Cas B (Reprise après sinistre) : Promotion d'urgence de ce Worker en Maître à partir de son dernier snapshot miroir.
    /// </summary>
    public async Task<EmergencyPromotionResponse> EmergencyPromoteAsync(bool force, CancellationToken ct)
    {
        await syncGate.WaitAsync(ct);
        try
        {
            if (!File.Exists(BackupFilePath))
            {
                return new EmergencyPromotionResponse(false, "Aucune sauvegarde miroir disponible sur ce worker pour effectuer la promotion d'urgence.");
            }

            var info = await GetLocalBackupInfoAsync(ct);
            logger.LogWarning("DÉCLENCHEMENT DE LA PROMOTION D'URGENCE. Restauration de la BDD depuis {BackupFilePath} (sauvegarde du {Timestamp})...",
                BackupFilePath, info?.Timestamp);

            // Arrêt propre du serveur local avant restauration de la base
            if (launcher is not null)
            {
                try
                {
                    logger.LogInformation("Arrêt du serveur API local avant restauration d'urgence...");
                    await launcher.SetAsync(new NodeMode(Server: false, Worker: true), ct);
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Impossible de demander l'arrêt temporaire du serveur au lanceur.");
                }
            }

            // Suppression des journaux WAL et SHM résiduels
            var walPath = DatabaseFilePath + "-wal";
            var shmPath = DatabaseFilePath + "-shm";
            if (File.Exists(walPath)) { try { File.Delete(walPath); } catch { } }
            if (File.Exists(shmPath)) { try { File.Delete(shmPath); } catch { } }

            // Copier le snapshot vers cluster.db
            File.Copy(BackupFilePath, DatabaseFilePath, overwrite: true);

            // Démarrer la fonction serveur via le lanceur
            if (launcher is not null)
            {
                logger.LogInformation("Lancement du rôle serveur et worker via le lanceur...");
                await launcher.SetAsync(new NodeMode(Server: true, Worker: true), ct);
            }

            return new EmergencyPromotionResponse(
                Success: true,
                Message: "Promotion d'urgence réussie. Ce nœud est désormais le nouveau Maître du cluster.",
                ClusterId: null,
                BackupTimestamp: info?.Timestamp);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Échec de la promotion d'urgence sur ce worker.");
            return new EmergencyPromotionResponse(false, $"Erreur lors de la promotion d'urgence : {ex.Message}");
        }
        finally
        {
            syncGate.Release();
        }
    }
}

public sealed record LocalBackupInfo(
    string Digest,
    long SizeBytes,
    DateTimeOffset Timestamp,
    DateTimeOffset ReceivedAt);
