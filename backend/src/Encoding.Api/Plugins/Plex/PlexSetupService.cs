using System.Text.Json;
using Encoding.Api.Flows;
using Encoding.Api.Libraries;
using Encoding.Contracts.Libraries;
using Encoding.Contracts.Plugins;

namespace Encoding.Api.Plugins.Plex;

/// <summary>Demande invalide de l'assistant (session expirée, serveur inconnu…) : message destiné à l'utilisateur, sans secret.</summary>
public sealed class PlexSetupException(string message) : Exception(message);

/// <summary>
/// Assistant de configuration du plugin Plex (0008) : connexion plex.tv par PIN, choix du serveur, récupération des
/// bibliothèques et correspondance des chemins. N'écrit que les champs du schéma du plugin (baseUrl, apiKey, pathMappings)
/// par <see cref="PluginConfigurationRepository"/>, avec la même validation qu'un enregistrement manuel.
/// </summary>
public sealed class PlexSetupService(
    PlexApiClient plex,
    FlowComponentRegistry catalog,
    PluginConfigurationRepository configurations,
    LibraryRepository libraries,
    LibraryNodeAccessRepository nodeAccess,
    PlexSetupSession sessions)
{
    public const string PluginId = "plex";

    public async Task<PlexPinResponse> CreatePinAsync(CancellationToken ct)
    {
        var clientIdentifier = "encoding-" + Guid.NewGuid().ToString("N");
        var (id, code, expiresAt) = await plex.CreatePinAsync(clientIdentifier, ct);
        var authUrl = "https://app.plex.tv/auth#?" + string.Join('&',
            "clientID=" + Uri.EscapeDataString(clientIdentifier),
            "code=" + Uri.EscapeDataString(code),
            "context%5Bdevice%5D%5Bproduct%5D=" + Uri.EscapeDataString(PlexApiClient.Product));
        return new(id, code, clientIdentifier, authUrl, expiresAt);
    }

    public async Task<PlexPinCheckResponse> CheckPinAsync(PlexPinCheckRequest request, CancellationToken ct)
    {
        if (request.PinId <= 0 || string.IsNullOrWhiteSpace(request.ClientIdentifier))
            throw new PlexSetupException("Code de connexion invalide.");
        var (token, expired) = await plex.CheckPinAsync(request.PinId, request.ClientIdentifier, ct);
        if (token is null) return new(expired ? "expired" : "pending", null, null);
        var servers = await plex.ServersAsync(token, request.ClientIdentifier, ct);
        return new("authorized", sessions.Protect(token, request.ClientIdentifier), servers.Select(item => item.Server).ToList());
    }

    public async Task<PluginConfigurationResponse> SelectServerAsync(PlexSelectServerRequest request, CancellationToken ct)
    {
        var (token, clientIdentifier) = sessions.Open(request.Session);
        var servers = await plex.ServersAsync(token, clientIdentifier, ct);
        var match = servers.Where(item => item.Server.Id == request.ServerId).ToList();
        if (match.Count != 1) throw new PlexSetupException("Ce serveur n’appartient pas au compte Plex connecté.");
        var baseUrl = (request.Uri ?? "").Trim().TrimEnd('/');
        await plex.VerifyServerAsync(baseUrl, match[0].AccessToken, ct);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["baseUrl"] = JsonSerializer.SerializeToElement(baseUrl),
            ["apiKey"] = JsonSerializer.SerializeToElement(match[0].AccessToken)
        };
        return await configurations.SaveGlobalAsync(Plugin(), new SavePluginConfigurationRequest(0, values), ct)
            ?? throw new PlexSetupException("La configuration du plugin n’a pas pu être enregistrée.");
    }

    public async Task<PlexLibrariesResponse> LibrariesAsync(PlexLibrariesRequest request, CancellationToken ct)
    {
        var values = await configurations.ResolveGlobalForConnectionTestAsync(Plugin(),
            request.Values ?? new Dictionary<string, JsonElement>(), ct);
        var sections = await plex.SectionsAsync(Text(values, "baseUrl"), Text(values, "apiKey"), ct);
        IReadOnlyList<LibraryPathMapping> current;
        try { current = LibraryPathMapping.Parse(Text(values, "pathMappings")); }
        catch (FormatException) { current = []; }

        var local = new List<LocalLibrary>();
        foreach (var library in (await libraries.ListAsync(ct)).OrderBy(item => item.Name, StringComparer.CurrentCulture))
        {
            var paths = (await nodeAccess.ListAsync(library.Id, ct) ?? [])
                .Where(access => access.Mode == LibraryNodeAccessMode.Direct && !string.IsNullOrWhiteSpace(access.LocalPath))
                .Select(access => access.LocalPath!).Distinct(StringComparer.Ordinal).ToList();
            local.Add(new(library.Id, library.Name, paths));
        }
        var suggestions = LibraryMappingSuggester.Suggest(local, sections).ToDictionary(item => item.LibraryId);
        return new(sections, local.Select(library => new PlexEncodingLibrary(
            library.Id, library.Name, library.LocalPaths,
            current.FirstOrDefault(mapping => mapping.LibraryId == library.Id)?.RemoteRoot,
            suggestions.GetValueOrDefault(library.Id))).ToList());
    }

    public async Task<PluginConfigurationResponse> SaveMappingsAsync(PlexSaveMappingsRequest request, CancellationToken ct)
    {
        var known = (await libraries.ListAsync(ct)).Select(library => library.Id).ToHashSet();
        if ((request.Mappings ?? []).Any(entry => !known.Contains(entry.LibraryId)))
            throw new PlexSetupException("Une bibliothèque de la correspondance n’existe plus. Rechargez la liste.");
        string text;
        try { text = LibraryMappingSuggester.Format((request.Mappings ?? []).Select(entry => (entry.LibraryId, entry.RemoteRoot))); }
        catch (FormatException exception) { throw new PlexSetupException(exception.Message); }
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["pathMappings"] = JsonSerializer.SerializeToElement(text) };
        return await configurations.SaveGlobalAsync(Plugin(), new SavePluginConfigurationRequest(0, values), ct)
            ?? throw new PlexSetupException("La configuration du plugin n’a pas pu être enregistrée.");
    }

    private PluginManifest Plugin() =>
        catalog.Plugins.SingleOrDefault(item => item.Id == PluginId) ?? throw new PlexSetupException("Le plugin Plex n’est pas installé.");

    private static string Text(IReadOnlyDictionary<string, JsonElement> values, string key) =>
        values.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

}
