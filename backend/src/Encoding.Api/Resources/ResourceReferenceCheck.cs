using Encoding.Contracts.Flows;

namespace Encoding.Api.Resources;

/// <summary>Contrôle à la publication d'un flow que chaque ressource référencée par un bloc existe, avec la version demandée.</summary>
public static class ResourceReferenceCheck
{
    /// <summary>Messages d'erreur, un par bloc dont la ressource est mal écrite, inconnue ou sans cette version.</summary>
    public static async Task<IReadOnlyList<string>> ProblemsAsync(FlowDefinition flow, ResourceRepository resources, CancellationToken ct)
    {
        var problems = new List<string>();
        foreach (var node in flow.Nodes)
        {
            if (node.Component.ComponentId is not "subtitles-add-fonts"
                || !node.Configuration.TryGetValue("resource", out var value)
                || value.ValueKind != System.Text.Json.JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) continue;
            var label = node.Name ?? node.Id;
            if (!ResourceReference.TryParse(value.GetString(), out var reference))
                problems.Add($"Bloc « {label} » : référence de ressource invalide (attendu « identifiant:version »).");
            else if (await resources.GetVersionAsync(reference.Id, reference.Version, ct) is null)
                problems.Add($"Bloc « {label} » : la ressource {reference.Id:D} n'a pas de version {reference.Version}.");
        }
        return problems;
    }
}
