using Encoding.Contracts.Executions;
using Encoding.NodeAgent.Services;

namespace Encoding.NodeAgent.Runtime;

/// <summary>Durées de conservation des dossiers de travail locaux (<c>executions/</c>, <c>transfers/</c> et, sur un nœud sans fonction serveur, <c>runtime-artifacts/</c>) d'un nœud.</summary>
public sealed record WorkspaceRetentionOptions(bool Enabled, TimeSpan Completed, TimeSpan Failed, TimeSpan Unknown, TimeSpan Interval)
{
    public static WorkspaceRetentionOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Node:Workspace");
        var options = new WorkspaceRetentionOptions(
            section.GetValue("CleanupEnabled", true),
            TimeSpan.FromHours(section.GetValue("CompletedRetentionHours", 0.0)),
            TimeSpan.FromHours(section.GetValue("FailedRetentionHours", 0.0)),
            TimeSpan.FromHours(section.GetValue("UnknownRetentionHours", 72.0)),
            TimeSpan.FromMinutes(section.GetValue("CleanupIntervalMinutes", 10.0)));
        if (options.Completed < TimeSpan.Zero || options.Failed < TimeSpan.Zero || options.Unknown < TimeSpan.Zero)
            throw new InvalidOperationException("Node:Workspace : les durées de conservation ne peuvent pas être négatives.");
        if (options.Interval < TimeSpan.FromMinutes(1)) throw new InvalidOperationException("Node:Workspace:CleanupIntervalMinutes doit être au moins 1.");
        return options;
    }

    /// <summary>
    /// Un dossier n'est supprimé que lorsque son exécution est terminée (jamais en file, en cours ou en annulation) et
    /// inactive depuis la durée prévue pour son état. Un identifiant inconnu du serveur attend la durée la plus longue.
    /// </summary>
    public bool ShouldDelete(string? state, TimeSpan idle) => state switch
    {
        ExecutionStates.Succeeded or ExecutionStates.Cancelled => idle >= Completed,
        ExecutionStates.Failed => idle >= Failed,
        null => idle >= Unknown,
        _ => false
    };
}

/// <summary>
/// Supprime les dossiers de travail des exécutions terminées (ils contiennent des copies de la source et des sorties volumineuses).
/// Ne touche ni aux médias de bibliothèque ni aux dossiers d'une exécution non terminée.
/// </summary>
public sealed class WorkspaceCleanupService(NodeAgentOptions options, WorkerIdentityStore identities, IConfiguration configuration, ILogger<WorkspaceCleanupService> logger) : BackgroundService
{
    private static readonly string[] Roots = ["executions", "transfers"];
    private const string ArtifactsRoot = "runtime-artifacts";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var retention = WorkspaceRetentionOptions.FromConfiguration(configuration);
        if (!retention.Enabled) { logger.LogInformation("Nettoyage des dossiers de travail désactivé (Node:Workspace:CleanupEnabled)."); return; }
        while (!ct.IsCancellationRequested)
        {
            try { await SweepAsync(retention, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning("Nettoyage des dossiers de travail momentanément indisponible : {Type}.", exception.GetType().Name); }
            try { await Task.Delay(retention.Interval, ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    internal async Task<int> SweepAsync(WorkspaceRetentionOptions retention, CancellationToken ct)
    {
        var identity = await identities.GetAsync(ct);
        if (!identity.IsEnrolled) return 0;
        var candidates = new Dictionary<Guid, List<string>>();
        // Avec la fonction serveur, ArtifactRetention (qui connaît publications, quarantaines et réservations) reste seul responsable de runtime-artifacts.
        var roots = options.Roles.Contains(Encoding.Contracts.Nodes.NodeRole.Server) ? Roots : [.. Roots, ArtifactsRoot];
        foreach (var root in roots)
        {
            var path = Path.Combine(options.DataDirectory, root);
            if (!Directory.Exists(path)) continue;
            foreach (var directory in Directory.EnumerateDirectories(path))
                if (Guid.TryParse(Path.GetFileName(directory), out var id)) (candidates.TryGetValue(id, out var list) ? list : candidates[id] = []).Add(directory);
        }
        if (candidates.Count == 0) return 0;

        var states = new Dictionary<Guid, string>();
        var queried = false;
        foreach (var serverUrl in identity.GetCandidateServerUrls(options))
        {
            try
            {
                using var client = new RuntimeClient(identity, serverUrl);
                foreach (var batch in candidates.Keys.Chunk(200))
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
                    var answer = await client.PostAsync<RuntimeExecutionState[]>("execution-states", new RuntimeExecutionStatesRequest(batch), timeout.Token) ?? [];
                    foreach (var item in answer) states[item.Id] = item.State;
                }
                queried = true;
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception) { logger.LogDebug(exception, "États d’exécution indisponibles pour le nettoyage des dossiers de travail."); }
        }
        if (!queried) return 0;

        var removed = 0; long freed = 0;
        foreach (var (id, directories) in candidates)
        foreach (var directory in directories)
        {
            ct.ThrowIfCancellationRequested();
            var idle = DateTime.UtcNow - LastActivityUtc(directory);
            if (!retention.ShouldDelete(states.TryGetValue(id, out var state) ? state : null, idle)) continue;
            try
            {
                var bytes = SizeOf(directory);
                Directory.Delete(directory, recursive: true);
                removed++; freed += bytes;
            }
            catch (IOException exception) { logger.LogWarning("Dossier de travail non supprimé : {Type}.", exception.GetType().Name); }
            catch (UnauthorizedAccessException exception) { logger.LogWarning("Dossier de travail non supprimé : {Type}.", exception.GetType().Name); }
        }
        if (removed > 0) logger.LogInformation("Nettoyage : {Count} dossiers de travail supprimés ({Bytes} octets).", removed, freed);
        return removed;
    }

    private static DateTime LastActivityUtc(string directory)
    {
        var latest = Directory.GetLastWriteTimeUtc(directory);
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            if (entry.LastWriteTimeUtc > latest) latest = entry.LastWriteTimeUtc;
        return latest;
    }

    private static long SizeOf(string directory) =>
        new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => { try { return file.Length; } catch (IOException) { return 0L; } });
}
