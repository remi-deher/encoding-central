namespace Encoding.Contracts.Plugins;

/// <summary>
/// Correspondance entre la racine d’une bibliothèque Encoding et le chemin du même dossier vu par un service
/// externe (Sonarr, Radarr…). Définition unique partagée par le déclencheur serveur et les actions des plugins.
/// Format de configuration : une ligne par bibliothèque, <c>identifiant-bibliothèque=/chemin/vu/par/le/service</c>.
/// </summary>
public sealed record LibraryPathMapping(Guid LibraryId, string RemoteRoot)
{
    public const int MaxMappings = 64;

    public static IReadOnlyList<LibraryPathMapping> Parse(string? text)
    {
        var result = new List<LibraryPathMapping>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0 || !Guid.TryParse(line[..separator].Trim(), out var library))
                throw new FormatException("Chaque ligne doit suivre le format « identifiant-bibliothèque=/chemin/du/service ».");
            var root = NormalizeRemote(line[(separator + 1)..]);
            if (root.Length == 0) throw new FormatException("La racine du service ne peut pas être vide.");
            if (result.Any(item => item.LibraryId == library || item.RemoteRoot == root))
                throw new FormatException("Une bibliothèque ou une racine de service est déclarée deux fois.");
            result.Add(new(library, root));
            if (result.Count > MaxMappings) throw new FormatException("Trop de correspondances de chemins.");
        }
        return result;
    }

    /// <summary>Retourne la bibliothèque et le chemin relatif d’un fichier vu par le service, ou null s’il est hors de toute racine.</summary>
    public static (Guid LibraryId, string RelativePath)? ToLibrary(IReadOnlyList<LibraryPathMapping> mappings, string? remotePath)
    {
        if (string.IsNullOrWhiteSpace(remotePath)) return null;
        var path = NormalizeRemote(remotePath);
        foreach (var mapping in mappings.OrderByDescending(item => item.RemoteRoot.Length))
        {
            var prefix = mapping.RemoteRoot.TrimEnd('/') + "/";
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var relative = path[prefix.Length..];
            var segments = relative.Split('/');
            if (segments.Any(segment => segment is "" or "." or "..") || relative.Contains('\0')) return null;
            return (mapping.LibraryId, relative);
        }
        return null;
    }

    /// <summary>Chemin vu par le service pour un fichier de la bibliothèque, ou null si elle n’est pas associée.</summary>
    public static string? ToRemote(IReadOnlyList<LibraryPathMapping> mappings, Guid libraryId, string relativePath)
    {
        var mapping = mappings.FirstOrDefault(item => item.LibraryId == libraryId);
        return mapping is null ? null : mapping.RemoteRoot.TrimEnd('/') + "/" + relativePath.Replace('\\', '/');
    }

    private static string NormalizeRemote(string path)
    {
        var normalized = path.Trim().Replace('\\', '/');
        while (normalized.Contains("//", StringComparison.Ordinal)) normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        return normalized.Length > 1 ? normalized.TrimEnd('/') : normalized;
    }
}
