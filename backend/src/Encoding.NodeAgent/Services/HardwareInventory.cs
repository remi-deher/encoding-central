using System.Globalization;
using System.Runtime.InteropServices;
using Encoding.Contracts.Nodes;

namespace Encoding.NodeAgent.Services;

/// <summary>
/// Inventaire matériel statique du nœud. Source unique pour l'enrôlement et les battements de cœur.
/// Le modèle de processeur et les GPU sont des informations best-effort ; leur absence n'est jamais une erreur.
/// </summary>
public static class HardwareInventory
{
    private static readonly Dictionary<string, string> Vendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0x1002"] = "AMD",
        ["0x10de"] = "NVIDIA",
        ["0x8086"] = "Intel",
    };

    /// <summary>Base d'identifiants PCI (paquet hwdata / pciutils) ; absente, le GPU garde ses identifiants numériques.</summary>
    private static readonly string[] PciIdsPaths = ["/usr/share/hwdata/pci.ids", "/usr/share/misc/pci.ids", "/usr/share/pci.ids"];

    /// <summary>Lu une seule fois : le matériel ne change pas pendant la vie du processus et la base PCI est volumineuse.</summary>
    private static readonly Lazy<NodeInventory> CurrentInventory = new(() => Read("/proc/cpuinfo", "/sys/class/drm"));

    public static NodeInventory Current() => CurrentInventory.Value with { FontSets = FontInventory.Read(Environment.GetEnvironmentVariable("ENCODING_FONT_DIR") ?? "/app/common/Fonts") };

    public static NodeInventory Read(string cpuInfoPath, string drmRoot) => new(
        RuntimeInformation.OSDescription,
        RuntimeInformation.ProcessArchitecture.ToString(),
        Environment.ProcessorCount,
        ReadMemoryMb(),
        ReadProcessorModel(cpuInfoPath),
        ReadGpus(drmRoot),
        RuntimeEnvironment: DetectRuntimeEnvironment());

    /// <summary>Runtime host/container category advertised in the node inventory.</summary>
    public static string DetectRuntimeEnvironment() => DetectRuntimeEnvironment(
        Environment.GetEnvironmentVariable("container"),
        string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase),
        File.Exists("/.dockerenv"),
        File.Exists("/run/.containerenv"),
        ReadTrimmed("/proc/1/cgroup"));

    public static string DetectRuntimeEnvironment(
        string? container,
        bool dotnetRunningInContainer,
        bool dockerMarker,
        bool podmanMarker,
        string? cgroup)
    {
        if (dockerMarker || string.Equals(container, "docker", StringComparison.OrdinalIgnoreCase) ||
            cgroup?.Contains("docker", StringComparison.OrdinalIgnoreCase) == true)
            return "docker";
        if (podmanMarker || string.Equals(container, "podman", StringComparison.OrdinalIgnoreCase) ||
            cgroup?.Contains("libpod", StringComparison.OrdinalIgnoreCase) == true)
            return "podman";
        if (dotnetRunningInContainer ||
            (!string.IsNullOrWhiteSpace(container) && !string.Equals(container, "none", StringComparison.OrdinalIgnoreCase)) ||
            cgroup?.Contains("kubepods", StringComparison.OrdinalIgnoreCase) == true ||
            cgroup?.Contains("containerd", StringComparison.OrdinalIgnoreCase) == true)
            return "container";
        return "native";
    }

    private static readonly string[] CgroupLimitPaths = ["/sys/fs/cgroup/memory.max", "/sys/fs/cgroup/memory/memory.limit_in_bytes"];

    /// <summary>Mémoire du conteneur (limite cgroup v2 ou v1), bornée par la mémoire de l'hôte ; le plafond du GC n'est pas la mémoire disponible.</summary>
    public static long ReadMemoryMb() => ReadMemoryMb(CgroupLimitPaths, "/proc/meminfo");

    public static long ReadMemoryMb(IEnumerable<string> cgroupLimitPaths, string memInfoPath)
    {
        long? host = null;
        try
        {
            foreach (var line in File.ReadLines(memInfoPath))
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                {
                    var digits = new string(line["MemTotal:".Length..].Where(char.IsDigit).ToArray());
                    if (long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var kb)) host = kb / 1024;
                    break;
                }
        }
        catch (Exception) { }
        long? limit = null;
        foreach (var path in cgroupLimitPaths)
        {
            try
            {
                if (long.TryParse(File.ReadAllText(path).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) && bytes > 0)
                { limit = bytes / (1024 * 1024); break; }
            }
            catch (Exception) { }
        }
        var chosen = (host, limit) switch { ({ } h, { } l) => Math.Min(h, l), ({ } h, null) => h, (null, { } l) => l, _ => (long?)null };
        return chosen ?? Math.Max(0, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024));
    }

    public static string? ReadProcessorModel(string cpuInfoPath)
    {
        try
        {
            foreach (var line in File.ReadLines(cpuInfoPath))
            {
                var separator = line.IndexOf(':');
                if (separator < 0) continue;
                var key = line[..separator].Trim();
                if (key is "model name" or "Model" or "Hardware")
                {
                    var value = line[(separator + 1)..].Trim();
                    if (value.Length > 0) return value.Length > 256 ? value[..256] : value;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    public static IReadOnlyList<NodeGpu> ReadGpus(string drmRoot, IReadOnlyList<string>? pciIdsPaths = null)
    {
        var gpus = new List<NodeGpu>();
        try
        {
            if (!Directory.Exists(drmRoot)) return gpus;
            foreach (var card in Directory.EnumerateDirectories(drmRoot, "card*").OrderBy(p => p, StringComparer.Ordinal))
            {
                if (Path.GetFileName(card).Contains('-')) continue; // connecteurs (card0-HDMI-A-1)
                var device = Path.Combine(card, "device");
                var vendorId = ReadTrimmed(Path.Combine(device, "vendor"));
                var deviceId = ReadTrimmed(Path.Combine(device, "device"));
                if (vendorId is null) continue;
                var vendor = Vendors.GetValueOrDefault(vendorId) ?? vendorId;
                var driver = ReadUeventValue(Path.Combine(device, "uevent"), "DRIVER");
                var friendly = deviceId is null ? null : LookupDeviceName(vendorId, deviceId, pciIdsPaths ?? PciIdsPaths);
                var name = friendly is not null ? $"{vendor} {friendly}" : deviceId is null ? vendor : $"{vendor} {deviceId}";
                gpus.Add(new NodeGpu(name, vendor, driver, Path.GetFileName(card)));
                if (gpus.Count == 16) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return gpus;
    }

    private static string? ReadTrimmed(string path)
    {
        try
        {
            var value = File.ReadAllText(path).Trim();
            return value.Length == 0 ? null : value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? ReadUeventValue(string path, string key)
    {
        try
        {
            var prefix = key + "=";
            foreach (var line in File.ReadLines(path))
                if (line.StartsWith(prefix, StringComparison.Ordinal)) return line[prefix.Length..].Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>
    /// Nom d'un périphérique PCI d'après pci.ids ; le nom commercial entre crochets est préféré
    /// (« CoffeeLake-S GT2 [UHD Graphics 630] » donne « UHD Graphics 630 »). Best-effort : null si introuvable.
    /// </summary>
    public static string? LookupDeviceName(string vendorId, string deviceId, IReadOnlyList<string> pciIdsPaths)
    {
        var vendorKey = vendorId.Replace("0x", "", StringComparison.OrdinalIgnoreCase).Trim().ToLowerInvariant();
        var deviceKey = deviceId.Replace("0x", "", StringComparison.OrdinalIgnoreCase).Trim().ToLowerInvariant();
        foreach (var path in pciIdsPaths)
        {
            try
            {
                if (!File.Exists(path)) continue;
                var inVendor = false;
                foreach (var line in File.ReadLines(path))
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    if (line[0] != '\t')
                    {
                        if (inVendor) return null; // section du fabricant terminée sans le périphérique
                        inVendor = line.StartsWith(vendorKey + "  ", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    if (!inVendor || line.Length < 2 || line[1] == '\t') continue; // sous-systèmes ignorés
                    if (!line.StartsWith("\t" + deviceKey + "  ", StringComparison.OrdinalIgnoreCase)) continue;
                    var name = line[(deviceKey.Length + 3)..].Trim();
                    var open = name.LastIndexOf('[');
                    var close = name.LastIndexOf(']');
                    var friendly = open >= 0 && close > open ? name[(open + 1)..close].Trim() : name;
                    return friendly.Length == 0 ? null : friendly.Length > 128 ? friendly[..128] : friendly;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }
}
