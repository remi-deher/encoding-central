namespace Encoding.Contracts.Plugins;

/// <summary>Bibliothèque d'un service externe (section Plex…) et ses dossiers vus par ce service.</summary>
public sealed record RemoteLibrary(string Key, string Title, string Kind, IReadOnlyList<string> Locations);

/// <summary>Bibliothèque Encoding et les chemins déclarés par ses nœuds en accès direct.</summary>
public sealed record LocalLibrary(Guid Id, string Name, IReadOnlyList<string> LocalPaths);

/// <summary>Racine proposée pour une bibliothèque Encoding, avec la raison lisible du choix.</summary>
public sealed record LibraryMappingSuggestion(Guid LibraryId, string RemoteRoot, string RemoteKey, string RemoteTitle, string Reason);

/// <summary>
/// Proposition de correspondance entre bibliothèques Encoding et emplacements d'un service externe (0008).
/// Règle unique : un chemin identique l'emporte ; sinon le plus grand nombre de segments finaux communs (au moins un),
/// départagé par le type de bibliothèque (films/séries) déduit du nom. Une égalité n'est jamais tranchée et un emplacement
/// n'est proposé qu'à une seule bibliothèque : l'utilisateur choisit.
/// </summary>
public static class LibraryMappingSuggester
{
    private const int ExactScore = 1000;
    private const int SegmentScore = 10;
    private const int KindBonus = 3;

    public static IReadOnlyList<LibraryMappingSuggestion> Suggest(IReadOnlyList<LocalLibrary> libraries, IReadOnlyList<RemoteLibrary> remote)
    {
        var candidates = new List<(LocalLibrary Library, RemoteLibrary Remote, string Location, int Score, string Reason)>();
        foreach (var library in libraries)
        {
            var best = new List<(RemoteLibrary Remote, string Location, int Score, string Reason)>();
            foreach (var section in remote)
                foreach (var location in section.Locations)
                {
                    var (score, reason) = Score(library, section, location);
                    if (score > 0) best.Add((section, location, score, reason));
                }
            if (best.Count == 0) continue;
            var top = best.Max(item => item.Score);
            var winners = best.Where(item => item.Score == top).ToList();
            if (winners.Count == 1) candidates.Add((library, winners[0].Remote, winners[0].Location, top, winners[0].Reason));
        }

        // Un emplacement va à une seule bibliothèque : le meilleur score le garde, une égalité le laisse sans proposition.
        var result = new List<LibraryMappingSuggestion>();
        foreach (var group in candidates.GroupBy(item => Normalize(item.Location), StringComparer.Ordinal))
        {
            var top = group.Max(item => item.Score);
            var winners = group.Where(item => item.Score == top).ToList();
            if (winners.Count != 1) continue;
            var winner = winners[0];
            result.Add(new(winner.Library.Id, Normalize(winner.Location), winner.Remote.Key, winner.Remote.Title, winner.Reason));
        }
        return result.OrderBy(item => libraries.ToList().FindIndex(library => library.Id == item.LibraryId)).ToList();
    }

    /// <summary>Texte <c>pathMappings</c> (format <see cref="LibraryPathMapping"/>) pour les correspondances choisies.</summary>
    public static string Format(IEnumerable<(Guid LibraryId, string RemoteRoot)> mappings)
    {
        var lines = mappings.Where(item => !string.IsNullOrWhiteSpace(item.RemoteRoot))
            .Select(item => $"{item.LibraryId}={Normalize(item.RemoteRoot)}").ToList();
        var text = string.Join('\n', lines);
        LibraryPathMapping.Parse(text); // même validation que la saisie manuelle (doublons, nombre, format)
        return text;
    }

    private static (int Score, string Reason) Score(LocalLibrary library, RemoteLibrary section, string location)
    {
        var remote = Segments(location);
        var best = (Score: 0, Reason: "");
        foreach (var path in library.LocalPaths)
        {
            var local = Segments(path);
            int score;
            string reason;
            if (local.SequenceEqual(remote, StringComparer.Ordinal) && local.Length > 0)
            {
                score = ExactScore;
                reason = "Même chemin sur le nœud et dans Plex";
            }
            else
            {
                var common = 0;
                while (common < local.Length && common < remote.Length
                       && string.Equals(local[^(common + 1)], remote[^(common + 1)], StringComparison.OrdinalIgnoreCase))
                    common++;
                if (common == 0) continue;
                score = common * SegmentScore;
                reason = common == 1 ? "Même dossier final" : $"{common} dossiers finaux communs";
            }
            if (KindMatches(library.Name, section.Kind))
            {
                score += KindBonus;
                if (score < ExactScore) reason += ", type de bibliothèque concordant";
            }
            if (score > best.Score) best = (score, reason);
        }
        return best;
    }

    private static bool KindMatches(string name, string kind)
    {
        var lower = name.ToLowerInvariant();
        var movies = lower.Contains("film") || lower.Contains("movie");
        var shows = lower.Contains("série") || lower.Contains("serie") || lower.Contains("show") || lower.Contains("tv");
        return kind switch
        {
            "movie" => movies && !shows,
            "show" => shows && !movies,
            _ => false
        };
    }

    private static string[] Segments(string path) => Normalize(path).Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static string Normalize(string path)
    {
        var normalized = path.Trim().Replace('\\', '/');
        while (normalized.Contains("//", StringComparison.Ordinal)) normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        return normalized.Length > 1 ? normalized.TrimEnd('/') : normalized;
    }
}
