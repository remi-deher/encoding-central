using System.Text.Json;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Plugins;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows.Conditions;
using Encoding.Api.Plugins;

namespace Encoding.Api.Flows;

public sealed class FlowDefinitionValidator(
    FlowComponentRegistry registry,
    ClusterExtensionActivationState? activation = null)
{
    public FlowValidationResult ValidateDraft(FlowDefinition flow)
    {
        // A disabled plugin may remain in an editable draft; publication and execution are checked separately.
        var result=Validate(flow,true,false);
        var allowed=new HashSet<string>(StringComparer.Ordinal){"node.unreachable","component.unavailable","graph.entry.count","connection.node.unknown",
            "connection.output.unknown","node.constraint.conflict","wait.invalid","wait.block.invalid","builder.scope.invalid","builder.scope.unclosed"};
        var issues=result.Issues.Where(issue=>!allowed.Contains(issue.Code)
            && !(issue.Code=="field.invalid" && (issue.Message=="Ce paramètre est obligatoire."
                || issue.Path.EndsWith("/targetNodeId",StringComparison.Ordinal)
                || issue.Path.EndsWith("/minimumVersion",StringComparison.Ordinal)))).ToList();
        if(flow.Nodes?.Any(node=>node is null || node.Component is null || node.Configuration is null)==true)
            issues.Add(new("draft.shape.invalid","/nodes","Chaque bloc doit conserver sa référence et sa configuration."));
        return new(issues);
    }
    private static readonly JsonSerializerOptions DocumentOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public (FlowDefinition? Definition, FlowValidationResult Result) ValidateDocument(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object || document.GetRawText().Length > 1048576)
            return (null, new([new("document.invalid", "/", "La définition doit être un objet JSON de moins de 1 Mo.")]));
        try
        {
            var definition = document.Deserialize<FlowDefinition>(DocumentOptions);
            return definition is null
                ? (null, new([new("document.required", "/", "Définition obligatoire.")]))
                : (definition, Validate(definition));
        }
        catch (JsonException)
        {
            return (null, new([new("document.invalid", "/", "Le document ne respecte pas le contrat de définition de flow.")]));
        }
    }

    public FlowValidationResult Validate(FlowDefinition flow)=>Validate(flow,false,true);

    /// <summary>Valide une suite déjà lancée sans appliquer les préférences d'activation aux plugins.</summary>
    public FlowValidationResult ValidateContinuation(FlowDefinition flow)=>Validate(flow,false,false);

    private FlowValidationResult Validate(FlowDefinition flow,bool allowIncompleteConditions,bool checkPluginActivation)
    {
        var issues = new List<FlowValidationIssue>();
        void Issue(string code, string path, string message) => issues.Add(new(code, path, message));
        if (flow.FormatVersion != 1) Issue("format.unsupported", "/formatVersion", "Format de flow non pris en charge.");
        if (flow.Id == Guid.Empty) Issue("flow.id.required", "/id", "L’identifiant est obligatoire.");
        if (flow.Version <= 0) Issue("flow.version.invalid", "/version", "La version doit être positive.");
        if (string.IsNullOrWhiteSpace(flow.Name) || flow.Name.Length > 128) Issue("flow.name.invalid", "/name", "Le nom doit contenir entre 1 et 128 caractères.");
        if (flow.MediaType is not ("video" or "image" or "music" or "subtitles")) Issue("flow.media.invalid", "/mediaType", "Type de média inconnu.");
        if (flow.Nodes is null || flow.Connections is null)
        {
            Issue("graph.required", "/", "Les listes nodes et connections sont obligatoires.");
            return new(issues);
        }
        if (flow.Nodes.Count > 1000 || flow.Connections.Count > 4000)
        {
            Issue("graph.tooLarge", "/", "Maximum : 1000 blocs et 4000 connexions.");
            return new(issues);
        }
        var nodes = new Dictionary<string, FlowComponentDescriptor>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < flow.Nodes.Count; i++)
        {
            var node = flow.Nodes[i];
            var path = $"/nodes/{i}";
            if (node is null || string.IsNullOrWhiteSpace(node.Id)) { Issue("node.id.required", path + "/id", "Identifiant de bloc obligatoire."); continue; }
            if (!ids.Add(node.Id)) { Issue("node.id.duplicate", path + "/id", "Identifiant de bloc dupliqué."); continue; }
            var reference = node.Component;
            var component = reference is null ? null : registry.Resolve(reference);
            if (component is null) { Issue("component.unavailable", path + "/component", "Cette version du composant, du plugin ou du schéma n’est pas disponible."); continue; }
            var pluginId = FlowComponentRegistry.TryGetLegacyAlias(reference!.ComponentId, out var legacyTarget)
                ? legacyTarget.PluginId
                : reference.PluginId;
            if (checkPluginActivation && activation is not null && !activation.IsPluginEnabled(pluginId))
                Issue("plugin.disabled", path + "/component", "Ce plugin est désactivé pour les nouveaux traitements du cluster.");
            nodes.Add(node.Id, component);
            if (!component.SupportedFlowMediaTypes.Contains(flow.MediaType, StringComparer.Ordinal)) Issue("component.incompatible", path + "/component",
                "Composant incompatible avec le média du flow.");
            if (node.Configuration is null) { Issue("configuration.required", path + "/configuration", "La configuration doit être un objet."); continue; }
            foreach (var key in node.Configuration.Keys)
                if (!component.ConfigurationSchema.Fields.Any(f => f.Id == key)) Issue("field.unknown", path + "/configuration/" + Pointer(key), "Champ inconnu.");
            foreach (var field in component.ConfigurationSchema.Fields)
            {
                node.Configuration.TryGetValue(field.Id, out var value);
                var message = ValidateField(field, value, flow.MediaType,allowIncompleteConditions);
                if (message is not null) Issue("field.invalid", path + "/configuration/" + Pointer(field.Id), message);
            }
            if (node.Component?.ComponentId == "audio-convert-by-rules")
            {
                try { AudioRuleOptions.From(node.Configuration); }
                catch (AudioRuleException error) { Issue("field.invalid", path + "/configuration/rules", error.Message); }
            }
            if (node.Configuration.TryGetValue("styleProfile", out var style) && style.ValueKind == JsonValueKind.String && style.GetString() == "custom")
            {
                try { AssStyleSettings.From(node.Configuration); }
                catch (SubtitleException error) { Issue("field.invalid", path + "/configuration/fontFamily", error.Message); }
            }
            if ((node.Component?.PluginId is "base" or "video") && (node.Component.ComponentId is "base-transcode-video" or "video-transcode"))
            {
                var profile = TryGetEncodingProfile(node.Configuration);
                var codec = node.Configuration.TryGetValue("videoCodec", out var codecValue)
                    ? codecValue.GetString() : "hevc";
                var targets = profile?.Targets ?? [];
                if (profile is not null && !targets.Any(target => VideoEncoderCatalog.Find(target.EncoderId)?.Codec == codec))
                    Issue("field.invalid", path + "/configuration/encodingProfile", "Le profil ne contient aucun réglage pour le codec choisi.");
                var mode = node.Configuration.TryGetValue("encoderMode", out var modeValue)
                    ? modeValue.GetString() : "automatic";
                if (profile is not null && mode == "specific")
                {
                    var encoderId = node.Configuration.TryGetValue("encoderId", out var encoderValue)
                        ? encoderValue.GetString() : null;
                    if (encoderId is null || !targets.Any(target => target.EncoderId == encoderId)
                        || VideoEncoderCatalog.Find(encoderId)?.Codec != codec)
                        Issue("field.invalid", path + "/configuration/encoderId", "L’encodeur précis doit posséder un réglage dans le profil et produire le codec choisi.");
                }
            }
            if (node.Component!.PluginId == "nodes" && node.Component.ComponentId == "nodes-run-on-node")
            {
                var exact = node.Configuration.TryGetValue("targetNodeId", out var targetNode)
                    && targetNode.ValueKind == JsonValueKind.String && Guid.TryParse(targetNode.GetString(), out _);
                var group = node.Configuration.TryGetValue("targetGroupId", out var targetGroup)
                    && targetGroup.ValueKind == JsonValueKind.String && Guid.TryParse(targetGroup.GetString(), out _);
                var labels = node.Configuration.TryGetValue("requiredLabels", out var labelSelector)
                    && labelSelector.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(labelSelector.GetString());
                if (!exact && !group && !labels)
                    Issue("field.invalid", path + "/configuration", "Définissez un nœud, un groupe ou au moins une étiquette requise.");
                if (node.Configuration.ContainsKey("targetNodeId") && !exact)
                    Issue("field.invalid", path + "/configuration/targetNodeId", "L’identifiant du nœud doit être un UUID valide.");
                if (node.Configuration.ContainsKey("targetGroupId") && !group)
                    Issue("field.invalid", path + "/configuration/targetGroupId", "L’identifiant du groupe doit être un UUID valide.");
                if (node.Configuration.TryGetValue("requiredLabels", out labelSelector)
                    && (labelSelector.ValueKind != JsonValueKind.String
                        || !string.IsNullOrWhiteSpace(labelSelector.GetString()) && !Encoding.Contracts.Nodes.NodeLabelSelector.IsValid(labelSelector.GetString())))
                    Issue("field.invalid", path + "/configuration/requiredLabels", "Utilisez au plus 64 étiquettes sous la forme clé ou clé=valeur.");
            }
            if (node.Component.PluginId == "nodes" && node.Component.ComponentId == "nodes-require-capability"
                && node.Configuration.TryGetValue("minimumVersion", out var minimumVersion)
                && minimumVersion.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(minimumVersion.GetString())
                && !System.Version.TryParse(minimumVersion.GetString(), out _))
                Issue("field.invalid", path + "/configuration/minimumVersion", "Utilisez une version numérique, par exemple 7.1.0.");
        }
        var entries = nodes.Where(n => n.Value.Kind == "input").Select(n => n.Key).ToArray();
        if (entries.Length != 1) Issue("graph.entry.count", "/nodes", "Le flow doit posséder exactement une entrée.");
        var outgoing = ids.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        var indegree = ids.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        var edgeIds = new HashSet<string>(StringComparer.Ordinal);
        var outputs = new HashSet<(string, string)>();
        for (var i = 0; i < flow.Connections.Count; i++)
        {
            var edge = flow.Connections[i];
            var path = $"/connections/{i}";
            if (edge is null) { Issue("connection.required", path, "Connexion obligatoire."); continue; }
            if (string.IsNullOrWhiteSpace(edge.Id) || !edgeIds.Add(edge.Id)) Issue("connection.id.invalid", path + "/id", "Identifiant obligatoire et unique.");
            if (edge.SourceNodeId is null || edge.TargetNodeId is null || !ids.Contains(edge.SourceNodeId) || !ids.Contains(edge.TargetNodeId))
            { Issue("connection.node.unknown", path, "La connexion référence un bloc inconnu."); continue; }
            if (edge.SourceNodeId == edge.TargetNodeId) Issue("connection.self", path, "Un bloc ne peut pas se relier à lui-même.");
            if (nodes.TryGetValue(edge.SourceNodeId, out var source)
                && !source.Outputs.Any(o => o.Id == edge.SourceOutputId)) Issue("connection.output.unknown", path + "/sourceOutputId", "Sortie non déclarée.");
            if (nodes.TryGetValue(edge.TargetNodeId, out var target) && target.Kind == "input") Issue("connection.entry", path + "/targetNodeId", "L’entrée du flow ne reçoit pas de liaison.");
            if (!outputs.Add((edge.SourceNodeId, edge.SourceOutputId))) Issue("connection.output.duplicate", path, "Une sortie ne peut être reliée qu’une fois.");
            outgoing[edge.SourceNodeId].Add(edge.TargetNodeId);
            indegree[edge.TargetNodeId]++;
        }
        var queue = new Queue<string>(indegree.Where(p => p.Value == 0).Select(p => p.Key));
        var count = 0;
        while (queue.TryDequeue(out var id))
        {
            count++;
            foreach (var target in outgoing[id]) if (--indegree[target] == 0) queue.Enqueue(target);
        }
        if (count != ids.Count) Issue("graph.cycle", "/connections", "Les cycles libres sont interdits.");
        if (entries.Length == 1)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>(); pending.Push(entries[0]);
            while (pending.TryPop(out var id)) if (seen.Add(id)) foreach (var target in outgoing[id]) pending.Push(target);
            foreach (var id in ids) if (!seen.Contains(id)) Issue("node.unreachable", "/nodes", $"Bloc non relié à l’entrée : {id}.");
        }
        if (entries.Length == 1 && count == ids.Count) issues.AddRange(FfmpegBuilderValidation.Validate(flow, entries[0]));
        foreach (var consumer in flow.Nodes.Where(node => node is not null && node.Configuration is not null))
        {
            if (!consumer.Configuration.TryGetValue("sourceFile", out var file) || file.ValueKind != JsonValueKind.String
                || !FlowArtifactReference.TryParse(file.GetString(), out var reference)) continue;
            if (!nodes.TryGetValue(reference.NodeId, out var producer) || !producer.ProducesFiles || reference.NodeId == consumer.Id)
            { Issue("artifact.reference.invalid", "/nodes/" + consumer.Id + "/configuration/sourceFile", "Choisissez un bloc producteur de fichiers distinct de ce bloc."); continue; }
            var reachable = new HashSet<string>(StringComparer.Ordinal); var pending = new Queue<string>(); pending.Enqueue(reference.NodeId);
            while (pending.TryDequeue(out var current))
                if (reachable.Add(current) && outgoing.TryGetValue(current, out var targets)) foreach (var target in targets) pending.Enqueue(target);
            if (!reachable.Contains(consumer.Id)) Issue("artifact.reference.invalid", "/nodes/" + consumer.Id + "/configuration/sourceFile",
                "Le producteur doit précéder le consommateur sur un chemin du flow.");
        }
        var packInstanceIds = new HashSet<string>(StringComparer.Ordinal);
        var packMembers = new Dictionary<string, FlowComponentPackInstance>(StringComparer.Ordinal);
        for (var packIndex = 0; packIndex < (flow.ComponentPacks?.Count ?? 0); packIndex++)
        {
            var pack = flow.ComponentPacks![packIndex];
            var path = $"/componentPacks/{packIndex}";
            if (pack is null)
            {
                Issue("pack.required", path, "La définition du pack est obligatoire.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(pack.Id) || !packInstanceIds.Add(pack.Id))
                Issue("pack.id.invalid", path + "/id", "L’identifiant du pack doit être unique.");
            if (pack.PackId == Guid.Empty || pack.PackVersion < 1 || string.IsNullOrWhiteSpace(pack.Name) || pack.Name.Length > 128)
                Issue("pack.reference.invalid", path, "La référence et la version du pack sont invalides.");
            var nodeList = pack.NodeIds ?? [];
            var memberIds = nodeList.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
            if (memberIds.Count == 0 || memberIds.Count != nodeList.Count || !memberIds.Contains(pack.InputNodeId))
                Issue("pack.members.invalid", path + "/nodeIds", "Le pack doit contenir des blocs distincts et son entrée.");
            foreach (var memberId in memberIds)
            {
                if (!ids.Contains(memberId))
                    Issue("pack.member.unknown", path + "/nodeIds", "Le pack référence un bloc absent du flow.");
                if (!packMembers.TryAdd(memberId, pack))
                    Issue("pack.member.duplicate", path + "/nodeIds", "Un bloc ne peut appartenir qu’à un pack.");
            }
            var packOutputs = pack.Outputs ?? [];
            if (pack.Outputs is null || packOutputs.Select(output => output?.Id).Distinct(StringComparer.Ordinal).Count() != packOutputs.Count)
                Issue("pack.outputs.invalid", path + "/outputs", "Les sorties exposées du pack doivent avoir des identifiants uniques.");
            foreach (var port in packOutputs)
            {
                if (port is null || !memberIds.Contains(port.SourceNodeId)
                    || !nodes.TryGetValue(port.SourceNodeId, out var source)
                    || !source.Outputs.Any(output => output.Id == port.SourceOutputId))
                    Issue("pack.output.invalid", path + "/outputs", "Une sortie du pack doit correspondre à une sortie de composant interne.");
            }
            foreach (var edge in flow.Connections)
            {
                var sourceInside = memberIds.Contains(edge.SourceNodeId);
                var targetInside = memberIds.Contains(edge.TargetNodeId);
                if (!sourceInside && targetInside && edge.TargetNodeId != pack.InputNodeId)
                    Issue("pack.input.boundary.invalid", path + "/inputNodeId", "Les liaisons entrantes doivent passer par l’entrée du pack.");
                if (sourceInside && !targetInside && !packOutputs.Any(port =>
                    port is not null && port.SourceNodeId == edge.SourceNodeId && port.SourceOutputId == edge.SourceOutputId))
                    Issue("pack.output.boundary.invalid", path + "/outputs", "Toute liaison sortant du pack doit utiliser une sortie exposée.");
            }
        }
        if(issues.Count==0)
        {
            var segmentEntries=entries.Concat(flow.Nodes.Where(RuntimeHandoffPolicy.IsHandoff)
                .SelectMany(node=>flow.Connections.Where(edge=>edge.SourceNodeId==node.Id).Select(edge=>edge.TargetNodeId)));
            foreach(var entry in segmentEntries.Distinct())
            {
                var targets=RuntimeHandoffPolicy.Segment(flow,entry)
                    .Where(node=>node.Component.PluginId=="nodes" && node.Component.ComponentId=="nodes-run-on-node")
                    .Select(node=>string.Join('|', new[] { "targetNodeId", "targetGroupId", "requiredLabels" }
                        .Where(key=>node.Configuration.ContainsKey(key)).Select(key=>key+"="+node.Configuration[key].GetRawText()))).Distinct().ToArray();
                if(targets.Length>1) Issue("node.constraint.conflict","/nodes","Les contraintes d'un segment entre deux relais doivent cibler le même worker.");
            }
        }
        foreach(var node in flow.Nodes.Where(node=>node?.Component is not null && RuntimeHandoffPolicy.IsHandoff(node)))
        {
            if(!flow.Connections.Any(edge=>edge.SourceNodeId==node.Id && edge.SourceOutputId=="success"))
                Issue("handoff.next.required","/nodes","Le relais doit être relié à la suite du flow.");
            if(node.Configuration?.TryGetValue("targetNodeId",out var target)==true && target.ValueKind==JsonValueKind.String
                && !string.IsNullOrWhiteSpace(target.GetString()) && (!Guid.TryParse(target.GetString(),out var targetId) || targetId==Guid.Empty))
                Issue("field.invalid","/nodes/"+node.Id+"/configuration/targetNodeId","Utilisez un identifiant de worker valide.");
            if(node.Configuration is not null && node.Configuration.TryGetValue("mode",out var relayMode)
                && relayMode.ValueKind==JsonValueKind.String && relayMode.GetString()=="selector")
            {
                if(node.Configuration.TryGetValue("targetGroupId",out var targetGroup)
                    && (targetGroup.ValueKind!=JsonValueKind.String || !string.IsNullOrWhiteSpace(targetGroup.GetString())
                        && (!Guid.TryParse(targetGroup.GetString(),out var targetGroupId) || targetGroupId==Guid.Empty)))
                    Issue("field.invalid","/nodes/"+node.Id+"/configuration/targetGroupId","Utilisez un identifiant de groupe valide.");
                try { _=RuntimeHandoffPolicy.Resolve(node,null); }
                catch(ArgumentException exception) { Issue("field.invalid","/nodes/"+node.Id+"/configuration",exception.Message); }
            }
        }
        var retryNodes=flow.Nodes.Where(node=>node?.Component is not null && node.Component.PluginId=="nodes"
            && node.Component.ComponentId==RuntimeRetryPolicy.ComponentId).ToArray();
        if(retryNodes.Length>1)
            Issue("retry.policy.duplicate","/nodes","Un flow ne peut contenir qu’une seule règle de relance après perte du worker.");
        foreach(var node in retryNodes)
        {
            if(!flow.Connections.Any(edge=>edge.SourceNodeId==node.Id && edge.SourceOutputId=="success"))
                Issue("retry.policy.next.required","/nodes","La règle de relance doit être reliée à la suite du flow.");
            try { _=RuntimeRetryPolicy.Resolve(flow); }
            catch(ArgumentException exception) { Issue("retry.policy.invalid","/nodes/"+node.Id+"/configuration",exception.Message); }
        }
        foreach(var node in flow.Nodes.Where(node=>node?.Component is not null && node.Configuration is not null && RuntimeWaitPolicy.IsWait(node)))
        {
            RuntimeWait wait;
            try { wait=RuntimeWaitPolicy.Resolve(node); }
            catch(ArgumentException exception) { Issue("wait.invalid","/nodes/"+node.Id+"/configuration",exception.Message); continue; }
            if(!flow.Connections.Any(edge=>edge.SourceNodeId==node.Id && edge.SourceOutputId=="resumed"))
                Issue("wait.next.required","/nodes",$"Le bloc Attendre « {node.Name??node.Id} » doit être relié à la suite du flow (sortie Reprise).");
            if(wait.Mode!="block") continue;
            var watched=flow.Nodes.SingleOrDefault(item=>item?.Id==wait.BlockNodeId);
            var descriptor=watched?.Component is null?null:registry.Resolve(watched.Component);
            if(watched is null || watched.Id==node.Id || descriptor is null || descriptor.Kind!="condition" || watched.Component.PluginId is "base" or "conditions" or "nodes")
                Issue("wait.block.invalid","/nodes/"+node.Id+"/configuration/blockNodeId","Désignez une condition de plugin (Plex, Jellyfin…) de ce flow.");
            else if(wait.MetOutputs!.Split(',').Any(output=>descriptor.Outputs.All(declared=>declared.Id!=output)))
                Issue("wait.block.invalid","/nodes/"+node.Id+"/configuration/blockOutputs","Une sortie indiquée n’existe pas sur le bloc surveillé.");
        }
        return new(issues);
    }

    private static string Pointer(string key) => key.Replace("~", "~0").Replace("/", "~1");

    private static string? ValidateField(FlowConfigurationField field, JsonElement value, string mediaType,bool allowIncompleteConditions)
    {
        var empty = value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            || value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString());
        if (empty) return field.Required ? "Ce paramètre est obligatoire." : null;
        switch (field.Type)
        {
            case "record-list":
                if (value.ValueKind != JsonValueKind.Array) return "Une liste de règles est attendue.";
                if (value.GetArrayLength() < (field.MinItems ?? 0) || value.GetArrayLength() > (field.MaxItems ?? 64)) return "Nombre de lignes hors limites.";
                foreach (var row in value.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object) return "Chaque ligne doit être un objet.";
                    if (row.EnumerateObject().Any(property => !(field.ItemFields ?? []).Any(child => child.Id == property.Name))) return "Paramètre inconnu dans la ligne.";
                    foreach (var child in field.ItemFields ?? [])
                    {
                        var childValue = row.TryGetProperty(child.Id, out var stored) ? stored : child.DefaultValue ?? default;
                        if (ValidateField(child, childValue, mediaType, allowIncompleteConditions) is { } error) return child.Label + " : " + error;
                    }
                }
                return null;
            case "condition-tree": return ConditionTree.Validate(value,mediaType,allowIncompleteConditions);
            case "text":
                if(field.Id=="sourceFile" && value.ValueKind==JsonValueKind.String) {
                    if (value.GetString()!.StartsWith(FlowArtifactReference.Prefix, StringComparison.Ordinal))
                        return FlowArtifactReference.TryParse(value.GetString(), out _) ? null : "Référence de fichier produit invalide.";
                    try { Encoding.Contracts.Libraries.MediaPathRules.Normalize(value.GetString()); }
                    catch(ArgumentException e) { return e.Message; }
                }
                return value.ValueKind == JsonValueKind.String ? null : "Une valeur textuelle est attendue.";
            case "script-reference":
                if (value.ValueKind != JsonValueKind.String) return "Une référence de script est attendue.";
                var reference = value.GetString();
                var separator = reference?.LastIndexOf('@') ?? -1;
                return separator > 0 && Guid.TryParse(reference![..separator], out var scriptId) && scriptId != Guid.Empty
                    && int.TryParse(reference[(separator + 1)..], out var version) && version > 0
                    ? null
                    : "Choisissez une version publiée de la bibliothèque de scripts.";
            case "encoding-profile":
                if (value.ValueKind != JsonValueKind.Object) return "Choisissez un profil de qualité publié.";
                try
                {
                    var profile = value.Deserialize<EncodingProfileSnapshot>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    return VideoEncoderCatalog.ValidateSnapshot(profile);
                }
                catch (JsonException) { return "Le profil de qualité intégré au flow est invalide."; }
            case "video-encoder":
                return value.ValueKind == JsonValueKind.String && VideoEncoderCatalog.Find(value.GetString()) is not null
                    ? null : "Choisissez un encodeur pris en charge.";
            case "boolean": return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : "Une valeur Oui ou Non est attendue.";
            case "select": return value.ValueKind == JsonValueKind.String && (field.Options ?? []).Any(o => o.Value == value.GetString()
                && (o.SupportedFlowMediaTypes is null
                || o.SupportedFlowMediaTypes.Contains(mediaType))) ? null : "Choisissez une valeur proposée pour ce média.";
            case "number":
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number)) return "Un nombre valide est attendu.";
                if (field.Min is { } min && number < min) return $"Minimum : {min}.";
                if (field.Max is { } max && number > max) return $"Maximum : {max}.";
                if (field.Step is > 0)
                {
                    try
                    {
                        var increment = (number - (field.Min ?? 0)) / field.Step.Value;
                        if (Math.Abs(increment - Math.Round(increment)) > 0.00000001m) return $"Pas attendu : {field.Step}.";
                    }
                    catch (OverflowException) { return "Le nombre dépasse la plage prise en charge."; }
                }
                return null;
            default: return "Type de champ non pris en charge.";
        }
    }

    private static EncodingProfileSnapshot? TryGetEncodingProfile(IReadOnlyDictionary<string, JsonElement> configuration)
    {
        if (!configuration.TryGetValue("encodingProfile", out var value) || value.ValueKind != JsonValueKind.Object) return null;
        try { return value.Deserialize<EncodingProfileSnapshot>(new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { return null; }
    }
}
