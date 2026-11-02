namespace Encoding.Contracts.Plugins;

// Assistant de configuration Plex (0008). Aucun jeton Plex n'apparaît dans ces contrats : la session est opaque et chiffrée.

public sealed record PlexPinResponse(long PinId, string Code, string ClientIdentifier, string AuthUrl, DateTimeOffset ExpiresAt);

public sealed record PlexPinCheckRequest(long PinId, string ClientIdentifier);

/// <summary>Statut : <c>pending</c>, <c>authorized</c> (session et serveurs renseignés) ou <c>expired</c>.</summary>
public sealed record PlexPinCheckResponse(string Status, string? Session, IReadOnlyList<PlexServer>? Servers);

public sealed record PlexServer(string Id, string Name, bool Owned, IReadOnlyList<PlexServerConnection> Connections);

public sealed record PlexServerConnection(string Uri, bool Local, bool Relay);

public sealed record PlexSelectServerRequest(string Session, string ServerId, string Uri);

public sealed record PlexLibrariesRequest(IReadOnlyDictionary<string, System.Text.Json.JsonElement>? Values);

public sealed record PlexLibrariesResponse(
    IReadOnlyList<RemoteLibrary> Sections,
    IReadOnlyList<PlexEncodingLibrary> Libraries);

/// <summary>Bibliothèque Encoding, racine Plex enregistrée (ou null) et proposition (ou null).</summary>
public sealed record PlexEncodingLibrary(
    Guid Id,
    string Name,
    IReadOnlyList<string> LocalPaths,
    string? CurrentRemoteRoot,
    LibraryMappingSuggestion? Suggestion);

public sealed record PlexMappingEntry(Guid LibraryId, string RemoteRoot);

public sealed record PlexSaveMappingsRequest(IReadOnlyList<PlexMappingEntry> Mappings);
