using System.Text.Json;
using Encoding.Api.Flows;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Nodes;
using Encoding.Contracts.Executions;
using Encoding.Api.Plugins;

namespace Encoding.Api.Executions;

public sealed class RuntimeEligibility(
    FlowComponentRegistry registry,
    FlowDefinitionValidator validator,
    ClusterExtensionActivationState? activation = null)
{
    public bool RequiresWriteAccess(FlowDefinition flow,string? nextNodeId=null,bool resumed=false)
    {
        var entry=nextNodeId??(resumed?null:flow.Nodes.Single(node=>registry.Resolve(node.Component)?.Kind=="input").Id);
        return entry is not null && RuntimeHandoffPolicy.Segment(flow,entry)
            .Any(node=>registry.Resolve(node.Component)?.RequiresWriteAccess==true);
    }

    public bool RequiresDirectAccess(FlowDefinition flow,string? nextNodeId=null,bool resumed=false)
    {
        var entry=nextNodeId??(resumed?null:flow.Nodes.Single(node=>registry.Resolve(node.Component)?.Kind=="input").Id);
        return entry is not null && RuntimeHandoffPolicy.Segment(flow,entry)
            .Any(node=>node.Component.PluginId=="nodes" && node.Component.ComponentId=="nodes-require-direct-access"
                && (!node.Configuration.TryGetValue("mode",out var m) || m.ValueKind!=System.Text.Json.JsonValueKind.String || m.GetString()!="prefer"));
    }

    public bool PrefersDirectAccess(FlowDefinition flow,string? nextNodeId=null,bool resumed=false)
    {
        var entry=nextNodeId??(resumed?null:flow.Nodes.Single(node=>registry.Resolve(node.Component)?.Kind=="input").Id);
        return entry is not null && RuntimeHandoffPolicy.Segment(flow,entry)
            .Any(node=>node.Component.PluginId=="nodes" && node.Component.ComponentId=="nodes-require-direct-access"
                && node.Configuration.TryGetValue("mode",out var m) && m.ValueKind==System.Text.Json.JsonValueKind.String && m.GetString()=="prefer");
    }

    /// <summary>Le segment contient-il un bloc qui décide lui-même du nœud (désignation ou accès bibliothèque) ? Sinon, le placement par défaut s'applique (ADR 0024).</summary>
    public bool ChoosesPlacementExplicitly(FlowDefinition flow,string? nextNodeId=null,bool resumed=false)
    {
        var entry=nextNodeId??(resumed?null:flow.Nodes.Single(node=>registry.Resolve(node.Component)?.Kind=="input").Id);
        return entry is not null && RuntimeHandoffPolicy.Segment(flow,entry)
            .Any(node=>node.Component.PluginId=="nodes" && node.Component.ComponentId is "nodes-run-on-node" or "nodes-library-access");
    }

    /// <summary>Une sonde n'exécute qu'une condition de plugin : seule la capacité de ce plugin est requise.</summary>
    public bool CanProbe(FlowDefinition flow,IReadOnlyList<NodeToolCapability> capabilities,string blockNodeId,bool resumed=false)
    {
        var node=flow.Nodes.SingleOrDefault(item=>item.Id==blockNodeId);
        var activationPluginId = node is not null && FlowComponentRegistry.TryGetLegacyAlias(node.Component.ComponentId, out var target)
            ? target.PluginId
            : node?.Component.PluginId;
        return node is not null && registry.Resolve(node.Component) is not null
            && (resumed || activation is null || activation.IsPluginEnabled(activationPluginId!))
            && capabilities.Any(c=>c.ToolId=="plugin/"+node.Component.PluginId
            && (c.Version==node.Component.PluginVersion || c.Version==registry.RuntimePluginVersion(node.Component)));
    }

    public bool CanRun(FlowDefinition flow,IReadOnlyList<NodeToolCapability> capabilities,Guid workerId,string? nextNodeId=null,bool resumed=false,
        RuntimeContext? context=null,NodeIdentityResponse? identity=null,NodeInventory? inventory=null,IReadOnlyDictionary<string,Guid>? placements=null)
    {
        if (MediaIntegrityPolicy.IsExplicit(flow) && !capabilities.Any(c => c.ToolId == MediaIntegrityPolicy.Capability)) return false;
        if(context?.DirectSource is not null && !capabilities.Any(capability=>capability.ToolId==RuntimeSourceObservation.Capability)) return false;
        if(!(resumed ? validator.ValidateContinuation(flow) : validator.Validate(flow)).IsValid) return false;
        if(!resumed && activation is not null)
            capabilities=activation.EnabledCapabilities(capabilities,capability=>capability.ToolId);
        var entry=nextNodeId??(resumed?null:flow.Nodes.Single(node=>registry.Resolve(node.Component)?.Kind=="input").Id);
        if(entry is not null && placements?.TryGetValue(entry,out var assignedWorker)==true && assignedWorker!=workerId) return false;
        // « Bibliothèque sur ce nœud » avec « et encodeur du profil » : un worker sans encodeur utilisable répond « non » (voir le runner),
        // donc la branche « oui » ne lui est pas demandée.
        var canEncode=MediaCodecPolicy.ProfileEncoderUsable(flow,capabilities.FirstOrDefault(capability=>capability.ToolId=="ffmpeg"));
        var nodesById=flow.Nodes.ToDictionary(node=>node.Id,StringComparer.Ordinal);
        bool Follow(FlowConnectionDefinition edge)=>canEncode || edge.SourceOutputId!="yes"
            || !(nodesById[edge.SourceNodeId] is { Component: { PluginId: "nodes", ComponentId: "nodes-library-access" } } access && RuntimeHandoffPolicy.LibraryAccessNeedsEncoder(access));
        bool FollowPlanned(FlowConnectionDefinition edge) => Follow(edge)
            && (placements is null || !placements.TryGetValue(edge.TargetNodeId,out var target) || target==workerId);
        var segment=entry is null?Array.Empty<FlowNodeDefinition>():RuntimeHandoffPolicy.Segment(flow,entry,FollowPlanned);
        var planned=(context?.Plan??[]).Select((step,index)=> {
            var refTarget = registry.FindReference(step.ComponentId)
                ?? new FlowComponentReference("base","1.0.0",step.ComponentId,"1.0.0",1);
            return new FlowNodeDefinition("plan-"+index,null,refTarget,step.Configuration);
        });
        foreach(var node in segment.Concat(planned))
        {
            if(RuntimeHandoffPolicy.IsHandoff(node) && node.Configuration.TryGetValue("mode",out var handoffMode) && handoffMode.GetString()=="server"
                && !capabilities.Any(c=>c.ToolId=="plugin/nodes" && Version.TryParse(c.Version,out var version) && version>=new Version(1,8,0))) return false;
            var pluginId = node.Component.PluginId;
            var componentId = node.Component.ComponentId;
            var isAliased = FlowComponentRegistry.TryGetLegacyAlias(componentId, out var legacyTarget);
            var targetPluginId = isAliased ? legacyTarget.PluginId : pluginId;
            var component=registry.Resolve(node.Component);

            var matches = capabilities.Any(c =>
                (!isAliased && c.ToolId == "plugin/" + pluginId && registry.FingerprintMatches(pluginId, c.ManifestFingerprint)
                    && (c.Version == node.Component.PluginVersion || c.Version == registry.RuntimePluginVersion(node.Component)
                    || (pluginId is "audio" or "video" or "subtitles") && component?.CompatiblePluginVersions?.Contains(c.Version,
                    StringComparer.Ordinal) == true))
                || (pluginId == "ai" && (componentId is "ai-translate-subtitles" or "ai-metadata-enricher") && c.ToolId == "ai-runtime" && c.Version == "1.0.0")
                || (isAliased && c.ToolId == "plugin/" + targetPluginId)
                || (isAliased && c.ToolId == "plugin/base" && IsBaseVersionSatisfied(c.Version, componentId))
                || (pluginId == "base" && !isAliased && c.ToolId == "plugin/base" && component?.CompatiblePluginVersions?.Contains(c.Version,StringComparer.Ordinal)==true)
                || (node.Component.PluginId is "video" or "audio" or "subtitles" or "mkv" or "images" or "probe" && c.ToolId == "plugin/base" && IsAtLeast(c.Version, "1.6.0")));

            if (!matches) return false;
            if (componentId is "base-add-fonts" or "subtitles-add-fonts"
                || componentId=="base-assemble-media" && node.Configuration.TryGetValue("addFonts",out var addFonts) && addFonts.ValueKind==JsonValueKind.True)
            {
                // Une ressource du catalogue est récupérée par le worker lui-même : aucun jeu de polices local n'est exigé.
                var fromResource = node.Configuration.TryGetValue("resource", out var resource) && resource.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(resource.GetString());
                var fontSet = node.Configuration.TryGetValue("fontSet", out var configuredSet) && configuredSet.ValueKind == JsonValueKind.String ? configuredSet.GetString() : "Crunchyroll";
                if (!fromResource && inventory?.FontSets?.Contains(fontSet ?? "", StringComparer.Ordinal) != true) return false;
            }
            if (node.Configuration.TryGetValue("sourceFile", out var fileReference) && fileReference.ValueKind == JsonValueKind.String
                && fileReference.GetString()!.StartsWith(FlowArtifactReference.Prefix, StringComparison.Ordinal)
                && !capabilities.Any(capability => capability.ToolId == FlowArtifactReference.Capability && IsAtLeast(capability.Version, "1.0.0"))) return false;
            if (registry.MinimumRuntime(node.Component, node.Configuration) is { } runtimeRequirement
                && !capabilities.Any(capability => capability.ToolId == "plugin/" + runtimeRequirement.PluginId && IsAtLeast(capability.Version, runtimeRequirement.Version))) return false;
            if(node.Component.PluginId=="nodes" && node.Component.ComponentId=="nodes-run-on-node")
            {
                if(node.Configuration.TryGetValue("targetNodeId",out var target) && target.ValueKind==System.Text.Json.JsonValueKind.String
                    && Guid.TryParse(target.GetString(),out var targetId) && targetId!=workerId) return false;
                if(node.Configuration.TryGetValue("targetGroupId",out var group) && group.ValueKind==System.Text.Json.JsonValueKind.String
                    && (!Guid.TryParse(group.GetString(),out var groupId) || identity is null || !identity.GroupIds.Contains(groupId))) return false;
                if(node.Configuration.TryGetValue("requiredLabels",out var labels) && labels.ValueKind==System.Text.Json.JsonValueKind.String
                    && !Encoding.Contracts.Nodes.NodeLabelSelector.Matches(labels.GetString(),identity?.EffectiveLabels)) return false;
            }
            if(node.Component.PluginId=="nodes" && node.Component.ComponentId=="nodes-require-capability")
            {
                var toolId=node.Configuration.TryGetValue("toolId",out var requestedTool)?requestedTool.GetString():null;
                var minimum=node.Configuration.TryGetValue("minimumVersion",out var requestedVersion)?requestedVersion.GetString():null;
                var required=node.Configuration.TryGetValue("requiredCapability",out var requestedCapability)?requestedCapability.GetString():null;
                var encoder=node.Configuration.TryGetValue("requiredEncoder",out var requestedEncoder)?requestedEncoder.GetString():null;
                if(toolId is null || !capabilities.Any(capability=>capability.ToolId==toolId
                    && (string.IsNullOrWhiteSpace(minimum) || Version.TryParse(capability.Version,out var actual) && Version.TryParse(minimum,out var min) && actual>=min)
                    && (string.IsNullOrWhiteSpace(required) || capability.AccelerationTypes.Contains(required,StringComparer.Ordinal)))) return false;
                var kind=node.Configuration.TryGetValue("encoderKind",out var requestedKind)?requestedKind.GetString():null;
                if(kind=="profile" && !MediaCodecPolicy.ProfileEncoderUsable(flow,capabilities.FirstOrDefault(capability=>capability.ToolId=="ffmpeg"))) return false;
                var kindOnly=kind is "hardware" or "cpu";
                if(!string.IsNullOrWhiteSpace(encoder) || kindOnly)
                {
                    var ffmpeg = capabilities.FirstOrDefault(c => string.Equals(c.ToolId, "ffmpeg", StringComparison.OrdinalIgnoreCase));
                    if (ffmpeg is null) return false;
                    var sought = encoder?.Trim() ?? "";
                    // Type d'encodage : « matériel » exclut le CPU, « cpu » exclut tout accélérateur.
                    bool KindMatches(string acceleration) => kind switch { "hardware" => acceleration != "cpu", "cpu" => acceleration == "cpu", _ => true };
                    var matching = Encoding.Contracts.Flows.VideoEncoderCatalog.All
                        .Where(desc => (sought.Length == 0 || string.Equals(desc.Codec, sought, StringComparison.OrdinalIgnoreCase) || string.Equals(desc.Id,
                            sought, StringComparison.OrdinalIgnoreCase)) && KindMatches(desc.Acceleration))
                        .ToList();
                    if (sought.Length == 0 && matching.Count == 0) return false;
                    var usable = ffmpeg.EncoderChecks?.Where(c => c.Status == "usable").Select(c => c.EncoderId) ?? [];
                    var av = ffmpeg.Encoders ?? [];
                    var supported = matching.Count > 0
                        ? matching.Any(desc => usable.Contains(desc.Id, StringComparer.OrdinalIgnoreCase) || (ffmpeg.EncoderChecks is null && av.Contains(desc.Id, StringComparer.OrdinalIgnoreCase)))
                        : (ffmpeg.EncoderChecks?.Any(c => c.Status == "usable" && string.Equals(c.EncoderId, sought, StringComparison.OrdinalIgnoreCase)) == true)
                          || av.Contains(sought, StringComparer.OrdinalIgnoreCase);
                    if (!supported) return false;
                }
            }
            if(component is null) return false;
            if(node.Component.PluginId=="base" && node.Component.ComponentId=="base-prepare-processing"
                && !capabilities.Any(c=>c.ToolId==(node.Configuration.TryGetValue("tool",out var chosen)?chosen.GetString():"ffmpeg"))) return false;
            if(node.Component.PluginId=="base" && node.Component.ComponentId=="base-execute-processing"
                && !capabilities.Any(c=>c.ToolId=="ffmpeg" || c.ToolId=="mkvtoolnix")) return false;
            foreach(var tool in component.RequiredTools)
                if(!capabilities.Any(c=>c.ToolId==tool.ToolId && (tool.MinimumVersion is null || Version.TryParse(c.Version,out var actual)
                    && Version.TryParse(tool.MinimumVersion,out var minimum) && actual>=minimum)
                    && tool.RequiredCapabilities.All(feature=>c.AccelerationTypes.Contains(feature)))) return false;
            var retryWithCpu=node.Configuration.TryGetValue("retryWithCpu",out var retryValue)&&retryValue.ValueKind==System.Text.Json.JsonValueKind.True;
            if(node.Configuration.TryGetValue("hardwareAcceleration",out var acceleration) && acceleration.GetString() is { } name && name!="cpu"&&!retryWithCpu
                && !capabilities.Any(c=>c.ToolId=="ffmpeg" && c.AccelerationTypes.Contains(name))) return false;
            if(node.Component.ComponentId=="base-script" && node.Configuration.TryGetValue("language",out var language)
                && !capabilities.Any(c=>c.ToolId==(language.GetString()=="python"?"python":"javascript"))) return false;
            if(node.Component.ComponentId=="base-script" && node.Configuration.TryGetValue("useTools",out var useTools) && useTools.ValueKind==System.Text.Json.JsonValueKind.True
                && !capabilities.Any(c=>c.ToolId=="script-tools")) return false;
            if(node.Component.ComponentId=="base-condition" && !capabilities.Any(c=>c.ToolId=="javascript")) return false;
            var encoders=capabilities.FirstOrDefault(c=>c.ToolId=="ffmpeg")?.Encoders??[];
            if(node.Component.ComponentId is "base-transcode-video" or "video-transcode")
            {
                var codec=node.Configuration.TryGetValue("videoCodec",out var codecValue)?codecValue.GetString()!:"hevc";
                if(node.Configuration.TryGetValue("encodingProfile",out var profileValue)
                    && profileValue.ValueKind==System.Text.Json.JsonValueKind.Object)
                {
                    var profile=profileValue.Deserialize<EncodingProfileSnapshot>(new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
                    var mode=node.Configuration.TryGetValue("encoderMode",out var modeValue)?modeValue.GetString()!:"automatic";
                    var selected=node.Configuration.TryGetValue("encoderId",out var encoderValue)?encoderValue.GetString():null;
                    var ffmpeg=capabilities.FirstOrDefault(capability=>capability.ToolId=="ffmpeg");
                    if(profile is null || ffmpeg is null) return false;
                    var usable=retryWithCpu
                        ?MediaCodecPolicy.ResolveCpuFallbackVideoEncoder(codec,profile,ffmpeg.Encoders??[],ffmpeg.AccelerationTypes,ffmpeg.EncoderChecks) is not null
                        :MediaCodecPolicy.ResolveVideoEncoder(codec,mode,selected,profile,ffmpeg.Encoders??[],ffmpeg.AccelerationTypes,ffmpeg.EncoderChecks) is not null;
                    if(!usable) return false;
                }
                else
                {
                    var hardware=node.Configuration.TryGetValue("hardwareAcceleration",out var hw)?hw.GetString()!:"cpu";
                    if(retryWithCpu&&hardware!="cpu")
                    {
                        if(MediaCodecPolicy.VideoEncoder(codec,"cpu",encoders) is null) return false;
                    }
                    else if(MediaCodecPolicy.VideoEncoder(codec,hardware,encoders) is null) return false;
                }
            }
            if (node.Component.ComponentId == "audio-convert-by-rules" && node.Configuration.TryGetValue("rules", out var audioRules) && audioRules.ValueKind == JsonValueKind.Array
                && audioRules.EnumerateArray().Any(rule => MediaTrackSelection.Text(rule, "action") != "copy"
                    && MediaCodecPolicy.AudioEncoder(MediaTrackSelection.Text(rule, "codec"), encoders) is null)) return false;
            if((node.Component.ComponentId is "base-audio-rules" or "audio-rules") && (MediaCodecPolicy.AudioEncoder("aac",encoders) is null
                || MediaCodecPolicy.AudioEncoder("ac3",encoders) is null)) return false;
            if((node.Component.ComponentId is "base-transcode-audio" or "audio-transcode") && node.Configuration.TryGetValue("audioCodec",out var audio)
                && MediaCodecPolicy.AudioEncoder(audio.GetString()!,encoders) is null) return false;
        }
        // Every media execution records a source scan before completion, even if no transformation is selected.
        return capabilities.Any(c=>c.ToolId=="ffprobe");
    }

    private static bool IsBaseVersionSatisfied(string actualVersion, string componentId)
    {
        if (!Version.TryParse(actualVersion, out var actual)) return true;
        var minVersion = componentId switch
        {
            "base-audio-rules" => new Version(1, 2, 0),
            "base-track-names" => new Version(1, 3, 0),
            "base-subtitle-manage" or "base-add-fonts" => new Version(1, 4, 0),
            "base-file-size-guard" or "base-auto-crop" or "base-deinterlace-video" or "base-filter-languages" or "base-remove-commentary" => new Version(1, 6, 0),
            _ => new Version(1, 0, 0)
        };
        return actual >= minVersion;
    }

    private static bool IsAtLeast(string actualVersion, string minimumVersion) =>
        Version.TryParse(actualVersion, out var actual)
        && Version.TryParse(minimumVersion, out var minimum)
        && actual >= minimum;
}
