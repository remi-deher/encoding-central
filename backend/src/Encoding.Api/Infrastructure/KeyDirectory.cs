namespace Encoding.Api.Infrastructure;

/// <summary>Dossier du trousseau Data Protection du central (réglages des plugins, connexions IA, sessions IA).</summary>
public static class KeyDirectory
{
    public static string Resolve(IConfiguration configuration, string? defaultRoot = null)
    {
        if (configuration["PluginSettings:KeyDirectory"] is { Length: > 0 } configured)
            return Path.GetFullPath(configured);
        if (configuration["Worker:DataDirectory"] is { Length: > 0 } dataDirectory)
            return Path.Combine(Path.GetFullPath(dataDirectory), "plugin-keys");
        return Path.Combine(defaultRoot ?? AppContext.BaseDirectory, "data", "plugin-keys");
    }
}
