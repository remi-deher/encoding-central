using System.Text.Json;
using Encoding.Api.Mcp;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class BusinessOperationSchemaTests
{
    [Fact]
    public void NestedItemsAndNumericBoundsAreValidated()
    {
        var libraryId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        using var valid = JsonDocument.Parse($"{{\"libraryId\":\"{libraryId}\",\"watchEnabled\":true,\"scanIntervalSeconds\":60,\"nodeLimits\":[{{\"nodeId\":\"{nodeId}\",\"maxConcurrentTasks\":4}}],\"expectedRevision\":0}}");
        using var invalidLimit = JsonDocument.Parse($"{{\"libraryId\":\"{libraryId}\",\"watchEnabled\":true,\"scanIntervalSeconds\":60,\"nodeLimits\":[{{\"nodeId\":\"{nodeId}\",\"maxConcurrentTasks\":1001}}],\"expectedRevision\":0}}");
        using var invalidItem = JsonDocument.Parse($"{{\"libraryId\":\"{libraryId}\",\"watchEnabled\":true,\"scanIntervalSeconds\":60,\"nodeLimits\":[{{\"maxConcurrentTasks\":4,\"unexpected\":true}}],\"expectedRevision\":0}}");

        Assert.True(BusinessOperationCatalog.ValidateArguments("save_library_processing", valid.RootElement));
        Assert.False(BusinessOperationCatalog.ValidateArguments("save_library_processing", invalidLimit.RootElement));
        Assert.False(BusinessOperationCatalog.ValidateArguments("save_library_processing", invalidItem.RootElement));
    }

    [Fact]
    public void FlowSchemasDescribeBoundedNestedArraysAndRejectUnknownTopLevelFields()
    {
        using var flow = JsonDocument.Parse("{\"nodes\":[{\"id\":\"input\",\"component\":\"base-input-file\",\"configuration\":{}}],\"connections\":[]}");
        using var extra = JsonDocument.Parse("{\"nodes\":[],\"connections\":[],\"typo\":true}");
        var schema = JsonSerializer.SerializeToElement(BusinessOperationCatalog.FindTool("create_flow")!.InputSchema);

        Assert.True(BusinessOperationCatalog.ValidateArguments("create_flow", flow.RootElement));
        Assert.False(BusinessOperationCatalog.ValidateArguments("create_flow", extra.RootElement));
        Assert.Equal(1000, schema.GetProperty("properties").GetProperty("nodes").GetProperty("maxItems").GetInt32());
        Assert.True(schema.GetProperty("properties").GetProperty("nodes").TryGetProperty("items", out _));
    }

    [Fact]
    public void MappedAssistantActionsShareDomainWithTheirMcpTools()
    {
        foreach (var (assistant, mcp) in BusinessOperationCatalog.AssistantToMcpToolNames)
        {
            Assert.True(BusinessOperationCatalog.IsAssistantAction(assistant));
            if (mcp is null) continue;
            var tool = BusinessOperationCatalog.FindTool(mcp);
            Assert.NotNull(tool);
            var action = BusinessOperationCatalog.Describe().Single(item => item.Name == assistant);
            Assert.Equal(tool.Domain, action.Domain);
            Assert.True(BusinessOperationCatalog.TryResolveMcpToolName(assistant, out var resolved));
            Assert.Equal(mcp, resolved);
        }
    }
}
