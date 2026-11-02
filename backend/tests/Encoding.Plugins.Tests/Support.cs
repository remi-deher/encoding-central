using System.Text.Json;

namespace Encoding.Plugins.Tests;

internal static class Support
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value, Json);

    public static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>Racine du dépôt : les plugins Python et le catalogue sont lus tels que livrés.</summary>
    public static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Racine du dépôt introuvable.");
    }
}
