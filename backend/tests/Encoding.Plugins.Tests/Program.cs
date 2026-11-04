using Encoding.Core.Runtime;
using Encoding.WorkerAgent.Runtime;
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
// Opt-out local : les suites média (ffmpeg, MKVToolNix, scripts) sont déléguées à la CI. ENCODING_TEST_REQUIRE_MEDIA_TOOLS=1 l'emporte.
if (Environment.GetEnvironmentVariable("ENCODING_TEST_SKIP_MEDIA") == "1" && Environment.GetEnvironmentVariable("ENCODING_TEST_REQUIRE_MEDIA_TOOLS") != "1")
    Console.WriteLine("SKIP: media suites delegated to CI (ENCODING_TEST_SKIP_MEDIA=1).");
else
{
    await ScriptToolsTests.RunAsync();
    await AudioRulesMediaTests.RunAsync();
    await TrackNamesMediaTests.RunAsync();
    await SubtitlesMediaTests.RunAsync();
}
FontMatcherTests.Run();

await TriggerTests.RunAsync();
Console.WriteLine("PASS: all plugin and trigger scenarios.");
return 0;
