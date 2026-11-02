using Encoding.NodeAgent.Services;
using static Encoding.Plugins.Tests.Support;

namespace Encoding.Plugins.Tests;

/// <summary>Choix du périphérique de rendu et détection NVIDIA pour l'encodage matériel.</summary>
internal static class GpuDeviceSelectorTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "encoding-gpu-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dri = Path.Combine(root, "dri");
            Directory.CreateDirectory(dri);
            Check(GpuDeviceSelector.RenderDevices(dri).Count == 0, "Sans périphérique, la liste est vide.");
            Check(GpuDeviceSelector.RenderDevice("", Path.Combine(root, "absent")) == GpuDeviceSelector.DefaultDevice,
                "Sans /dev/dri, repli sur renderD128 pour garder des commandes valides.");

            File.WriteAllText(Path.Combine(dri, "renderD129"), "");
            File.WriteAllText(Path.Combine(dri, "renderD128"), "");
            File.WriteAllText(Path.Combine(dri, "card0"), "");
            Check(GpuDeviceSelector.RenderDevices(dri).Select(path => Path.GetFileName(path)).SequenceEqual(new[] { "renderD128", "renderD129" }),
                "Seuls les renderD* sont listés, triés.");
            Check(GpuDeviceSelector.RenderDevice("", dri) == Path.Combine(dri, "renderD128"),
                "Par défaut, le premier renderD* est retenu.");
            Check(GpuDeviceSelector.RenderDevice("renderD129", dri) == Path.Combine(dri, "renderD129"), "Un nom court force le périphérique sous /dev/dri.");
            Check(GpuDeviceSelector.RenderDevice("/dev/dri/renderD130", dri) == "/dev/dri/renderD130", "Un chemin complet est conservé.");

            Check(!GpuDeviceSelector.NvidiaPresent(root, _ => null), "Sans /dev/nvidiactl ni nvidia-smi, NVIDIA est absent.");
            Check(GpuDeviceSelector.NvidiaPresent(root, _ => "/usr/bin/nvidia-smi"), "nvidia-smi suffit à détecter NVIDIA.");
            File.WriteAllText(Path.Combine(root, "nvidiactl"), "");
            Check(GpuDeviceSelector.NvidiaPresent(root, _ => null), "/dev/nvidiactl suffit à détecter NVIDIA.");
        }
        finally { Directory.Delete(root, true); }
    }
}
