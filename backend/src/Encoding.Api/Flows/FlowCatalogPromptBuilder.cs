using System.Text;
using System.Text.Json;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Plugins;
using Encoding.Api.Plugins;

namespace Encoding.Api.Flows;

public sealed class FlowCatalogPromptBuilder(
    FlowComponentRegistry registry,
    ClusterExtensionActivationState? activation = null)
{
    public string BuildSystemPrompt(string mediaType)
    {
        var prompt = BuildSystemPrompt(mediaType, includeDescriptions: true);
        if (prompt.Length <= 100_000) return prompt;
        prompt = BuildSystemPrompt(mediaType, includeDescriptions: false);
        if (prompt.Length <= 100_000) return prompt;
        throw new InvalidOperationException("Le catalogue des composants dépasse la taille de contexte sûre, même après réduction des descriptions facultatives.");
    }

    private string BuildSystemPrompt(string mediaType, bool includeDescriptions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Tu es l'architecte expert de flows multimédia pour le moteur distribué Encoding.");
        sb.AppendLine("Tu composes des flux complets, cohérents et fiables.");
        sb.AppendLine("FORMAT OBLIGATOIRE : Réponds UNIQUEMENT avec un objet JSON valide représentant le FlowDefinition (aucun texte avant ni après, pas de balises superflues).");
        sb.AppendLine();
        sb.AppendLine("RÈGLES D'OR DU MOTEUR DE FLOWS :");
        sb.AppendLine("1. Exactement UNE entrée obligatoire (ex: base-input-video ou base-input-file) sans AUCUNE connexion entrante.");
        sb.AppendLine("2. Le graphe est un DAG acyclique strict (aucun cycle). Tous les blocs doivent être reliés depuis l'entrée.");
        sb.AppendLine("3. Chaque sortie ne peut être reliée qu'une seule fois. Plusieurs sorties peuvent converger vers une même entrée.");
        sb.AppendLine("4. Respecter scrupuleusement les sorties (sourceOutputId) et les identifiants de composants du catalogue.");
        sb.AppendLine("5. Respecter les champs obligatoires, leurs valeurs autorisées, bornes et valeurs par défaut.");
        sb.AppendLine("6. Les capacités d'exécution indiquées au catalogue sont déclaratives et ne prouvent pas la disponibilité réelle d'un outil sur un worker.");
        sb.AppendLine("7. Conserver l'identité et les identifiants du flow lors d'une modification ; réutiliser ceux des blocs inchangés.");
        if (!includeDescriptions) sb.AppendLine("Le catalogue a été réduit de façon déterministe : les descriptions facultatives sont omises, les identifiants et contraintes sont conservés.");
        sb.AppendLine();
        sb.AppendLine("CATALOGUE DES COMPOSANTS DISPONIBLES :");

        foreach (var plugin in registry.Plugins.Where(plugin => activation?.IsPluginEnabled(plugin.Id) ?? true))
        {
            var matching = plugin.Components
                .Where(c => c.SupportedFlowMediaTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase)
                         || c.SupportedFlowMediaTypes.Contains("general", StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (matching.Count == 0) continue;

            sb.AppendLine($"Plugin: {plugin.Id} (v{plugin.Version})");
            foreach (var c in matching)
            {
                sb.AppendLine($"  - id: \"{c.Id}\" (v{c.Version}, schéma {c.ConfigurationSchema.Version}) [{c.Kind}] : {c.Name}");
                if (includeDescriptions && !string.IsNullOrWhiteSpace(c.Description)) sb.AppendLine($"    Description: {c.Description}");
                sb.AppendLine("    Sorties :");
                foreach (var output in c.Outputs)
                    sb.AppendLine($"      - {output.Id} ({output.Label}){(!includeDescriptions || string.IsNullOrWhiteSpace(output.Description) ? string.Empty : $": {output.Description}")}");
                sb.AppendLine("    Configuration :");
                foreach (var field in c.ConfigurationSchema.Fields)
                {
                    var rules = new List<string> { field.Type, field.Required ? "obligatoire" : "facultatif" };
                    if (field.LegacyOnly) rules.Add("historique : conserver uniquement dans un flow existant, ne pas ajouter");
                    if (field.DefaultValue is { } defaultValue) rules.Add($"défaut={defaultValue.GetRawText()}");
                    if (field.Min is { } min) rules.Add($"min={min}");
                    if (field.Max is { } max) rules.Add($"max={max}");
                    if (field.Step is { } step) rules.Add($"pas={step}");
                    if (field.Suggestions is { Count: > 0 }) rules.Add($"suggestions=[{string.Join(", ", field.Suggestions)}]");
                    var options = field.Options?.Where(option => option.SupportedFlowMediaTypes is null
                            || option.SupportedFlowMediaTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase)
                            || option.SupportedFlowMediaTypes.Contains("general", StringComparer.OrdinalIgnoreCase))
                        .Select(option => $"{option.Value} ({option.Label})").ToArray() ?? Array.Empty<string>();
                    if (options.Length > 0) rules.Add($"choix=[{string.Join(", ", options)}]");
                    if (field.VisibleWhenFieldId is { Length: > 0 } visibleField)
                        rules.Add($"visible si {visibleField} {(field.VisibleWhenEmpty ? "est vide ou " : string.Empty)}vaut {field.VisibleWhenValue}");
                    var description = !includeDescriptions || string.IsNullOrWhiteSpace(field.Description) ? string.Empty : $" — {field.Description}";
                    sb.AppendLine($"      - {field.Id} ({field.Label}) : {string.Join(", ", rules)}{description}");
                    if (field.ItemFields is { } itemFields)
                    {
                        sb.AppendLine($"        liste : {field.MinItems ?? 0} à {field.MaxItems ?? 64} lignes, première règle correspondante selon le composant");
                        foreach (var child in itemFields)
                            sb.AppendLine($"        - {child.Id} ({child.Type}) : {child.Label}; défaut={child.DefaultValue?.GetRawText() ?? "null"}; min={child.Min}; max={child.Max}; choix=[{string.Join(", ", child.Options?.Select(option => option.Value) ?? [])}]" + (includeDescriptions ? " — " + child.Description : ""));
                    }
                    if (field.ConditionFields is { Count: > 0 })
                        foreach (var condition in field.ConditionFields.Where(condition => condition.SupportedFlowMediaTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase)
                                     || condition.SupportedFlowMediaTypes.Contains("general", StringComparer.OrdinalIgnoreCase)))
                            sb.AppendLine($"        condition {condition.Id} ({condition.Label}, {condition.Type}) ; opérateurs=[{string.Join(", ", condition.Operators.Select(op => op.Value))}]");
                    if (field.ConditionPresets is { Count: > 0 })
                        foreach (var preset in field.ConditionPresets.Where(preset => preset.SupportedFlowMediaTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase)
                                     || preset.SupportedFlowMediaTypes.Contains("general", StringComparer.OrdinalIgnoreCase)))
                            sb.AppendLine($"        préréglage {preset.Id} ({preset.Label}) : {JsonSerializer.Serialize(preset.Expression)}");
                }
                if (c.RequiredTools.Count > 0)
                    sb.AppendLine($"    Outils requis (déclaratifs) : {string.Join(", ", c.RequiredTools.Select(tool => $"{tool.ToolId}{(tool.MinimumVersion is null ? string.Empty : $">={tool.MinimumVersion}")}"))}");
                if (c.RequiresMedia || c.RequiresWriteAccess)
                    sb.AppendLine($"    Accès déclarés : média={c.RequiresMedia}, écriture={c.RequiresWriteAccess}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("STRUCTURE DU DOCUMENT JSON ATTENDU :");
        sb.AppendLine("""
        {
          "formatVersion": 1,
          "id": "11111111-1111-1111-1111-111111111111",
          "version": 1,
          "name": "Nom du Flow",
          "mediaType": "video",
          "nodes": [
            {
              "id": "node_1",
              "name": "Entrée",
              "component": {
                "pluginId": "base",
                "pluginVersion": "1.0.0",
                "componentId": "base-input-video",
                "componentVersion": "1.0.0",
                "configurationSchemaVersion": 1
              },
              "configuration": {}
            }
          ],
          "connections": [
            {
              "id": "conn_1",
              "sourceNodeId": "node_1",
              "sourceOutputId": "output",
              "targetNodeId": "node_2"
            }
          ]
        }
        """);

        return sb.ToString();
    }
}
