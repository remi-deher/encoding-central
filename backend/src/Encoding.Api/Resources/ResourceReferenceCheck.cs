using System.Text.Json;
using Encoding.Contracts.Flows;

namespace Encoding.Api.Resources;

/// <summary>Contrôle à la publication d'un flow que chaque ressource référencée par un bloc existe, avec la version demandée.</summary>
public static class ResourceReferenceCheck
{
    /// <summary>Messages d'erreur, un par bloc dont une ressource est mal écrite, inconnue ou sans la version demandée.</summary>
    public static async Task<IReadOnlyList<string>> ProblemsAsync(FlowDefinition flow, ResourceRepository resources, CancellationToken ct)
    {
        var problems = new List<string>();
        foreach (var node in flow.Nodes)
        {
            if (!ReadsResources(node)
                || !node.Configuration.TryGetValue("resource", out var value)
                || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) continue;
            var label = node.Name ?? node.Id;
            if (!ResourceReference.TryParseList(value.GetString(), out var references))
            {
                problems.Add($"Bloc « {label} » : référence de ressource invalide (attendu « identifiant:version », plusieurs séparées par des virgules).");
                continue;
            }
            foreach (var reference in references)
                if (await resources.GetVersionAsync(reference.Id, reference.Version, ct) is null)
                    problems.Add($"Bloc « {label} » : la ressource {reference.Id:D} n'a pas de version {reference.Version}.");
        }
        return problems;
    }

    private static bool ReadsResources(FlowNodeDefinition node) =>
        node.Component.ComponentId is "subtitles-add-fonts" or "base-add-fonts" or "base-assemble-media";
}
