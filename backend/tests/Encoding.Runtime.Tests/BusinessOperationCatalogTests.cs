using System.Text.Json;
using Encoding.Api.Mcp;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class BusinessOperationCatalogTests
{
    [Fact]
    public void ResumeExecutionIsAnAdminConfirmedMutation()
    {
        var operation = BusinessOperationCatalog.FindTool("resume_execution");

        Assert.NotNull(operation);
        Assert.Equal("executions-and-logs", operation.Domain);
        Assert.False(operation.ReadOnly);
        Assert.True(operation.RequiresAdministrator);
        Assert.True(operation.RequiresConfirmation);
        Assert.False(operation.AvailableToAssistant);
        Assert.True(BusinessOperationCatalog.IsAssistantAction("resume_execution"));

        using var valid = JsonDocument.Parse("{\"id\":\"b588f5bf-0bbd-45b9-a5db-7c73dfc064c7\"}");
        using var invalid = JsonDocument.Parse("{\"id\":\"b588f5bf-0bbd-45b9-a5db-7c73dfc064c7\",\"force\":true}");
        Assert.True(BusinessOperationCatalog.ValidateArguments("resume_execution", valid.RootElement));
        Assert.False(BusinessOperationCatalog.ValidateArguments("resume_execution", invalid.RootElement));
    }

    [Fact]
    public void AssistantKebabNamesMapToMcpSnakeToolsWhenPresent()
    {
        Assert.Equal(6, BusinessOperationCatalog.AssistantToMcpToolNames.Count);

        Assert.True(BusinessOperationCatalog.TryResolveMcpToolName("cancel-execution", out var cancel));
        Assert.Equal("cancel_execution", cancel);
        Assert.NotNull(BusinessOperationCatalog.FindTool(cancel));

        Assert.True(BusinessOperationCatalog.TryResolveMcpToolName("delete-flow", out var delete));
        Assert.Equal("delete_flow", delete);

        Assert.True(BusinessOperationCatalog.TryResolveMcpToolName("assign-library-flow", out var assign));
        Assert.Equal("assign_library_flow", assign);

        Assert.True(BusinessOperationCatalog.TryResolveMcpToolName("create-flow-template", out var template));
        Assert.Equal("create_template_from_flow", template);

        Assert.False(BusinessOperationCatalog.TryResolveMcpToolName("publish-flow", out _));
        Assert.False(BusinessOperationCatalog.TryResolveMcpToolName("start-library", out _));
        Assert.Null(BusinessOperationCatalog.AssistantToMcpToolNames["publish-flow"]);
        Assert.Null(BusinessOperationCatalog.AssistantToMcpToolNames["start-library"]);
    }

    [Fact]
    public void MechanicalKebabSnakeConversionRoundTripsCatalogTools()
    {
        foreach (var tool in BusinessOperationCatalog.ToolDefinitions)
        {
            var kebab = BusinessOperationCatalog.ToAssistantName(tool.Name);
            Assert.Equal(tool.Name, BusinessOperationCatalog.ToMcpName(kebab));
            Assert.True(BusinessOperationCatalog.TryResolveMcpToolName(tool.Name, out var resolved));
            Assert.Equal(tool.Name, resolved);
        }
    }
}
