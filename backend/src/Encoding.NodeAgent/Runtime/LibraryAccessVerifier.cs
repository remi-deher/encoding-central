namespace Encoding.NodeAgent.Runtime;

public sealed class LibraryAccessException(string operation, string path)
    : IOException($"Accès effectif refusé ({operation}) dans « {path} ». Vérifiez les droits du processus et le montage du nœud.");

/// <summary>Tests effective process permissions using only dedicated, randomly named temporary files.</summary>
public static class LibraryAccessVerifier
{
    public static async Task VerifyPublicationAsync(string root, CancellationToken ct, string? replacementMediaPath = null)
    {
        if (!Directory.Exists(root)) throw new LibraryAccessException("montage indisponible", root);
        var results = SafeMediaPaths.Resolve(root, ".encoding-results");
        var parent = Directory.Exists(results) ? results : root;
        await VerifyDirectoryAsync(parent, ct);
        if (replacementMediaPath is not null)
        {
            var source = SafeMediaPaths.Resolve(root, replacementMediaPath);
            var sourceParent = Path.GetDirectoryName(source)!;
            if (!Directory.Exists(sourceParent)) throw new LibraryAccessException("dossier source indisponible", sourceParent);
            if (!string.Equals(sourceParent, parent, StringComparison.Ordinal)) await VerifyDirectoryAsync(sourceParent, ct);
        }
    }

    private static async Task VerifyDirectoryAsync(string parent, CancellationToken ct)
    {
        var check = SafeMediaPaths.Resolve(parent, ".encoding-access-check-" + Guid.NewGuid().ToString("N"));
        var operation = "création du répertoire temporaire";
        var created = false;
        try
        {
            Directory.CreateDirectory(check);
            created = true;
            operation = "écriture";
            var source = Path.Combine(check, "source");
            var target = Path.Combine(check, "renamed");
            var expected = System.Security.Cryptography.RandomNumberGenerator.GetBytes(64);
            await File.WriteAllBytesAsync(source, expected, ct);
            operation = "relecture";
            if (!(await File.ReadAllBytesAsync(source, ct)).SequenceEqual(expected))
                throw new LibraryAccessException(operation, parent);
            operation = "renommage";
            File.Move(source, target);
            operation = "nettoyage";
            File.Delete(target);
            Directory.Delete(check);
            created = false;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            throw new LibraryAccessException(operation, parent);
        }
        finally
        {
            // Only our own two probe files may be removed, never recurse through arbitrary content.
            if (created)
            {
                foreach (var file in new[] { "source", "renamed" })
                {
                    try { File.Delete(Path.Combine(check, file)); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }
                try { Directory.Delete(check); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
