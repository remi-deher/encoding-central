using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Encoding.Api.Infrastructure;

/// <summary>One versioned SQL migration read from disk. <see cref="Checksum"/> ignores line-ending differences.</summary>
public sealed record MigrationFile(string Version, int Number, string Path, string Checksum);

/// <summary>Reads and validates the migration directory. The rules here are the single definition of a valid migration set.</summary>
public static partial class MigrationCatalog
{
    /// <summary>
    /// Migrations renamed after being published: new version → version already recorded by existing databases.
    /// A database that recorded the old version treats the renamed file as applied instead of running it twice.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> RenamedFrom = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["0036_cluster_reconciliation"] = "0035_cluster_reconciliation",
        ["0037_library_cluster_tombstones"] = "0036_library_cluster_tombstones",
    };

    [GeneratedRegex("^(?<number>[0-9]{4})_[a-z0-9]+(_[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    /// <summary>Returns the bundled SQLite migration directory, falling back to the source tree during development.</summary>
    public static string ResolveDirectory(string contentRoot)
    {
        var outputDirectory = Path.Combine(AppContext.BaseDirectory, "migrations", "sqlite");
        var directory = Directory.Exists(outputDirectory) ? outputDirectory : Path.Combine(contentRoot, "migrations", "sqlite");
        return Directory.Exists(directory) ? directory : throw new DirectoryNotFoundException($"Dossier de migrations introuvable : {directory}");
    }

    /// <summary>Lists migrations in application order. Throws when a name is malformed or a number is used twice.</summary>
    public static IReadOnlyList<MigrationFile> Load(string directory)
    {
        var files = new List<MigrationFile>();
        foreach (var path in Directory.GetFiles(directory, "*.sql").Order(StringComparer.Ordinal))
        {
            var version = Path.GetFileNameWithoutExtension(path);
            var match = VersionPattern().Match(version);
            if (!match.Success)
                throw new InvalidOperationException($"Nom de migration invalide : {Path.GetFileName(path)}. Format attendu : NNNN_description_en_minuscules.sql.");
            files.Add(new(version, int.Parse(match.Groups["number"].Value, System.Globalization.CultureInfo.InvariantCulture), path, Checksum(File.ReadAllText(path))));
        }
        var duplicate = files.GroupBy(file => file.Number).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Le numéro de migration {duplicate.Key:D4} est utilisé plusieurs fois : {string.Join(", ", duplicate.Select(file => Path.GetFileName(file.Path)))}.");
        return files;
    }

    public static string Checksum(string sql) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sql.Replace("\r\n", "\n", StringComparison.Ordinal))));
}
