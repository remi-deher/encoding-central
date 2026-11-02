using System.Security.Cryptography;
using System.Text;

namespace Encoding.Core.Nodes;

/// <summary>
/// Secret d'enrôlement et de gestion du nœud local de l'image unique (architectures 0037 et 0044). Le lanceur le génère à chaque démarrage dans un
/// fichier du dossier de données du nœud (lisible par lui seul) ; le serveur et l'agent du même conteneur le lisent.
/// Il vit dans un fichier et non dans l'environnement afin que les scripts et plugins sous bac à sable ne l'héritent pas.
/// </summary>
public static class LocalEnrollment
{
    public const string SecretFileSetting = "Node:LocalEnrollmentSecretFile";
    public const string HeaderName = "X-Local-Enrollment-Secret";
    public const string FileName = "local-enrollment.secret";

    /// <summary>Crée un nouveau secret aléatoire et le publie de façon atomique (droits propriétaire seul).</summary>
    public static void Create(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, overwrite: true);
    }

    public static string? Read(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Vrai si le secret fourni est celui du fichier ; comparaison à durée constante.</summary>
    public static bool Matches(string? path, string? provided)
    {
        var expected = Read(path);
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(provided)) return false;
        return CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(provided));
    }

    /// <summary>Le secret n'est jamais envoyé qu'à un serveur joignable en boucle locale (même conteneur).</summary>
    public static bool IsLoopback(string apiBaseUrl) =>
        Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var uri) && (uri.IsLoopback);
}
