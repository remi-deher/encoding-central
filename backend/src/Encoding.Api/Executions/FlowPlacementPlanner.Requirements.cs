using System.Text.Json;
using Encoding.Api.Flows;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Flows.Conditions;
using Encoding.Contracts.Nodes;

namespace Encoding.Api.Executions;

public sealed partial class FlowPlacementPlanner
{
    public IReadOnlyList<FlowPathRequirements> ExtractPathRequirements(FlowDefinition flow, JsonElement? sourceMetadata, IReadOnlyList<NodePlacementCandidate>? candidates = null)
    {
        var inputNode = flow.Nodes.FirstOrDefault(n => registry.Resolve(n.Component)?.Kind == "input");
        if (inputNode is null) return [];

        var outgoing = flow.Connections.ToLookup(c => c.SourceNodeId);
        var nodesById = flow.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);

        var paths = new List<List<FlowNodeDefinition>>();
        var currentPath = new List<FlowNodeDefinition>();
        var visitedInCurrentPath = new HashSet<string>(StringComparer.Ordinal);

        void Explore(string nodeId)
        {
            if (!nodesById.TryGetValue(nodeId, out var node)) return;
            if (!visitedInCurrentPath.Add(nodeId)) return;
            currentPath.Add(node);

            var edges = outgoing[nodeId].ToList();
            if (edges.Count == 0)
            {
                paths.Add(new(currentPath));
            }
            else
            {
                // Élagage des conditions si les métadonnées de la source sont disponibles
                string? filterOutput = null;
                if (sourceMetadata is { } meta && node.Component.PluginId == "conditions" && MediaConditionEvaluator.IsMediaCondition(node.Component.ComponentId))
                {
                    try
                    {
                        filterOutput = MediaConditionEvaluator.Evaluate(node.Component.ComponentId, node.Configuration, meta);
                    }
                    catch
                    {
                        filterOutput = null;
                    }
                }
                else if (sourceMetadata is { } matchMeta && node.Component.PluginId == "conditions" && node.Component.ComponentId == "conditions-match"
                    && node.Configuration.TryGetValue("rules", out var rulesJson))
                {
                    try
                    {
                        var tree = ConditionTree.Parse(rulesJson, flow.MediaType);
                        if (IsPureMediaCondition(tree))
                        {
                            var ctx = new ConditionEvaluationContext(
                                new("", ".mkv", new Dictionary<string, JsonElement>(StringComparer.Ordinal), matchMeta, matchMeta, []),
                                Guid.Empty, Guid.Empty, null, "", "");
                            filterOutput = ConditionTree.Evaluate(tree, ctx) ? "yes" : "no";
                        }
                    }
                    catch
                    {
                        filterOutput = null;
                    }
                }
                else if (candidates is not null && node.Component.PluginId == "nodes" && node.Component.ComponentId == "nodes-library-access" && RuntimeHandoffPolicy.LibraryAccessNeedsEncoder(node))
                {
                    // Si aucun nœud à accès direct ne possède d'encodeur matériel compatible avec le profil du flow,
                    // la branche « oui » ne sera jamais empruntée par le nœud source direct : ne pas l'imposer.
                    var anyDirectCanEncode = candidates.Any(c =>
                        c.IsOnline && c.IsWorker
                        && c.Access is { IsEnabled: true, Mode: "direct", CanRead: true, LocalPath: not null }
                        && MediaCodecPolicy.ProfileEncoderUsable(flow, c.Capabilities.FirstOrDefault(cap => cap.ToolId == "ffmpeg")));
                    if (!anyDirectCanEncode) filterOutput = "no";
                }

                var relevantEdges = filterOutput is not null
                    ? edges.Where(e => e.SourceOutputId == filterOutput).ToList()
                    : edges;

                if (relevantEdges.Count == 0)
                {
                    paths.Add(new(currentPath));
                }
                else
                {
                    foreach (var edge in relevantEdges)
                    {
                        Explore(edge.TargetNodeId);
                    }
                }
            }

            currentPath.RemoveAt(currentPath.Count - 1);
            visitedInCurrentPath.Remove(nodeId);
        }

        Explore(inputNode.Id);

        return paths.Select(path=>BuildRequirements(path,sourceMetadata)).ToList();
    }

    private static bool IsPureMediaCondition(ConditionExpression tree) => tree switch
    {
        ConditionGroup group => group.Children.All(IsPureMediaCondition),
        ConditionRule rule => rule.Field.StartsWith("video.", StringComparison.Ordinal)
            || rule.Field.StartsWith("audio.", StringComparison.Ordinal)
            || rule.Field.StartsWith("subtitles.", StringComparison.Ordinal)
            || rule.Field is "file.duration" or "file.size",
        _ => false
    };

    private FlowPathRequirements BuildRequirements(List<FlowNodeDefinition> nodes,JsonElement? sourceMetadata)
    {
        var nodeIds = nodes.Select(n => n.Id).ToList();
        var requiresDirect = false;
        var prefersDirect = false;
        var requiresWrite = false;
        var tools = new HashSet<string>(StringComparer.Ordinal) { "ffprobe" };
        var plugins = new HashSet<string>(StringComparer.Ordinal);
        var videoTranscodes = new List<VideoTranscodeRequirement>();
        var audioEncoders = new HashSet<string>(StringComparer.Ordinal);
        var fontSets = new HashSet<string>(StringComparer.Ordinal);
        var runConstraints = new List<NodeRunConstraint>();
        var capabilityConstraints = new List<CapabilityConstraint>();

        foreach (var node in nodes)
        {
            var component = registry.Resolve(node.Component);
            if (component?.RequiresWriteAccess == true) requiresWrite = true;

            plugins.Add("plugin/" + node.Component.PluginId);

            if (node.Component.PluginId == "nodes" && node.Component.ComponentId == "nodes-require-direct-access")
            {
                if (node.Configuration.TryGetValue("mode", out var m) && m.ValueKind == JsonValueKind.String && m.GetString() == "prefer")
                    prefersDirect = true;
                else
                    requiresDirect = true;
            }

            if (component is not null)
            {
                foreach (var tool in component.RequiredTools)
                {
                    tools.Add(tool.ToolId);
                    foreach (var requiredCapability in tool.RequiredCapabilities)
                        capabilityConstraints.Add(new(tool.ToolId,tool.MinimumVersion,requiredCapability,null,null));
                    if(tool.RequiredCapabilities.Count==0 && tool.MinimumVersion is not null)
                        capabilityConstraints.Add(new(tool.ToolId,tool.MinimumVersion,null,null,null));
                }
            }

            if (node.Component.PluginId == "base" && node.Component.ComponentId is "base-execute-processing" or "base-ffmpeg-builder-execute")
            {
                tools.Add("ffmpeg");
            }

            if (node.Component.ComponentId == "base-script")
            {
                var lang = node.Configuration.TryGetValue("language", out var l) ? l.GetString() : "python";
                tools.Add(lang == "javascript" ? "javascript" : "python");
                if (node.Configuration.TryGetValue("useTools", out var ut) && ut.ValueKind == JsonValueKind.True)
                    tools.Add("script-tools");
            }

            if (node.Component.ComponentId == "base-condition")
            {
                tools.Add("javascript");
            }

            if (node.Component.ComponentId is "base-transcode-video" or "video-transcode")
            {
                tools.Add("ffmpeg");
                var codec = node.Configuration.TryGetValue("videoCodec", out var cVal) ? cVal.GetString()! : "hevc";
                var mode = node.Configuration.TryGetValue("encoderMode", out var mVal) ? mVal.GetString()! : "automatic";
                var encoderId = node.Configuration.TryGetValue("encoderId", out var eVal) ? eVal.GetString() : null;
                var retryCpu = node.Configuration.TryGetValue("retryWithCpu", out var rVal) && rVal.ValueKind == JsonValueKind.True;
                var hw = node.Configuration.TryGetValue("hardwareAcceleration", out var hVal) ? hVal.GetString() : "cpu";
                EncodingProfileSnapshot? profile = null;
                if (node.Configuration.TryGetValue("encodingProfile", out var pVal) && pVal.ValueKind == JsonValueKind.Object)
                {
                    try { profile = pVal.Deserialize<EncodingProfileSnapshot>(Json); } catch { }
                }
                videoTranscodes.Add(new(codec, mode, encoderId, profile, hw, retryCpu));
            }

            if (node.Component.ComponentId is "base-audio-rules" or "audio-rules")
            {
                tools.Add("ffmpeg");
                // Audio rules are input-dependent. Before the source scan, admit a capable scanner;
                // the first metadata report replans against the exact conversions before they run.
                if(sourceMetadata is { } metadata)
                {
                    var audioPlan=AudioRulePlanner.Plan(metadata,AudioRuleOptions.From(node.Configuration));
                    foreach(var conversion in audioPlan.Conversions)
                        if(conversion.Codec is not null) audioEncoders.Add(conversion.Codec);
                }
            }
            else if (node.Component.ComponentId is "base-transcode-audio" or "audio-transcode")
            {
                tools.Add("ffmpeg");
                if (node.Configuration.TryGetValue("audioCodec", out var aCodec) && aCodec.ValueKind == JsonValueKind.String)
                    audioEncoders.Add(aCodec.GetString()!);
            }

            if (node.Component.ComponentId is "base-add-fonts" or "subtitles-add-fonts"
                || (node.Component.ComponentId == "base-assemble-media" && node.Configuration.TryGetValue("addFonts", out var af) && af.ValueKind == JsonValueKind.True))
            {
                var fromResource = node.Configuration.TryGetValue("resource", out var resource) && resource.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(resource.GetString());
                if (!fromResource)
                {
                    var fontSet = node.Configuration.TryGetValue("fontSet", out var configuredSet) && configuredSet.ValueKind == JsonValueKind.String
                        ? configuredSet.GetString()! : "Crunchyroll";
                    fontSets.Add(fontSet);
                }
            }

            if (node.Component.PluginId == "nodes" && node.Component.ComponentId == "nodes-run-on-node")
            {
                Guid? tNode = null;
                if (node.Configuration.TryGetValue("targetNodeId", out var tn) && Guid.TryParse(tn.GetString(), out var tid)) tNode = tid;
                Guid? tGroup = null;
                if (node.Configuration.TryGetValue("targetGroupId", out var tg) && Guid.TryParse(tg.GetString(), out var gid)) tGroup = gid;
                var reqLabels = node.Configuration.TryGetValue("requiredLabels", out var rl) ? rl.GetString() : null;
                runConstraints.Add(new(tNode, tGroup, reqLabels));
            }

            if (node.Component.PluginId == "nodes" && node.Component.ComponentId == "nodes-require-capability")
            {
                var tool = node.Configuration.TryGetValue("toolId", out var rt) ? rt.GetString() : "ffmpeg";
                var min = node.Configuration.TryGetValue("minimumVersion", out var mv) ? mv.GetString() : null;
                var reqCap = node.Configuration.TryGetValue("requiredCapability", out var rc) ? rc.GetString() : null;
                var reqEnc = node.Configuration.TryGetValue("requiredEncoder", out var re) ? re.GetString() : null;
                var kind = node.Configuration.TryGetValue("encoderKind", out var ek) ? ek.GetString() : null;
                if (tool is not null) capabilityConstraints.Add(new(tool, min, reqCap, reqEnc, kind));
            }

        }

        return new(
            nodeIds,
            requiresDirect,
            prefersDirect,
            requiresWrite,
            tools,
            plugins,
            videoTranscodes,
            audioEncoders,
            fontSets,
            runConstraints,
            capabilityConstraints);
    }

    public bool NodeSatisfiesPath(NodePlacementCandidate node, FlowPathRequirements path, out List<string> missing)
    {
        missing = [];

        if (path.RequiresDirectAccess && node.Access is not { Mode: "direct", CanRead: true, LocalPath: not null })
            missing.Add("accès direct local requis");

        if (path.RequiresWriteAccess && node.Access is not { CanWrite: true })
            missing.Add("accès en écriture requis");

        foreach (var tool in path.RequiredTools)
        {
            if (!node.Capabilities.Any(c => c.ToolId == tool))
                missing.Add($"outil manquant : {tool}");
        }

        bool HasPlugin(string pluginReq)
        {
            if (node.Capabilities.Any(c => c.ToolId == pluginReq)) return true;
            var rawId = pluginReq.StartsWith("plugin/", StringComparison.Ordinal) ? pluginReq["plugin/".Length..] : pluginReq;
            if (rawId is "video" or "audio" or "subtitles" or "mkv" or "images" or "probe")
            {
                var baseCap = node.Capabilities.FirstOrDefault(c => c.ToolId == "plugin/base");
                if (baseCap is not null && Version.TryParse(baseCap.Version, out var bv) && bv >= new Version(1, 6, 0))
                    return true;
            }
            return false;
        }

        foreach (var plugin in path.RequiredPlugins)
        {
            if (!HasPlugin(plugin))
                missing.Add($"plugin manquant : {plugin}");
        }

        if (!NodeSatisfiesTranscodeRequirements(node, path))
            missing.Add("encodeurs vidéo incompatibles");

        var ffmpeg = node.Capabilities.FirstOrDefault(c => c.ToolId == "ffmpeg");
        var availableEncoders = ffmpeg?.Encoders ?? [];
        foreach (var audio in path.AudioEncoders)
        {
            if (MediaCodecPolicy.AudioEncoder(audio, availableEncoders) is null)
                missing.Add($"encodeur audio manquant : {audio}");
        }

        foreach (var fontSet in path.FontSets)
        {
            if (node.Inventory?.FontSets?.Contains(fontSet, StringComparer.Ordinal) != true)
                missing.Add($"jeu de polices manquant : {fontSet}");
        }

        foreach (var constraint in path.RunConstraints)
        {
            if (constraint.TargetNodeId.HasValue && constraint.TargetNodeId.Value != node.NodeId)
                missing.Add($"nœud cible imposé {constraint.TargetNodeId.Value:D}");
            if (constraint.TargetGroupId.HasValue && (node.Identity is null || !node.Identity.GroupIds.Contains(constraint.TargetGroupId.Value)))
                missing.Add($"groupe imposé {constraint.TargetGroupId.Value:D}");
            if (!string.IsNullOrWhiteSpace(constraint.RequiredLabels) && !NodeLabelSelector.Matches(constraint.RequiredLabels, node.Identity?.EffectiveLabels))
                missing.Add($"étiquettes imposées {constraint.RequiredLabels}");
        }

        foreach (var cap in path.CapabilityConstraints)
        {
            var matching = node.Capabilities.FirstOrDefault(c => c.ToolId == cap.ToolId);
            if (matching is null)
            {
                missing.Add($"capacité d'outil absente : {cap.ToolId}");
                continue;
            }
            if (!string.IsNullOrWhiteSpace(cap.MinimumVersion)
                && (!Version.TryParse(matching.Version, out var v) || !Version.TryParse(cap.MinimumVersion, out var min) || v < min))
                missing.Add($"version minimale non satisfaite pour {cap.ToolId} : >= {cap.MinimumVersion}");
            if (!string.IsNullOrWhiteSpace(cap.RequiredCapability) && !matching.AccelerationTypes.Contains(cap.RequiredCapability, StringComparer.Ordinal))
                missing.Add($"accélération requise absente : {cap.RequiredCapability}");
            if(!string.IsNullOrWhiteSpace(cap.RequiredEncoder) || cap.EncoderKind is "hardware" or "cpu")
            {
                var encoderTool=node.Capabilities.FirstOrDefault(capability=>capability.ToolId=="ffmpeg");
                var sought=cap.RequiredEncoder?.Trim();
                var encoderOptions=VideoEncoderCatalog.All.Where(encoder=>(string.IsNullOrWhiteSpace(sought)
                    || encoder.Codec.Equals(sought,StringComparison.OrdinalIgnoreCase) || encoder.Id.Equals(sought,StringComparison.OrdinalIgnoreCase))
                    && (cap.EncoderKind switch { "hardware"=>encoder.Acceleration!="cpu", "cpu"=>encoder.Acceleration=="cpu", _=>true })).ToArray();
                var usable=encoderTool?.EncoderChecks?.Where(check=>check.Status=="usable").Select(check=>check.EncoderId).ToArray();
                if(encoderTool is null || encoderOptions.Length==0 || !encoderOptions.Any(encoder=>usable?.Contains(encoder.Id,StringComparer.OrdinalIgnoreCase)
                    ?? encoderTool.Encoders?.Contains(encoder.Id,StringComparer.OrdinalIgnoreCase)==true))
                    missing.Add($"encodeur requis absent ou non utilisable : {cap.RequiredEncoder ?? cap.EncoderKind}");
            }
        }

        return missing.Count == 0;
    }

    private static bool NodeSatisfiesTranscodeRequirements(NodePlacementCandidate node, FlowPathRequirements path)
    {
        if (path.VideoTranscodes.Count == 0) return true;
        var ffmpeg = node.Capabilities.FirstOrDefault(c => c.ToolId == "ffmpeg");
        if (ffmpeg is null) return false;

        var availableEncoders = ffmpeg.Encoders ?? [];
        var accelerations = ffmpeg.AccelerationTypes ?? [];

        foreach (var transcode in path.VideoTranscodes)
        {
            if (transcode.Profile is not null)
            {
                var usable = MediaCodecPolicy.ResolveVideoEncoder(
                    transcode.Codec,
                    transcode.Mode,
                    transcode.EncoderId,
                    transcode.Profile,
                    availableEncoders,
                    accelerations,
                    ffmpeg.EncoderChecks) is not null
                    || (transcode.RetryWithCpu && MediaCodecPolicy.ResolveCpuFallbackVideoEncoder(
                        transcode.Codec,
                        transcode.Profile,
                        availableEncoders,
                        accelerations,
                        ffmpeg.EncoderChecks) is not null);
                if (!usable) return false;
            }
            else
            {
                var hw = transcode.HardwareAcceleration ?? "cpu";
                if (transcode.RetryWithCpu && hw != "cpu")
                {
                    if (MediaCodecPolicy.VideoEncoder(transcode.Codec, "cpu", availableEncoders) is null
                        && MediaCodecPolicy.VideoEncoder(transcode.Codec, hw, availableEncoders) is null)
                        return false;
                }
                else
                {
                    if (MediaCodecPolicy.VideoEncoder(transcode.Codec, hw, availableEncoders) is null)
                        return false;
                }
            }
        }

        return true;
    }

    private static IReadOnlyList<string> CollectCapabilitiesNames(IReadOnlyList<FlowPathRequirements> paths)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in paths)
        {
            foreach (var t in p.RequiredTools) result.Add("tool:" + t);
            foreach (var pl in p.RequiredPlugins) result.Add(pl);
            foreach (var v in p.VideoTranscodes) result.Add("video:" + v.Codec + (v.HardwareAcceleration != null ? ":" + v.HardwareAcceleration : ""));
            foreach (var a in p.AudioEncoders) result.Add("audio:" + a);
            foreach (var f in p.FontSets) result.Add("fonts:" + f);
        }
        return result.OrderBy(s => s, StringComparer.Ordinal).ToList();
    }
}
