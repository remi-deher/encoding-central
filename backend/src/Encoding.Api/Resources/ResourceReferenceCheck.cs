using System.Text.Json;
using Encoding.Contracts.Flows;

namespace Encoding.Api.Resources;

/// <summary>Contrôle à la publication d'un flow que chaque ressource référencée par un bloc existe, avec la version et le type demandés.</summary>
public static class ResourceReferenceCheck
{
    /// <summary>Messages d'erreur, un par bloc dont une ressource est mal écrite, inconnue, d'un autre type ou sans la version demandée.</summary>
    public static async Task<IReadOnlyList<string>> ProblemsAsync(FlowDefinition flow, ResourceRepository resources, CancellationToken ct)
    {
        var problems = new List<string>();
        foreach (var node in flow.Nodes)
        {
            foreach (var (field, kind, list) in Fields(node))
            {
                if (!node.Configuration.TryGetValue(field, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                    continue;
                var label = node.Name ?? node.Id;
                var text = value.GetString();
                if (!TryRead(text, list, out var references))
                {
                    problems.Add(list
                        ? $"Bloc « {label} » : référence de ressource invalide (attendu « identifiant:version », plusieurs séparées par des virgules)."
                        : $"Bloc « {label} » : référence de ressource invalide (attendu « identifiant:version »).");
                    continue;
                }
                foreach (var reference in references)
                {
                    if (await resources.GetVersionAsync(reference.Id, reference.Version, ct) is null)
                        problems.Add($"Bloc « {label} » : la ressource {reference.Id:D} n'a pas de version {reference.Version}.");
                    else if (await resources.GetAsync(reference.Id, ct) is { } details && details.Summary.Kind != kind)
                        problems.Add($"Bloc « {label} » : la ressource {reference.Id:D} est de type « {details.Summary.Kind} », « {kind} » est attendu.");
                }
            }
        }
        return problems;
    }

    private static bool TryRead(string? text, bool list, out IReadOnlyList<ResourceReference> references)
    {
        if (list) return ResourceReference.TryParseList(text, out references);
        if (!ResourceReference.TryParse(text, out var single))
        {
            references = [];
            return false;
        }
        references = [single];
        return true;
    }

    private static IEnumerable<(string Field, string Kind, bool List)> Fields(FlowNodeDefinition node)
    {
        var id = node.Component.ComponentId;
        if (id is "subtitles-add-fonts" or "base-add-fonts"
            || id == "base-assemble-media" && Flag(node, "addFonts"))
            yield return ("resource", "font", true);
        if (id is "subtitles-convert-ass" or "subtitles-style-ass"
            || id == "base-assemble-media" && Flag(node, "manageSubtitles"))
            yield return ("profile", "subtitle-profile", false);
        if (id is "subtitles-add-track" or "base-add-subtitle-track")
            yield return ("resource", "subtitle", false);
    }

    private static bool Flag(FlowNodeDefinition node, string key) =>
        node.Configuration.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.True;
}
