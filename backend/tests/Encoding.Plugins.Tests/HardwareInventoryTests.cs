using Encoding.NodeAgent.Services;
using static Encoding.Plugins.Tests.Support;

namespace Encoding.Plugins.Tests;

/// <summary>Inventaire matériel (CPU, GPU) : lecture best-effort, jamais bloquante.</summary>
internal static class HardwareInventoryTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "encoding-hw-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cpuInfo = Path.Combine(root, "cpuinfo");
            Directory.CreateDirectory(root);
            File.WriteAllText(cpuInfo, "processor\t: 0\nmodel name\t: AMD Ryzen 7 5700G\n\nprocessor\t: 1\nmodel name\t: AMD Ryzen 7 5700G\n");
            Check(HardwareInventory.ReadProcessorModel(cpuInfo) == "AMD Ryzen 7 5700G", "Le modèle de processeur est lu une fois.");
            Check(HardwareInventory.ReadProcessorModel(Path.Combine(root, "absent")) is null, "Un fichier absent donne un modèle inconnu, sans erreur.");

            var memInfo = Path.Combine(root, "meminfo");
            File.WriteAllText(memInfo, "MemTotal:       16384000 kB\nMemFree:  1 kB\n");
            var v2 = Path.Combine(root, "memory.max");
            File.WriteAllText(v2, "2147483648\n");
            Check(HardwareInventory.ReadMemoryMb([v2], memInfo) == 2048, "La limite cgroup du conteneur est la mémoire déclarée.");
            File.WriteAllText(v2, "max\n");
            Check(HardwareInventory.ReadMemoryMb([v2], memInfo) == 16000, "Sans limite cgroup, la mémoire de l'hôte est utilisée.");
            File.WriteAllText(v2, "9223372036854771712\n");
            Check(HardwareInventory.ReadMemoryMb([v2], memInfo) == 16000, "Une limite supérieure à l'hôte est bornée par l'hôte.");

            var drm = Path.Combine(root, "drm");
            var device = Path.Combine(drm, "card0", "device");
            Directory.CreateDirectory(device);
            Directory.CreateDirectory(Path.Combine(drm, "card0-HDMI-A-1", "device"));
            File.WriteAllText(Path.Combine(device, "vendor"), "0x1002\n");
            File.WriteAllText(Path.Combine(device, "device"), "0x1638\n");
            File.WriteAllText(Path.Combine(device, "uevent"), "DRIVER=amdgpu\nPCI_ID=1002:1638\n");
            var gpus = HardwareInventory.ReadGpus(drm, []);
            Check(gpus.Count == 1, "Les connecteurs ne sont pas comptés comme des GPU.");
            Check(gpus[0].Vendor == "AMD" && gpus[0].Driver == "amdgpu" && gpus[0].Device == "card0", "Le GPU expose fabricant, pilote et périphérique.");
            Check(gpus[0].Name == "AMD 0x1638", "Sans base PCI, le GPU garde ses identifiants numériques.");

            var pciIds = Path.Combine(root, "pci.ids");
            File.WriteAllText(pciIds, "# commentaire\n8086  Intel Corporation\n\t3e92  CoffeeLake-S GT2 [UHD Graphics 630]\n\t\t1028 0000  Sous-système ignoré\n1002  Advanced Micro Devices, Inc. [AMD/ATI]\n\t1638  Cezanne [Radeon Vega Series]\n\t6749  Turks PRO\n");
            Check(HardwareInventory.LookupDeviceName("0x8086", "0x3e92", [pciIds]) == "UHD Graphics 630", "Le nom commercial entre crochets est préféré.");
            Check(HardwareInventory.LookupDeviceName("0x1002", "0x6749", [pciIds]) == "Turks PRO", "Sans crochets, le nom complet est utilisé.");
            Check(HardwareInventory.LookupDeviceName("0x1002", "0xffff", [pciIds]) is null, "Un périphérique inconnu reste sans nom.");
            Check(HardwareInventory.LookupDeviceName("0x8086", "0x3e92", [Path.Combine(root, "absent")]) is null, "Une base absente n'est pas une erreur.");
            Check(HardwareInventory.ReadGpus(drm, [pciIds])[0].Name == "AMD Radeon Vega Series", "Le GPU lu utilise le nom de la base PCI.");
            Check(HardwareInventory.ReadGpus(Path.Combine(root, "absent")).Count == 0, "Sans GPU, la liste est vide.");
        }
        finally { Directory.Delete(root, true); }
    }
}
