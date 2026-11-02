using Encoding.Contracts.Flows;
using Encoding.Contracts.Flows.Conditions;
using Encoding.Contracts.Plugins;
using System.Text.Json;

namespace Encoding.Contracts.Executions;

/// <summary>Closed list of native operations that never modify a file or require durable artifacts.</summary>
public static class DirectMediaInspection
{
    public static bool AllowsMetadataOnly(FlowComponentDescriptor? component) =>
        component is { MediaEffect:MediaEffects.ReadOnly,RequiresMedia:false,ProducesFiles:false };

    public static bool Allows(string plugin, string component, IReadOnlyDictionary<string,JsonElement>? configuration = null, int pendingPlanSteps = 0,
        bool builderActive = false) => (builderActive && plugin is "base" or "video" or "audio" && FfmpegBuilderPolicy.IsSetting(component))
        || plugin=="base" && component==FfmpegBuilderPolicy.Start
        || plugin=="base" && component==FfmpegBuilderPolicy.Execute && pendingPlanSteps==0
        || plugin is "base" or "probe" && (component.StartsWith("base-probe-",StringComparison.Ordinal)
            || component is "probe-media" or "probe-tracks" or "probe-video" or "probe-audio" or "probe-hdr")
        || plugin is "base" or "subtitles" && (component is "base-track-names" or "subtitles-track-names" or "subtitles-snapshot-track-titles")
            && configuration?.TryGetValue("mode",out var namingMode)==true && namingMode.ValueKind==JsonValueKind.String && namingMode.GetString()=="snapshot"
        || plugin switch
    {
        "conditions" => component=="conditions-match" || MediaConditionEvaluator.IsMediaCondition(component),
        "base" => component is "base-input-file" or "base-input-video" or "base-input-audio" or "base-input-music"
            or "base-input-image" or "base-input-subtitles" or "base-variable" or "base-complete-flow" or "base-defer-processing" or "base-prepare-processing"
            || component=="base-execute-processing" && pendingPlanSteps==0 && !(configuration?.TryGetValue("forceProcessing",out var force)==true && force.ValueKind==JsonValueKind.True)
            || component=="base-audio-rules" && IsPlanned(configuration),
        "audio" => component is "audio-rules" or "audio-convert-by-rules" && IsPlanned(configuration),
        "nodes" => component is "nodes-require-direct-access" or "nodes-library-access" or "nodes-require-capability" or "nodes-run-on-node",
        _ => false
    };

    private static bool IsPlanned(IReadOnlyDictionary<string,JsonElement>? configuration) =>
        configuration?.TryGetValue("processingMode",out var mode)!=true || mode.ValueKind==JsonValueKind.String && mode.GetString()=="plan";
}
