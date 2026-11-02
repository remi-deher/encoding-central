using System.Text.Json;
using Encoding.Api.Flows;
using Encoding.Contracts.Flows;

namespace Encoding.Runtime.Tests;

internal static class FlowValidationScenarios
{
    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static void Run()
    {
        var registry = StoreCatalogFixture.Create();
        var validator = new FlowDefinitionValidator(registry);
        FlowNodeDefinition Node(string id, string componentId, IReadOnlyDictionary<string, JsonElement>? changes = null)
        {
            var plugin = registry.Plugins.Single(item => item.Components.Any(component => component.Id == componentId));
            var descriptor = plugin.Components.Single(component => component.Id == componentId);
            var configuration = descriptor.ConfigurationSchema.Fields
                .Where(field => field.DefaultValue is not null)
                .ToDictionary(field => field.Id, field => field.DefaultValue!.Value);
            if (changes is not null) foreach (var pair in changes) configuration[pair.Key] = pair.Value;
            return new(id, null, new(plugin.Id, plugin.Version, componentId, descriptor.Version, descriptor.ConfigurationSchema.Version), configuration);
        }

        var input = Node("input", "base-input-file");
        var end = Node("end", "base-complete-flow");
        var basic = new FlowDefinition(1, Guid.NewGuid(), 1, "Validation", "video", [input, end],
            [new("to-end", "input", "success", "end")]);
        FlowValidationResult Validate(FlowDefinition flow) => validator.Validate(flow);

        var duplicateId = Validate(basic with { Nodes = [input, input, end] });
        Assert(duplicateId, "node.id.duplicate", "Duplicate node ids are rejected.");

        var invalidOutput = Validate(basic with { Connections = [new("to-end", "input", "missing", "end")] });
        Assert(invalidOutput, "connection.output.unknown", "Connections cannot name undeclared component outputs.");

        var orphaned = Validate(basic with { Nodes = [.. basic.Nodes, Node("orphan", "base-variable")] });
        Assert(orphaned, "node.unreachable", "Every flow block must be reachable from its entry.");

        var wrongMedia = basic with
        {
            MediaType = "music",
            Nodes = [input, Node("plex", "plex-refresh-library"), end],
            Connections = [new("to-plex", "input", "success", "plex"), new("to-end", "plex", "success", "end")]
        };
        Assert(Validate(wrongMedia), "component.incompatible", "A video-only plugin component cannot enter a music flow.");

        var condition = Node("condition", "conditions-match");
        var cyclic = new FlowDefinition(1, Guid.NewGuid(), 1, "Cycle", "video", [input, condition, end],
            [new("to-condition", "input", "success", "condition"), new("to-end", "condition", "yes", "end"), new("back", "condition", "no", "input")]);
        Assert(Validate(cyclic), "graph.cycle", "Free cycles are rejected instead of being run as flow loops.");
    }

    private static void Assert(FlowValidationResult result, string code, string description)
    {
        if (!result.Issues.Any(issue => issue.Code == code))
            throw new InvalidOperationException(description + " Expected validation code: " + code + ".");
    }
}
