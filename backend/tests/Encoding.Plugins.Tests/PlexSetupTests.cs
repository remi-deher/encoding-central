using System.Text.Json;
using Encoding.Api.Plugins.Plex;
using Encoding.Contracts.Plugins;
using Microsoft.AspNetCore.DataProtection;
using static Encoding.Plugins.Tests.Support;

namespace Encoding.Plugins.Tests;

/// <summary>Assistant de configuration Plex (0008) : proposition de correspondance, session opaque, lecture des réponses Plex.</summary>
internal static class PlexSetupTests
{
    public static void Run()
    {
        IdenticalPathsWin();
        TrailingSegmentsAndKindBreakTies();
        TiesAreNeverDecided();
        ALocationGoesToOneLibraryOnly();
        FormatUsesTheSharedMappingRules();
        SessionRoundTripsAndExpires();
        TamperedSessionsAreRejected();
        ServersKeepTokensOffTheContractAndPreferLocal();
        SectionsAreReadWithTheirLocations();
    }

    private static readonly Guid MoviesData = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid MoviesData2 = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid ShowsUsb = Guid.Parse("00000000-0000-0000-0000-0000000000b1");

    private static readonly RemoteLibrary PlexMovies = new("3", "Films", "movie", ["/usb/MEDIA/FILMS", "/media2/FILMS", "/media/FILMS"]);
    private static readonly RemoteLibrary PlexShows = new("2", "Séries TV", "show", ["/usb/MEDIA/SERIES", "/media/SERIES"]);
    private static readonly RemoteLibrary PlexAdult = new("6", "[-18] Séries TV", "show", ["/media/SERIES-18"]);

    private static void IdenticalPathsWin()
    {
        var result = LibraryMappingSuggester.Suggest(
            [new(MoviesData, "Films — NAS DATA", ["/media/FILMS"]), new(MoviesData2, "Films — NAS DATA2", ["/media2/FILMS"]), new(ShowsUsb, "Séries — USB 1", ["/usb/MEDIA/SERIES"])],
            [PlexMovies, PlexShows, PlexAdult]);
        Check(result.Count == 3, "Each library with an identical Plex location gets a suggestion.");
        Check(result.Single(item => item.LibraryId == MoviesData).RemoteRoot == "/media/FILMS", "An identical path beats a shared final folder (/media2/FILMS).");
        Check(result.Single(item => item.LibraryId == MoviesData2).RemoteRoot == "/media2/FILMS", "DATA2 maps to its own location.");
        var shows = result.Single(item => item.LibraryId == ShowsUsb);
        Check(shows.RemoteRoot == "/usb/MEDIA/SERIES" && shows.RemoteKey == "2" && shows.Reason.Contains("Même chemin"), "The suggestion names the Plex section and explains the exact match.");
    }

    private static void TrailingSegmentsAndKindBreakTies()
    {
        // Montages différents entre le nœud et Plex : les dossiers finaux communs décident, le type départage.
        var result = LibraryMappingSuggester.Suggest(
            [new(MoviesData, "Films", ["/mnt/pool/MEDIA/FILMS"]), new(ShowsUsb, "Séries", ["/mnt/pool/MEDIA/SERIES"])],
            [new("1", "Films", "movie", ["/data/MEDIA/FILMS"]), new("2", "Séries", "show", ["/data/MEDIA/SERIES"])]);
        Check(result.Single(item => item.LibraryId == MoviesData).RemoteRoot == "/data/MEDIA/FILMS", "Two common final folders map Films.");
        Check(result.Single(item => item.LibraryId == ShowsUsb).RemoteRoot == "/data/MEDIA/SERIES", "Two common final folders map Séries.");
        var kind = LibraryMappingSuggester.Suggest([new(MoviesData, "Films", ["/srv/FILMS"])],
            [new("1", "Vidéos", "show", ["/a/FILMS"]), new("2", "Films", "movie", ["/b/FILMS"])]);
        Check(kind.Single().RemoteRoot == "/b/FILMS", "With equal folders, the matching library kind wins.");
    }

    private static void TiesAreNeverDecided()
    {
        var result = LibraryMappingSuggester.Suggest([new(MoviesData, "Bibliothèque", ["/srv/FILMS"])],
            [new("1", "A", "movie", ["/a/FILMS"]), new("2", "B", "movie", ["/b/FILMS"])]);
        Check(result.Count == 0, "Two equally good locations produce no suggestion: the user chooses.");
        Check(LibraryMappingSuggester.Suggest([new(MoviesData, "Films", [])], [PlexMovies]).Count == 0, "A library without direct access path gets no suggestion.");
        Check(LibraryMappingSuggester.Suggest([new(MoviesData, "Films", ["/x/AUTRE"])], [PlexMovies]).Count == 0, "No common folder, no suggestion.");
    }

    private static void ALocationGoesToOneLibraryOnly()
    {
        var result = LibraryMappingSuggester.Suggest(
            [new(MoviesData, "Films A", ["/n1/FILMS"]), new(MoviesData2, "Films B", ["/n2/FILMS"])],
            [new("1", "Films", "movie", ["/plex/FILMS"])]);
        Check(result.Count == 0, "A location two libraries claim equally is suggested to neither.");
        var exact = LibraryMappingSuggester.Suggest(
            [new(MoviesData, "Films A", ["/plex/FILMS"]), new(MoviesData2, "Films B", ["/n2/FILMS"])],
            [new("1", "Films", "movie", ["/plex/FILMS"])]);
        Check(exact.Count == 1 && exact[0].LibraryId == MoviesData, "The stronger claim keeps the location.");
    }

    private static void FormatUsesTheSharedMappingRules()
    {
        var text = LibraryMappingSuggester.Format([(MoviesData, "/media/FILMS/"), (ShowsUsb, "/usb/MEDIA/SERIES"), (MoviesData2, " ")]);
        var parsed = LibraryPathMapping.Parse(text);
        Check(parsed.Count == 2 && parsed[0].RemoteRoot == "/media/FILMS", "Format writes the LibraryPathMapping format and skips empty roots.");
        var duplicate = false;
        try { LibraryMappingSuggester.Format([(MoviesData, "/media/FILMS"), (MoviesData2, "/media/FILMS")]); }
        catch (FormatException) { duplicate = true; }
        Check(duplicate, "A root used twice is refused like a manual entry.");
    }

    private static void SessionRoundTripsAndExpires()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-23T10:00:00Z"));
        var sessions = new PlexSetupSession(new EphemeralDataProtectionProvider(), clock: clock);
        var session = sessions.Protect("compte-secret", "encoding-client");
        Check(!session.Contains("compte-secret"), "The session never carries the token in clear.");
        var (token, client) = sessions.Open(session);
        Check(token == "compte-secret" && client == "encoding-client", "An open session returns the account token server side.");
        clock.Now += PlexSetupSession.Lifetime + TimeSpan.FromSeconds(1);
        Check(Throws(() => sessions.Open(session)), "A session older than its lifetime is refused.");
    }

    private static void TamperedSessionsAreRejected()
    {
        var sessions = new PlexSetupSession(new EphemeralDataProtectionProvider());
        var session = sessions.Protect("compte-secret", "encoding-client");
        var tampered = session[..^4] + (session[^4] == 'A' ? "BBBB" : "AAAA");
        Check(Throws(() => sessions.Open(tampered)), "A modified session is refused.");
        Check(Throws(() => sessions.Open("pas-une-session")), "Garbage is refused.");
        Check(Throws(() => sessions.Open(null)), "A missing session is refused.");
        var other = new PlexSetupSession(new EphemeralDataProtectionProvider());
        Check(Throws(() => other.Open(session)), "A session from another key ring is refused.");
    }

    private static void ServersKeepTokensOffTheContractAndPreferLocal()
    {
        using var document = JsonDocument.Parse("""
            [
              {"name":"Lecteur","provides":"player","clientIdentifier":"p1","accessToken":"t0"},
              {"name":"ARTEMIS","provides":"server","clientIdentifier":"s1","accessToken":"jeton-serveur","owned":true,
               "connections":[{"uri":"https://relay.plex.direct:8443","local":false,"relay":true},
                              {"uri":"https://1-2-3-4.abc.plex.direct:32400","local":false,"relay":false},
                              {"uri":"http://192.168.1.51:32400/","local":true,"relay":false},
                              {"uri":"ftp://ignored","local":true}]},
              {"name":"Sans jeton","provides":"server","clientIdentifier":"s2"}
            ]
            """);
        var servers = PlexApiClient.ParseServers(document.RootElement);
        Check(servers.Count == 1 && servers[0].Server.Id == "s1" && servers[0].AccessToken == "jeton-serveur", "Only servers with a token are kept, players are ignored.");
        var connections = servers[0].Server.Connections;
        Check(connections.Count == 3 && connections[0].Uri == "http://192.168.1.51:32400" && connections[^1].Relay, "Local addresses come first, relays last, non-HTTP dropped.");
        Check(!JsonSerializer.Serialize(servers[0].Server).Contains("jeton-serveur"), "The server contract sent to the browser has no token.");
    }

    private static void SectionsAreReadWithTheirLocations()
    {
        using var document = JsonDocument.Parse("""
            {"MediaContainer":{"Directory":[
              {"key":"3","type":"movie","title":"Films","Location":[{"id":1,"path":"/media/FILMS"},{"id":2,"path":"/usb/MEDIA/FILMS"}]},
              {"key":5,"type":"artist","title":"Musique","Location":[{"path":"/audio"}]},
              {"type":"show","title":"Sans clé"}]}}
            """);
        var sections = PlexApiClient.ParseSections(document.RootElement);
        Check(sections.Count == 2 && sections[0].Locations.SequenceEqual(["/media/FILMS", "/usb/MEDIA/FILMS"]), "Sections keep every location in order.");
        Check(sections[1].Key == "5" && sections[1].Kind == "artist", "Numeric keys are read as text.");
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (PlexSetupException) { return true; }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
