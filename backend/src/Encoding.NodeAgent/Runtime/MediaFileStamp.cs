namespace Encoding.NodeAgent.Runtime;

/// <summary>A cheap change detector, not a content identity or a transfer checksum.</summary>
public sealed record MediaFileStamp(long Bytes, long LastWriteUtcTicks)
{
    public static MediaFileStamp Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new IOException("Le fichier média n’est plus disponible.");
        return new(info.Length, info.LastWriteTimeUtc.Ticks);
    }

    public bool Matches(string path)
    {
        try { return File.Exists(path) && Read(path) == this; }
        catch { return false; }
    }

    public void Verify(string path)
    {
        if (Read(path) != this) throw new IOException("Le fichier média a changé pendant son utilisation.");
    }

    public string ToDigest()
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes($"stamp:{Bytes}:{LastWriteUtcTicks}");
        return Convert.ToHexStringLower(sha.ComputeHash(bytes));
    }
}
