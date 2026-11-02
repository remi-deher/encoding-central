using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Encoding.Api.Resources;

/// <summary>
/// Magasin local adressé par empreinte SHA-256 des fichiers de ressources (ADR 0006). Chaque serveur a le sien ;
/// seules les métadonnées sont réconciliées. Un fichier n'est publié dans le magasin qu'après contrôle de son
/// contenu : jamais de chemin issu de l'utilisateur, le nom sur disque est l'empreinte.
/// </summary>
public sealed class ResourceBlobStore(IConfiguration configuration)
{
    public const long MaxFileBytes = 20L * 1024 * 1024;
    public const long MaxResourceBytes = 100L * 1024 * 1024;
    public const int MaxFilesPerVersion = 64;

    private static readonly string[] FontExtensions = [".ttf", ".otf", ".ttc"];

    private readonly string root = Path.GetFullPath(configuration["Resources:Directory"]
        ?? (configuration["Node:DataDirectory"] is { Length: > 0 } dataDir ? Path.Combine(dataDir, "resources") : Path.Combine(AppContext.BaseDirectory, "resources")));

    public string Root => root;

    public static bool ValidDigest(string? digest) => digest is { Length: 64 } && Regex.IsMatch(digest, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    public string PathFor(string digest)
    {
        if (!ValidDigest(digest)) throw new ArgumentException("Empreinte SHA-256 invalide.");
        return Path.Combine(root, digest[..2], digest);
    }

    /// <summary>Fichier de transfert en cours (reprise par <c>Range</c>) ; le nom ne se confond pas avec un fichier publié.</summary>
    public string PartialPathFor(string digest)
    {
        if (!ValidDigest(digest)) throw new ArgumentException("Empreinte SHA-256 invalide.");
        return Path.Combine(root, "partial", digest + ".part");
    }

    /// <summary>Publie un fichier déjà vérifié sous son empreinte, sans écraser un fichier existant.</summary>
    public void Publish(string verifiedFile, string digest)
    {
        var target = PathFor(digest);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target)) File.Delete(verifiedFile);
        else File.Move(verifiedFile, target);
    }

    public bool Exists(string digest) => ValidDigest(digest) && File.Exists(PathFor(digest));

    public Stream OpenRead(string digest) => new FileStream(PathFor(digest), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

    /// <summary>Normalise un nom de fichier et refuse tout ce qui ressemble à un chemin ou à un type non autorisé.</summary>
    public static string NormalizeName(string? name, string kind)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length is 0 or > 128 || trimmed.Any(char.IsControl) || trimmed.IndexOfAny(['/', '\\', ':']) >= 0 || trimmed is "." or "..")
            throw new ResourceException("Nom de fichier invalide (128 caractères au plus, sans chemin).");
        var allowed = kind == "font" ? FontExtensions : [];
        if (!allowed.Contains(Path.GetExtension(trimmed), StringComparer.OrdinalIgnoreCase))
            throw new ResourceException($"Extension non autorisée : seuls {string.Join(", ", allowed)} sont acceptés pour ce type.");
        return trimmed;
    }

    public async Task<ResourceFile> PutAsync(Stream source, string fileName, string kind, CancellationToken ct)
    {
        var name = NormalizeName(fileName, kind);
        Directory.CreateDirectory(root);
        var temporary = Path.Combine(root, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            long length = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                int count;
                while ((count = await source.ReadAsync(buffer, ct)) > 0)
                {
                    length += count;
                    if (length > MaxFileBytes) throw new ResourceException("Le fichier dépasse 20 Mio.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                }
                await output.FlushAsync(ct);
                output.Flush(true);
            }
            if (length == 0) throw new ResourceException("Le fichier est vide.");
            await CheckSignatureAsync(temporary, kind, ct);
            var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
            var target = PathFor(digest);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target)) File.Delete(temporary);
            else File.Move(temporary, target);
            return new(name, digest, length);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Delete(string digest)
    {
        if (!ValidDigest(digest)) return;
        var path = PathFor(digest);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>Le contenu doit correspondre au type déclaré : l'extension seule ne prouve rien.</summary>
    private static async Task CheckSignatureAsync(string path, string kind, CancellationToken ct)
    {
        var header = new byte[4];
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16, FileOptions.Asynchronous);
        var read = await file.ReadAsync(header, ct);
        if (kind == "font" && read == 4 && IsFontSignature(header)) return;
        throw new ResourceException("Le contenu n'est pas une police TrueType ou OpenType valide.");
    }

    private static bool IsFontSignature(byte[] h) =>
        h is [0x00, 0x01, 0x00, 0x00] || h is [(byte)'O', (byte)'T', (byte)'T', (byte)'O']
        || h is [(byte)'t', (byte)'t', (byte)'c', (byte)'f'] || h is [(byte)'t', (byte)'r', (byte)'u', (byte)'e'];
}
