namespace Encoding.NodeAgent.Services;

/// <summary>
/// Choix du périphérique de rendu et détection NVIDIA pour l'encodage matériel (architecture 0051).
/// Source unique pour la sonde d'encodeurs et pour les commandes FFmpeg : ne pas coder « renderD128 » en dur ailleurs.
/// </summary>
public static class GpuDeviceSelector
{
    public const string DeviceVariable = "ENCODING_GPU_DEVICE";
    public const string DefaultDevice = "/dev/dri/renderD128";

    /// <summary>
    /// Ordre : ENCODING_GPU_DEVICE (chemin complet ou nom comme « renderD129 »), puis le premier /dev/dri/renderD* par ordre
    /// alphabétique, puis renderD128 pour que les commandes restent valides sans GPU (la sonde échoue alors proprement).
    /// </summary>
    public static string RenderDevice(string? forced = null, string driRoot = "/dev/dri")
    {
        forced ??= Environment.GetEnvironmentVariable(DeviceVariable);
        if (!string.IsNullOrWhiteSpace(forced))
        {
            forced = forced.Trim();
            return forced.Contains('/') ? forced : Path.Combine(driRoot, forced);
        }
        return RenderDevices(driRoot).FirstOrDefault() ?? DefaultDevice;
    }

    public static IReadOnlyList<string> RenderDevices(string driRoot = "/dev/dri")
    {
        try
        {
            if (!Directory.Exists(driRoot)) return [];
            return Directory.EnumerateFileSystemEntries(driRoot, "renderD*")
                .OrderBy(path => path, StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Le pilote propriétaire est monté par le NVIDIA Container Toolkit : /dev/nvidiactl ou nvidia-smi.</summary>
    public static bool NvidiaPresent(string devRoot = "/dev", Func<string, string?>? find = null) =>
        File.Exists(Path.Combine(devRoot, "nvidiactl")) || (find ?? Encoding.NodeAgent.Runtime.ProcessToolRunner.Find)("nvidia-smi") is not null;
}
