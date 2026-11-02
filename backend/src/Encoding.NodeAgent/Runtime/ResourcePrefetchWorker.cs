using Encoding.Contracts.Nodes;
using Encoding.NodeAgent.Services;

namespace Encoding.NodeAgent.Runtime;

/// <summary>
/// Remplit le cache du worker avec la dernière version de chaque ressource dès qu'elle est publiée sur son serveur :
/// le lancement d'un flow n'a alors plus rien à télécharger. Une version plus ancienne est récupérée à la demande.
/// </summary>
public sealed class ResourcePrefetchWorker(NodeAgentOptions options, WorkerIdentityStore identities, ILogger<ResourcePrefetchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var identity = await identities.GetAsync(ct);
                if (identity.IsEnrolled && options.Roles.Contains(NodeRole.Worker))
                {
                    foreach (var serverUrl in identity.GetCandidateServerUrls(options))
                    {
                        try
                        {
                            using var client = new RuntimeClient(identity, serverUrl);
                            foreach (var version in await client.LatestResourceVersionsAsync(ct))
                                await client.EnsureResourceAsync(version, ResourceCache.Root(options), null, ct);
                            break;
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception exception) { logger.LogDebug(exception, "Préchargement ressource via ce serveur différé."); }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogDebug("Préchargement des ressources différé : {Type}.", exception.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }
}
