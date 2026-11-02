using Encoding.Api.Libraries;

namespace Encoding.Api.Plugins;

/// <summary>
/// Transforme les imports signalés par un service externe en exécutions. Chaque demande suit le chemin commun des
/// bibliothèques : découverte par un nœud en accès direct, puis mise en file du fichier détecté. Aucun chemin média
/// reçu du réseau n’est exécuté sans être retrouvé dans la bibliothèque.
/// </summary>
public sealed class IntegrationTriggerService(IntegrationTriggerRepository requests, LibraryWorkflowRepository workflow,
    ILogger<IntegrationTriggerService> logger) : BackgroundService
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {

                await ProcessAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Traitement des déclencheurs d’intégration momentanément indisponible."); }
            try { await Task.Delay(Poll, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    /// <summary>Traite les demandes disponibles ; retourne le nombre de demandes réclamées.</summary>
    public async Task<int> ProcessAsync(CancellationToken ct)
    {
        var claimed = await requests.ClaimAsync(25, ct);
        foreach (var request in claimed)
        {
            try { await ProcessOneAsync(request, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                // La réclamation expire d’elle-même : la demande sera reprise, sans double exécution grâce aux reçus d’empreinte.
                logger.LogWarning(exception, "Déclencheur {Request} en échec temporaire.", request.Id);
            }
        }
        return claimed.Count;
    }

    private async Task ProcessOneAsync(IntegrationTriggerRequest request, CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - request.RequestedAt > IntegrationTriggerRepository.Lifetime)
        {
            await requests.CompleteAsync(request.Id, "expired", "Le fichier n’est pas apparu dans la bibliothèque dans le délai imparti.", ct);
            return;
        }
        if (!await requests.HasScanSinceAsync(request.LibraryId, request.RequestedAt, ct))
        {
            if (await workflow.RequestScanAsync(request.LibraryId, ct) is null)
                await requests.CompleteAsync(request.Id, "skipped", "Activez la bibliothèque et attribuez-lui un flow publié avant le déclencheur.", ct);
            else
                await requests.ReleaseAsync(request.Id, TimeSpan.FromSeconds(5), ct);
            return;
        }
        if (await requests.FindMediaAsync(request.LibraryId, request.RelativePath, ct) is not { } file)
        {
            await requests.CompleteAsync(request.Id, "skipped",
                "Fichier introuvable dans la bibliothèque après la découverte : vérifiez la correspondance des chemins et le type de média du flow.", ct);
            return;
        }
        var result = await workflow.EnqueueTriggeredAsync(request.LibraryId, [file], ct);
        if (result is { Queued: 1 })
            await requests.CompleteAsync(request.Id, "queued", "Exécution créée.", ct);
        else
            await requests.CompleteAsync(request.Id, "skipped",
                "Aucune exécution créée : fichier déjà traité avec cette empreinte, déjà en file, ou flow attribué indisponible.", ct);
    }
}
