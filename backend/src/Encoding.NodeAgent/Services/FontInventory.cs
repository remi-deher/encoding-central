namespace Encoding.NodeAgent.Services;

/// <summary>Observed set names only; absence of an inventory is distinct from an empty directory.</summary>
public static class FontInventory
{
    public static IReadOnlyList<string>? Read(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return [];
            return Directory.EnumerateDirectories(root)
                .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                .Where(path => Directory.EnumerateFiles(path).Any(file =>
                    (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0
                    && new[] { ".ttf", ".otf", ".ttc" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)))
                .Select(Path.GetFileName).OfType<string>()
                .Where(name => name.Length <= 128 && !name.Contains("..", StringComparison.Ordinal)
                    && System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9 ._-]*$"))
                .Order(StringComparer.Ordinal).Take(64).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }
}
