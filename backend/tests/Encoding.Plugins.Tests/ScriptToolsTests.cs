using Encoding.Core.Runtime;
using System.Text.Json;
using Encoding.NodeAgent.Runtime;
using static Encoding.Plugins.Tests.Support;

namespace Encoding.Plugins.Tests;

/// <summary>Aide <c>tools</c> des scripts base-script (architecture 0054) : JavaScript et Python, avec et sans bac à sable Landlock.</summary>
internal static class ScriptToolsTests
{
    private const string JavascriptCode = """
        const probe = tools.run('ffprobe', ['-version']);
        let blocked = '';
        try { tools.run('curl', ['--version']); } catch (e) { blocked = String(e.message); }
        let missing = tools.run('ffprobe', ['-v', 'error', 'absent.mkv']);
        const made = tools.run('ffmpeg', ['-v', 'error', '-f', 'lavfi', '-i', 'testsrc=duration=1:size=64x64:rate=5', '-f', 'lavfi', '-i', 'sine=duration=1', '-shortest', '-y', 'made.mkv']);
        const info = tools.run('ffprobe', ['-v', 'error', '-show_entries', 'stream=codec_type', '-of', 'csv=p=0', 'made.mkv']);
        return { outputId: 'success', variables: { probeOk: probe.ok, version: probe.stdout.split(/\r?\n/)[0], blocked, missingOk: missing.ok, missingExit: missing.exitCode,
          madeOk: made.ok, streams: info.stdout.trim().split(/\r?\n/).sort(), allowed: tools.allowed.length, ffprobePath: tools.path('ffprobe') !== null, curlPath: tools.path('curl') } };
        """;

    private const string PythonCode = """
        probe = tools.run('ffprobe', ['-version'])
        blocked = ''
        try:
            tools.run('curl', ['--version'])
        except RuntimeError as e:
            blocked = str(e)
        missing = tools.run('ffprobe', ['-v', 'error', 'absent.mkv'])
        made = tools.run('ffmpeg', ['-v', 'error', '-f', 'lavfi', '-i', 'testsrc=duration=1:size=64x64:rate=5', '-f', 'lavfi', '-i', 'sine=duration=1', '-shortest', '-y', 'made.mkv'])
        info = tools.run('ffprobe', ['-v', 'error', '-show_entries', 'stream=codec_type', '-of', 'csv=p=0', 'made.mkv'])
        return {'outputId': 'success', 'variables': {'probeOk': probe['ok'], 'version': probe['stdout'].splitlines()[0], 'blocked': blocked, 'missingOk': missing['ok'], 'missingExit': missing['exitCode'],
          'madeOk': made['ok'], 'streams': sorted(info['stdout'].strip().splitlines()), 'allowed': len(tools.allowed), 'ffprobePath': tools.path('ffprobe') is not None, 'curlPath': tools.path('curl')}}
        """;

    public static async Task RunAsync()
    {
        // Sans useTools, le programme généré reste celui d'avant l'aide : aucun identifiant ajouté aux scripts existants.
        Check(!ScriptTools.Compose(false, false, "return 1;").Contains("const tools", StringComparison.Ordinal), "Without useTools the JavaScript program has no tools helper.");
        Check(!ScriptTools.Compose(true, false, "return None").Contains("_Tools", StringComparison.Ordinal), "Without useTools the Python program has no tools helper.");
        Check(ScriptTools.Compose(false, true, "return 1;").Contains("const tools", StringComparison.Ordinal)
            && ScriptTools.Compose(true, true, "return None").Contains("tools = _Tools()", StringComparison.Ordinal), "With useTools both languages receive the helper.");

        var processes = new ProcessToolRunner();
        var node = ProcessToolRunner.Find("node");
        var python = ProcessToolRunner.Find("python3");
        if (ProcessToolRunner.Find("ffmpeg") is null || ProcessToolRunner.Find("ffprobe") is null || node is null || python is null)
        {
            Console.WriteLine("SKIP: script tools tests require ffmpeg, ffprobe, node and python3.");
            return;
        }

        foreach (var sandbox in new[] { false, true })
        {
            if (sandbox && !ProcessSandbox.IsAvailable)
            {
                Console.WriteLine("SKIP: sandboxed script tools tests require Linux Landlock on this machine.");
                continue;
            }
            foreach (var isPython in new[] { false, true })
            {
                var directory = Path.Combine(Path.GetTempPath(), "encoding-script-tools-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                try
                {
                    var file = Path.Combine(directory, isPython ? "script.py" : "script.js");
                    await File.WriteAllTextAsync(file, ScriptTools.Compose(isPython, true, isPython ? PythonCode : JavascriptCode));
                    var context = JsonSerializer.Serialize(new { variables = new Dictionary<string, object>(), currentFile = "current.mkv" });
                    var label = (isPython ? "Python" : "JavaScript") + (sandbox ? " (Landlock)" : "");
                    var result = await processes.RunAsync(isPython ? python : node, [file], directory, TimeSpan.FromSeconds(120), CancellationToken.None, context, 1048576, sandbox);
                    if (sandbox && result.ExitCode == 79)
                    {
                        Console.WriteLine($"SKIP: {label} — Landlock refuses to apply here (exit 79); run this suite on a host with Landlock.");
                        continue;
                    }
                    Check(result.ExitCode == 0, $"{label}: the script ends normally (exit {result.ExitCode}).");
                    using var document = JsonDocument.Parse(result.Output);
                    var variables = document.RootElement.GetProperty("variables");
                    Check(document.RootElement.GetProperty("outputId").GetString() == "success", $"{label}: the script chooses its output.");
                    Check(variables.GetProperty("probeOk").GetBoolean() && variables.GetProperty("version").GetString()!.StartsWith("ffprobe version", StringComparison.Ordinal), $"{label}: tools.run runs an allowed tool and returns its output.");
                    Check(variables.GetProperty("blocked").GetString()!.Contains("curl", StringComparison.Ordinal), $"{label}: a tool outside the allow list is refused.");
                    Check(variables.GetProperty("curlPath").ValueKind == JsonValueKind.Null && variables.GetProperty("ffprobePath").GetBoolean(), $"{label}: tools.path resolves allowed tools only.");
                    Check(!variables.GetProperty("missingOk").GetBoolean() && variables.GetProperty("missingExit").GetInt32() != 0, $"{label}: a failing tool reports its exit code.");
                    Check(variables.GetProperty("madeOk").GetBoolean() && File.Exists(Path.Combine(directory, "made.mkv")), $"{label}: a tool writes media into the working directory.");
                    Check(variables.GetProperty("streams").EnumerateArray().Select(item => item.GetString()).SequenceEqual(["audio", "video"]), $"{label}: the produced media can be probed.");
                    Check(variables.GetProperty("allowed").GetInt32() == ScriptTools.AllowedTools.Length, $"{label}: the allow list is the documented one.");
                    Console.WriteLine($"PASS: script tools helper, {label}.");
                }
                finally { Directory.Delete(directory, true); }
            }
        }
    }
}
