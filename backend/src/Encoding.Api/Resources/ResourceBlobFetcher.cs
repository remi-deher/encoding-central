using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Encoding.Api.Infrastructure;

namespace Encoding.Api.Resources;

/// <summary>
/// Récupère auprès des pairs admis un fichier de ressource absent du magasin local (ADR 0006). Les pairs sont
/// interrogés dans l'ordre ; taille et SHA-256 sont vérifiés avant publication dans le magasin. Un transfert interrompu
/// reprend à l'octet atteint (en-tête <c>Range</c>). Deux demandes simultanées de la même empreinte partagent un seul transfert.
/// </summary>
public sealed class ResourceBlobFetcher(
    ResourceBlobStore blobs,
    ClusterPeerIdentityStore localIdentity,
    ClusterReconciliationRepository peers,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    ILogger<ResourceBlobFetcher> logger)
{
    public const string RoutePrefix = "/api/v1/cluster/resources/blobs/";
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, Lazy<Task<bool>>> inFlight = new(StringComparer.Ordinal);

    /// <summary>Texte signé : méthode, chemin et empreinte, jamais un corps.</summary>
    public static byte[] SignedRequest(string digest) => System.Text.Encoding.UTF8.GetBytes("GET " + RoutePrefix + digest);

    public bool IsPresent(string digest) => blobs.Exists(digest);

    /// <summary>Vrai si le fichier est dans le magasin local à la fin de l'appel.</summary>
    public async Task<bool> EnsureAsync(string digest, long expectedBytes, CancellationToken ct)
    {
        if (!ResourceBlobStore.ValidDigest(digest) || expectedBytes is <= 0 or > ResourceBlobStore.MaxFileBytes) return false;
        if (blobs.Exists(digest)) return true;
        var lazy = inFlight.GetOrAdd(digest, key => new(() => FetchAsync(key, expectedBytes)));
        try { return await lazy.Value.WaitAsync(ct); }
        finally { if (lazy.Value.IsCompleted) inFlight.TryRemove(digest, out _); }
    }

    private async Task<bool> FetchAsync(string digest, long expectedBytes)
    {
        using var timeout = new CancellationTokenSource(TransferTimeout);
        foreach (var peerUrl in await PeerUrlsAsync(timeout.Token))
        {
            try
            {
                if (await TryFetchFromAsync(peerUrl, digest, expectedBytes, timeout.Token)) return true;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException or InvalidDataException)
            {
                logger.LogDebug(exception, "Récupération de la ressource {Digest} impossible auprès de {Peer}.", digest[..12], peerUrl);
            }
        }
        return blobs.Exists(digest);
    }

    private async Task<IReadOnlyList<string>> PeerUrlsAsync(CancellationToken ct)
    {
        var own = Normalize(configuration["Cluster:Reconciliation:AdvertisedApiBaseUrl"]);
        var configured = configuration.GetSection("Cluster:Reconciliation:Peers").Get<string[]>() ?? [];
        return configured.Concat(await peers.GetPeerEndpointsAsync(ct))
            .Select(Normalize).Where(url => url is not null && !string.Equals(url, own, StringComparison.OrdinalIgnoreCase))
            .Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task<bool> TryFetchFromAsync(string peerUrl, string digest, long expectedBytes, CancellationToken ct)
    {
        var partial = blobs.PartialPathFor(digest);
        Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (offset >= expectedBytes) { File.Delete(partial); offset = 0; }

        var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(peerUrl), RoutePrefix.TrimStart('/') + digest));
        message.Headers.Add(ClusterReconciliationSignature.PeerHeader, localIdentity.PeerId.ToString("D"));
        message.Headers.Add(ClusterReconciliationSignature.TimeHeader, time);
        message.Headers.Add(ClusterReconciliationSignature.SignatureHeader, ClusterReconciliationSignature.Create(localIdentity, time, SignedRequest(digest)));
        if (offset > 0) message.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, null);

        using var response = await httpClientFactory.CreateClient("ClusterReconciliation").SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) { File.Delete(partial); return false; }
        if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.PartialContent) return false;
        var append = response.StatusCode == HttpStatusCode.PartialContent && offset > 0;
        // A peer may ignore Range and return the complete blob with 200. In that case the existing
        // partial file will be replaced, so only the new response body contributes to the size check.
        var existingBytes = append ? offset : 0;
        if (existingBytes + (response.Content.Headers.ContentLength ?? 0) > expectedBytes) return false;

        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
        {
            var buffer = new byte[81920];
            int count;
            long total = existingBytes;
            while ((count = await input.ReadAsync(buffer, ct)) > 0)
            {
                total += count;
                if (total > expectedBytes) { output.Close(); File.Delete(partial); throw new InvalidDataException("Le pair a envoyé plus d'octets que prévu."); }
                await output.WriteAsync(buffer.AsMemory(0, count), ct);
            }
        }
        return await PublishIfValidAsync(partial, digest, expectedBytes, ct);
    }

    /// <summary>Taille puis empreinte vérifiées ; un fichier incomplet est conservé pour reprise, un fichier faux est supprimé.</summary>
    private async Task<bool> PublishIfValidAsync(string partial, string digest, long expectedBytes, CancellationToken ct)
    {
        var length = new FileInfo(partial).Length;
        if (length < expectedBytes) return false;
        string actual;
        await using (var file = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous))
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
        if (length != expectedBytes || actual != digest)
        {
            File.Delete(partial);
            throw new InvalidDataException("Le fichier reçu ne correspond pas à l'empreinte attendue.");
        }
        blobs.Publish(partial, digest);
        return true;
    }

    private static string? Normalize(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && (uri.Scheme is "http" or "https") && string.IsNullOrEmpty(uri.UserInfo)
            ? new Uri(uri.GetLeftPart(UriPartial.Authority) + "/").AbsoluteUri
            : null;
}
