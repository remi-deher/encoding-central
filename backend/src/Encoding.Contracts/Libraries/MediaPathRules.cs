namespace Encoding.Contracts.Libraries;

public static class MediaPathRules
{
    public static string Normalize(string? path)
    {
        if(string.IsNullOrWhiteSpace(path)||path.Length>2048||Path.IsPathRooted(path)||path.Contains(':')||path.Contains('\0')) throw new ArgumentException("Le média doit être un chemin relatif à la bibliothèque.");
        var normalized=path.Replace('\\','/');
        if(normalized.Split('/').Any(segment=>segment is "" or "." or "..")) throw new ArgumentException("Le chemin relatif du média est invalide.");
        return normalized;
    }
}
