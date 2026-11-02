using System.Text.Json;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Flows.Conditions;

namespace Encoding.Api.Flows;

/// <summary>Stateless graph exploration. No runtime, repository, process or plugin invocation is accessible here.</summary>
public sealed class FlowSimulator(FlowComponentRegistry registry, FlowDefinitionValidator validator)
{
    public FlowSimulationResult Simulate(FlowSimulationRequest request, IReadOnlyDictionary<string, JsonElement>? nodeDefaults = null)
    {
        var (flow, validation) = validator.ValidateDocument(request.Definition);
        if (flow is null || !validation.IsValid) return new("invalid", "Corrigez la définition du flow avant de le tester.", [], Issues: validation.Issues);
        if (request.File is not { } file || string.IsNullOrWhiteSpace(file.Name) || file.Name.Length > 4096
            || file.Metadata.ValueKind != JsonValueKind.Object || file.Variables is null
            || file.SourceNodeId == Guid.Empty || file.WorkerNodeId == Guid.Empty)
            throw new ArgumentException("Le profil nécessite un nom, des métadonnées, des variables et des identifiants de nœuds valides.");
        if (!ValidMetadata(file.Metadata))
            throw new ArgumentException("Les métadonnées doivent contenir format et streams avec un codec_type par piste.");
        var nodes = flow.Nodes.ToDictionary(node => node.Id);
        foreach (var pair in request.Assumptions ?? new Dictionary<string, FlowSimulationAssumption>())
        {
            if (pair.Value is null || !nodes.TryGetValue(pair.Key, out var node)
                || !registry.Resolve(node.Component)!.Outputs.Any(output => output.Id == pair.Value.OutputId)
                || pair.Value.Metadata is { } metadata && !ValidMetadata(metadata)
                || pair.Value.WorkerNodeId == Guid.Empty || pair.Value.Extension?.Length > 32)
                throw new ArgumentException("Un résultat simulé référence un bloc, une sortie ou un profil invalide.");
        }
        var variables = new Dictionary<string, JsonElement>(file.Variables);
        var context = new RuntimeContext("source", Path.GetExtension(file.Name), variables, file.Metadata, file.Metadata, []);
        var worker = file.WorkerNodeId;
        string? next = flow.Nodes.Single(node => registry.Resolve(node.Component)!.Kind == "input").Id;
        string? terminal = null, lastOutput = null;
        var steps = new List<FlowSimulationStep>();
        while (next is not null)
        {
            var node = nodes[next]; var descriptor = registry.Resolve(node.Component)!;
            var id = node.Component.ComponentId; var c = node.Configuration;
            string Text(string key, string fallback = "") => c.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : fallback;
            bool Flag(string key, bool fallback = false) => c.TryGetValue(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;
            string? output = "success";
            var detail = "Étape déterministe évaluée."; var assumed = false;
            var observations = new List<ConditionObservation>();
            try
            {
                if (node.Component.PluginId == "conditions" && id == "conditions-match")
                {
                    var tree = ConditionTree.Parse(c["rules"], flow.MediaType);
                    output = ConditionTree.Evaluate(tree, new(context with { Variables = variables }, file.SourceNodeId, worker, file.LibraryId, file.Name, "source"), observations.Add) ? "yes" : "no";
                    detail = output == "yes" ? "Conditions satisfaites avec les données courantes." : "Conditions non satisfaites avec les données courantes.";
                }
                else if (node.Component.PluginId == "conditions" && MediaConditionEvaluator.IsMediaCondition(id))
                {
                    if (context.CurrentMetadata is null)
                    {
                        if (request.Assumptions?.GetValueOrDefault(node.Id) is not { } result)
                            return new("waiting", "Choisissez explicitement le résultat de cette condition (Oui / Non).", steps, node.Id);
                        assumed = true; output = result.OutputId;
                        detail = "Résultat supposé pour la simulation.";
                    }
                    else
                    {
                        output = MediaConditionEvaluator.Evaluate(id, c, context.CurrentMetadata);
                        detail = output == "yes" ? "Condition satisfaite d’après les métadonnées." : "Condition non satisfaite d’après les métadonnées.";
                    }
                }
                else if (node.Component.PluginId == "base" && id.StartsWith("base-input-", StringComparison.Ordinal))
                    detail = "Profil de test utilisé ; aucun fichier de bibliothèque n’est ouvert.";
                else if (node.Component.PluginId == "base" && id == "base-variable")
                {
                    variables[Text("name")] = FlowVariableValue.Parse(Text("value"));
                    detail = $"Variable « {Text("name")} » définie.";
                }
                else if (node.Component.PluginId == "probe" && id is "probe-validate-output" or MediaIntegrityPolicy.Component)
                {
                    if (request.Assumptions?.GetValueOrDefault(node.Id) is not { } result)
                        return new("waiting", "Choisissez le résultat des contrôles de sortie (Valide / Invalide / Erreur).", steps, node.Id);
                    assumed = true; output = result.OutputId;
                    detail = "Résultat supposé pour la validation post-assemblage ; le fichier n'est pas ouvert par le simulateur.";
                    if (result.Variables is not null) foreach (var pair in result.Variables) variables[pair.Key] = pair.Value;
                }
                else if ((node.Component.PluginId is "base" or "probe") && (id.StartsWith("base-probe-", StringComparison.Ordinal) || id.StartsWith("probe-", StringComparison.Ordinal)))
                {
                    variables[Text("resultVariable", "result")] = FlowMediaValues.Probe(id, context.CurrentMetadata!.Value,
                        c.TryGetValue("trackId", out var track) ? track.GetInt32() : 0);
                    detail = "Métadonnées du profil courant copiées dans la variable de résultat.";
                }
                else if (node.Component.PluginId == "base" && id == FfmpegBuilderPolicy.Start)
                {
                    if(FfmpegBuilderPolicy.Active(variables) || context.Plan.Count>0)
                        throw new ArgumentException("Une préparation non exécutée ne peut pas être écrasée.");
                    variables[FfmpegBuilderPolicy.StateKey]=JsonSerializer.SerializeToElement(node.Id);
                    variables["plan.tool"]=JsonSerializer.SerializeToElement("ffmpeg");
                    variables["plan.preserveUnchangedTracks"]=JsonSerializer.SerializeToElement(Flag("preserveUnchangedTracks",true));
                    detail="Builder FFmpeg ouvert ; métadonnées courantes inchangées jusqu’à l’exécution.";
                }
                else if (node.Component.PluginId == "base" && id == "base-prepare-processing")
                {
                    context = context with { Plan = [] };
                    variables["plan.tool"] = JsonSerializer.SerializeToElement(Text("tool", "ffmpeg"));
                    variables["plan.preserveUnchangedTracks"] = JsonSerializer.SerializeToElement(Flag("preserveUnchangedTracks", true));
                    detail = "Plan initialisé ; fichier courant inchangé.";
                }
                else if (node.Component.PluginId == "base" && id is "base-complete-flow" or "base-defer-processing")
                {
                    output = null; terminal = id == "base-defer-processing" ? "deferred" : Text("result", "unchanged"); detail = $"Fin déclarée : {terminal}.";
                    if (id == "base-defer-processing") variables["runtime.deferMinutes"] = c.TryGetValue("delayMinutes", out var delay) ? delay : JsonSerializer.SerializeToElement(2);
                }
                else if (node.Component.PluginId == "base" && (id == "base-execute-processing" && !Flag("forceProcessing") || id == FfmpegBuilderPolicy.Execute) && context.Plan.Count == 0)
                { output = "skipped"; detail = "Aucun traitement planifié : étape ignorée."; if(id==FfmpegBuilderPolicy.Execute) FfmpegBuilderPolicy.Close(variables); }
                else if (node.Component.PluginId == "nodes" && id == RuntimeRetryPolicy.ComponentId)
                { output = "success"; detail = "Règle de relance bornée activée pour les pertes de worker."; }
                else if (node.Component.PluginId == "nodes" && id == "nodes-require-direct-access")
                { output = "success"; detail = "Contrainte d’accès direct à la bibliothèque prise en compte pour l’attribution du segment."; }
                else if (FfmpegBuilderPolicy.Active(variables) && FfmpegBuilderPolicy.IsSetting(id)
                    || (node.Component.PluginId is "base" or "video" or "audio" or "subtitles") && Text("processingMode", "direct") == "plan")
                {
                    if (variables.TryGetValue("plan.tool", out var selected) && (FfmpegBuilderPolicy.IsSetting(id) != (selected.GetString() == "ffmpeg")))
                    { output = "error"; detail = "Outil du plan incompatible avec cette action."; }
                    else { context = context with { Plan = [.. context.Plan, new(id, c)] }; detail = "Traitement ajouté au plan ; aucune transformation simulée à ce stade."; }
                }
                else
                {
                    if (request.Assumptions?.GetValueOrDefault(node.Id) is not { } result)
                        return new("waiting", "Choisissez explicitement le résultat de cette étape pour poursuivre. Sa capacité réelle d’exécution n’est pas vérifiée.", steps, node.Id);
                    if(id==FfmpegBuilderPolicy.Execute && result.OutputId=="success" && result.Metadata is null)
                        return new("waiting","Renseignez les métadonnées de la sortie FFmpeg pour évaluer les blocs suivants sans réutiliser celles de l’entrée.",steps,node.Id);
                    assumed = true; output = result.OutputId;
                    detail = "Résultat fourni par le scénario ; aucune action réelle exécutée.";
                    if (node.Component.PluginId == "nodes" && id == "nodes-run-on-node" && output == "success")
                    {
                        var exactNode = Guid.TryParse(Text("targetNodeId"), out var requiredWorker);
                        var hasOtherFilter = Guid.TryParse(Text("targetGroupId"), out _) || !string.IsNullOrWhiteSpace(Text("requiredLabels"));
                        if (exactNode && requiredWorker != worker || !exactNode && !hasOtherFilter)
                            throw new ArgumentException("Le worker courant du profil ne correspond pas à la contrainte de ce bloc. Modifiez le profil ou le résultat du relais précédent.");
                        if (hasOtherFilter) detail += " L’appartenance au groupe et les étiquettes ne sont pas vérifiées par la simulation.";
                    }
                    if(id!=FfmpegBuilderPolicy.Execute || output=="success")
                    {
                        if (result.Variables is not null) foreach (var pair in result.Variables) variables[pair.Key] = pair.Value;
                        if (result.Metadata is { } metadata) context = context with { CurrentMetadata = metadata };
                        if (result.Modified) context = context with { CurrentSha256 = "simulated-" + node.Id };
                        if (!string.IsNullOrWhiteSpace(result.Extension)) context = context with { Extension = "." + result.Extension.TrimStart('.') };
                    }
                    if (RuntimeHandoffPolicy.IsHandoff(node) && output == "success")
                    {
                        if (result.WorkerNodeId is not { } target || target == worker)
                            throw new ArgumentException("Indiquez un autre worker pour simuler le relais. Son éligibilité n’est pas attestée par le test.");
                        var selector = RuntimeHandoffPolicy.Resolve(node, nodeDefaults);
                        if (!selector.Accepts(target, worker) && selector.Mode != "selector")
                            throw new ArgumentException("Le worker simulé doit correspondre au worker choisi dans le bloc.");
                        if (selector.Mode == "selector") detail += " Le scénario suppose que le worker satisfait les filtres de groupe et d’étiquettes.";
                        worker = target;
                        detail += context.CurrentSha256 == "source" ? " Relais des données et délégation de la source." : " Relais avec transfert du fichier transformé.";
                    }
                    if (id == "base-execute-processing" && output == "success" || id == FfmpegBuilderPolicy.Execute)
                    { context = context with { Plan = [] }; if(id==FfmpegBuilderPolicy.Execute) FfmpegBuilderPolicy.Close(variables); }
                }
            }
            catch (ConditionEvaluationException exception) { output = "error"; detail = exception.Message; }
            context = context with { Variables = new Dictionary<string, JsonElement>(variables) };
            steps.Add(new(node.Id, node.Name ?? descriptor.Name, output, detail, assumed, context, worker, observations));
            next = FlowPath.NextNodeId(flow, node.Id, output);
            lastOutput = output;
        }
        var failed = ExecutionOutcomes.IsFailure(terminal, lastOutput);
        if (context.Plan.Count > 0 && !failed) return new(ExecutionStates.Failed, "Le flow se termine avec un plan non exécuté.", steps, TerminalResult: terminal);
        return new(failed ? ExecutionStates.Failed : "completed",
            failed ? "Le chemin simulé se termine en échec." : "Chemin simulé terminé. Les résultats supposés restent à vérifier sur un worker réel.", steps,
            TerminalResult: terminal);
    }
    private static bool ValidMetadata(JsonElement metadata) => metadata.ValueKind == JsonValueKind.Object
        && metadata.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array && streams.GetArrayLength() <= 1000
        && streams.EnumerateArray().All(stream => stream.ValueKind == JsonValueKind.Object
            && stream.TryGetProperty("codec_type", out var type) && type.ValueKind == JsonValueKind.String)
        && metadata.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.Object;
}
