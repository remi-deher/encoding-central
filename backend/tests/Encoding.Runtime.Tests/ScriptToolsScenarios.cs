using System.Text.Json;
using Encoding.Api.Executions;
using Encoding.Api.Flows;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Nodes;

namespace Encoding.Runtime.Tests;

/// <summary>Éligibilité des scripts qui demandent l'aide tools (architecture 0054).</summary>
internal static class ScriptToolsScenarios
{
    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public static void Run(FlowComponentRegistry registry, FlowDefinitionValidator validator, RuntimeEligibility eligibility)
    {
        var baseManifest = registry.Plugins.Single(plugin => plugin.Id == "base");
        var input = baseManifest.Components.Single(component => component.Id == "base-input-file");
        var script = baseManifest.Components.Single(component => component.Id == "base-script");
        var field = script.ConfigurationSchema.Fields.Single(item => item.Id == "useTools");
        Check(field.Type == "boolean", "The script component declares the useTools switch.");
        var timeout = script.ConfigurationSchema.Fields.Single(item => item.Id == "timeoutSeconds");
        Check(timeout.Max >= 86400, "A script may run long media commands.");

        FlowDefinition FlowWith(bool useTools) => new(1, Guid.NewGuid(), 1, "Script tools", "video", [
            new("input", null, new("base", "1.0.0", input.Id, input.Version, 1), new Dictionary<string, JsonElement>()),
            new("script", null, new("base", "1.0.0", script.Id, script.Version, 1),
                new Dictionary<string, JsonElement> { ["language"] = J("javascript"), ["code"] = J("return {};"), ["useTools"] = J(useTools) })],
            [new("edge", "input", "success", "script")]);

        var worker = Guid.NewGuid();
        IReadOnlyList<NodeToolCapability> older = [new("plugin/base", "1.0.0", ["general"], []), new("javascript", "22", ["general"], []), new("ffprobe", "7.0", ["video"], [])];
        IReadOnlyList<NodeToolCapability> newer = [.. older, new("script-tools", "1", ["general"], [])];
        Check(validator.Validate(FlowWith(true)).IsValid, "A script with useTools is a valid flow.");
        Check(eligibility.CanRun(FlowWith(false), older, worker), "A script without tools still runs on an older worker.");
        Check(!eligibility.CanRun(FlowWith(true), older, worker), "A script that needs tools is never given to a worker without the helper.");
        Check(eligibility.CanRun(FlowWith(true), newer, worker), "A worker that announces script-tools can run it.");
    }
}
