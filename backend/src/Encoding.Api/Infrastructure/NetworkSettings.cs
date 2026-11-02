using System.Net;
using System.Text.Json;
using Encoding.Api.Auth;

namespace Encoding.Api.Infrastructure;

/// <summary>
/// Réglage réseau propre à ce nœud : reverse proxy dont les en-têtes <c>X-Forwarded-*</c> sont crus, et URL publique
/// (adresse par laquelle ce nœud est joignable, affichée à la place de celle du navigateur ; vide = adresse du navigateur).
/// </summary>
public sealed record NetworkSettings(bool TrustProxy, IReadOnlyList<string> Proxies, string? PublicUrl = null)
{
    public static readonly NetworkSettings Default = new(false, []);
}

/// <summary>
/// Conserve le réglage dans un fichier du dossier de données du nœud (non répliqué : chaque nœud a son propre proxy).
/// Premier démarrage : repris des variables <c>Auth__TrustForwardedHeaders</c>, <c>Auth__KnownProxies</c> et
/// <c>Auth__KnownNetworks</c> si elles existent, pour ne pas casser les installations déjà configurées.
/// </summary>
public sealed class NetworkSettingsService
{
    public const int MaximumEntries = 32;
    private readonly string path;
    private readonly object sync = new();
    private NetworkSettings current;
    private ProxyMatcher matcher;

    public NetworkSettingsService(IConfiguration configuration)
    {
        path = configuration["Network:SettingsFile"] is { Length: > 0 } configured ? configured
            : Path.Combine(configuration["Node:DataDirectory"] ?? Path.Combine(AppContext.BaseDirectory, "Data"), "network-settings.json");
        current = Load(configuration);
        matcher = ProxyMatcher.Create(current.Proxies);
    }

    public NetworkSettings Current { get { lock (sync) return current; } }

    public bool IsTrustedProxy(IPAddress? address)
    {
        lock (sync) return current.TrustProxy && address is not null && matcher.Contains(address);
    }

    public bool IsListed(IPAddress address)
    {
        lock (sync) return matcher.Contains(address);
    }

    /// <summary>Valide et enregistre ; renvoie les erreurs par champ, vide si accepté.</summary>
    public IReadOnlyDictionary<string, string[]> Save(bool trustProxy, IEnumerable<string>? proxies, string? publicUrl = null)
    {
        if (!TryNormalizePublicUrl(publicUrl, out var normalizedUrl))
            return new Dictionary<string, string[]> { ["publicUrl"] = ["Indiquez une adresse complète, par exemple https://encoding.example.com."] };
        var entries = (proxies ?? []).Select(item => item?.Trim() ?? string.Empty).Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (entries.Length > MaximumEntries)
            return new Dictionary<string, string[]> { ["proxies"] = [$"Au plus {MaximumEntries} adresses."] };
        var invalid = entries.Where(item => !ProxyMatcher.IsValidEntry(item)).ToArray();
        if (invalid.Length > 0)
            return new Dictionary<string, string[]> { ["proxies"] = [$"Adresse ou réseau invalide : {string.Join(", ", invalid)}."] };
        if (trustProxy && entries.Length == 0)
            return new Dictionary<string, string[]> { ["proxies"] = ["Indiquez au moins une adresse de proxy pour activer la confiance."] };
        var next = new NetworkSettings(trustProxy, entries, normalizedUrl);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(next, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        File.Move(temporary, path, overwrite: true);
        lock (sync)
        {
            current = next;
            matcher = ProxyMatcher.Create(entries);
        }
        return new Dictionary<string, string[]>();
    }

    /// <summary>Accepte une adresse http(s) absolue, sans identifiants, requête ni ancre ; retire le « / » final. Vide = non définie.</summary>
    public static bool TryNormalizePublicUrl(string? value, out string? normalized)
    {
        normalized = null;
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return true;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || string.IsNullOrEmpty(uri.Host))
            return false;
        normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }

    private NetworkSettings Load(IConfiguration configuration)
    {
        try
        {
            if (File.Exists(path)
                && JsonSerializer.Deserialize<NetworkSettings>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)) is { } stored)
                return stored with
                {
                    Proxies = stored.Proxies?.Where(ProxyMatcher.IsValidEntry).ToArray() ?? [],
                    PublicUrl = TryNormalizePublicUrl(stored.PublicUrl, out var url) ? url : null,
                };
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // Fichier illisible : repli sur la configuration, sans confiance accordée par défaut.
        }
        var seeded = (configuration["Auth:KnownProxies"] + "," + configuration["Auth:KnownNetworks"])
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(ProxyMatcher.IsValidEntry).ToArray();
        return configuration.GetValue("Auth:TrustForwardedHeaders", false) && seeded.Length > 0 ? new NetworkSettings(true, seeded) : NetworkSettings.Default;
    }
}

/// <summary>Ensemble d'adresses et de réseaux (CIDR) ; les adresses IPv4 mappées en IPv6 sont ramenées en IPv4.</summary>
public sealed class ProxyMatcher
{
    private readonly IReadOnlyList<IPNetwork> networks;
    private ProxyMatcher(IReadOnlyList<IPNetwork> networks) => this.networks = networks;

    public static ProxyMatcher Create(IEnumerable<string> entries) => new(entries.Where(IsValidEntry).Select(Parse).ToArray());

    public static bool IsValidEntry(string entry)
    {
        try { Parse(entry); return true; }
        catch (Exception exception) when (exception is FormatException or ArgumentException) { return false; }
    }

    private static IPNetwork Parse(string entry)
    {
        if (entry.Contains('/')) return IPNetwork.Parse(entry);
        var address = Normalize(IPAddress.Parse(entry));
        return new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
    }

    public static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    public bool Contains(IPAddress address)
    {
        var normalized = Normalize(address);
        return networks.Any(network => network.Contains(normalized));
    }
}

public static class NetworkSettingsExtensions
{
    public const string OriginalRemoteAddressItem = "Encoding.OriginalRemoteAddress";

    /// <summary>
    /// Applique X-Forwarded-For et X-Forwarded-Proto uniquement si l'appelant direct figure parmi les proxies déclarés.
    /// La chaîne X-Forwarded-For est lue de droite à gauche en sautant les proxies déclarés.
    /// </summary>
    public static WebApplication UseTrustedProxies(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<NetworkSettingsService>();
        if (settings.Current.TrustProxy)
            app.Logger.LogInformation("Proxies de confiance : {Proxies}.", string.Join(", ", settings.Current.Proxies));
        app.Use((context, next) =>
        {
            var direct = context.Connection.RemoteIpAddress;
            context.Items[OriginalRemoteAddressItem] = direct;
            if (direct is null || !settings.IsTrustedProxy(direct)) return next();
            var chain = context.Request.Headers["X-Forwarded-For"].SelectMany(value => (value ?? string.Empty).Split(','))
                .Select(value => value.Trim()).Where(value => value.Length > 0).ToArray();
            for (var index = chain.Length - 1; index >= 0; index--)
            {
                if (!IPAddress.TryParse(chain[index], out var hop)) break;
                context.Connection.RemoteIpAddress = hop;
                if (!settings.IsListed(hop)) break;
            }
            var proto = context.Request.Headers["X-Forwarded-Proto"].ToString().Split(',')[0].Trim();
            if (proto.Equals("https", StringComparison.OrdinalIgnoreCase) || proto.Equals("http", StringComparison.OrdinalIgnoreCase))
                context.Request.Scheme = proto.ToLowerInvariant();
            return next();
        });
        return app;
    }

    public static void MapNetworkSettingsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/network").WithTags("Réseau").RequireAuthorization(AuthExtensions.AdministratorPolicy);
        group.MapGet("/settings", (NetworkSettingsService settings) => Results.Ok(settings.Current)).WithName("GetNetworkSettings");
        group.MapPut("/settings", (NetworkSettingsRequest request, NetworkSettingsService settings) =>
        {
            var errors = settings.Save(request.TrustProxy, request.Proxies, request.PublicUrl);
            return errors.Count > 0 ? Results.ValidationProblem(errors.ToDictionary(pair => pair.Key, pair => pair.Value)) : Results.Ok(settings.Current);
        }).WithName("UpdateNetworkSettings");
        // Observe la requête reçue : si elle traverse un proxy qui pose les en-têtes habituels, l'adresse directe est le proxy à déclarer.
        group.MapPost("/detect-proxy", (HttpContext http, NetworkSettingsService settings) =>
        {
            var direct = http.Items[OriginalRemoteAddressItem] as IPAddress ?? http.Connection.RemoteIpAddress;
            var headers = http.Request.Headers;
            var forwarded = headers.ContainsKey("X-Forwarded-For") || headers.ContainsKey("X-Forwarded-Proto")
                || headers.ContainsKey("X-Forwarded-Host") || headers.ContainsKey("Forwarded") || headers.ContainsKey("X-Real-IP");
            var address = direct is null ? null : ProxyMatcher.Normalize(direct);
            var privateAddress = address is not null && (IPAddress.IsLoopback(address) || IsPrivate(address));
            return Results.Ok(new NetworkDetectionResponse(
                address?.ToString(), forwarded, privateAddress, address is not null && settings.IsListed(address),
                headers["X-Forwarded-For"].ToString(), headers["X-Forwarded-Proto"].ToString()));
        }).WithName("DetectReverseProxy");
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 169 && bytes[1] == 254);
        }
        return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
    }
}

public sealed record NetworkSettingsRequest(bool TrustProxy, IReadOnlyList<string>? Proxies, string? PublicUrl = null);

public sealed record NetworkDetectionResponse(
    string? ProxyAddress, bool ForwardedHeadersSeen, bool PrivateAddress, bool AlreadyTrusted, string ForwardedFor, string ForwardedProto);
