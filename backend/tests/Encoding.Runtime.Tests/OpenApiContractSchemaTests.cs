using Encoding.Api.Infrastructure;
using Encoding.Contracts.Executions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class OpenApiContractSchemaTests
{
    [Fact]
    public void RuntimeAndExecutionContractSchemasArePublished()
    {
        Assert.Contains("ExecutionState", OpenApiEndpoints.ContractSchemas.Keys);
        Assert.Contains("ExecutionResponse", OpenApiEndpoints.ContractSchemas.Keys);
        Assert.Contains("ExecutionOverviewResponse", OpenApiEndpoints.ContractSchemas.Keys);
        Assert.Contains("FinishRuntimeExecution", OpenApiEndpoints.ContractSchemas.Keys);
        Assert.Contains("RuntimeLease", OpenApiEndpoints.ContractSchemas.Keys);
        Assert.Contains("RuntimeExecutionState", OpenApiEndpoints.ContractSchemas.Keys);
        Assert.Contains("ProblemDetails", OpenApiEndpoints.ContractSchemas.Keys);
    }

    [Fact]
    public void ExecutionStateSchemaEnumeratesNamedConstants()
    {
        var schema = OpenApiEndpoints.ContractSchemas["ExecutionState"];
        var json = System.Text.Json.JsonSerializer.Serialize(schema);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var values = doc.RootElement.GetProperty("enum").EnumerateArray().Select(item => item.GetString()).ToArray();
        foreach (var state in ExecutionStates.All)
            Assert.Contains(state, values);
    }

    [Fact]
    public void BuildSpecificationEmbedsContractSchemas()
    {
        var dataSource = new DefaultEndpointDataSource(Array.Empty<Endpoint>());
        var spec = System.Text.Json.JsonSerializer.SerializeToElement(OpenApiEndpoints.BuildSpecification(dataSource));
        var schemas = spec.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.TryGetProperty("ExecutionResponse", out _));
        Assert.True(schemas.TryGetProperty("FinishRuntimeExecution", out _));
        Assert.Equal("3.1.0", spec.GetProperty("openapi").GetString());
    }
}
