using Encoding.Core.Runtime;
using Encoding.NodeAgent.Runtime;
using Encoding.Plugins.Tests;

// Le worker relance son propre exécutable avec --isolated-process pour appliquer Landlock avant de lancer le plugin :
// l’hôte de test joue ce rôle comme le fait le point d’entrée du worker.
if (args.FirstOrDefault() == "--isolated-process") return ProcessSandbox.Run(args);


PluginTests.Run();
PlexSetupTests.Run();
await StoreTests.RunAsync();
HardwareInventoryTests.Run();
GpuDeviceSelectorTests.Run();
await MembershipTests.RunAsync();
await ScriptToolsTests.RunAsync();
await AudioRulesMediaTests.RunAsync();
await TrackNamesMediaTests.RunAsync();
await SubtitlesMediaTests.RunAsync();
FontMatcherTests.Run();

await TriggerTests.RunAsync();
Console.WriteLine("PASS: all plugin and trigger scenarios.");
return 0;
