using System.Text.Json;
using Encoding.Api.Executions;
using Encoding.Api.Flows;
using Encoding.Api.Infrastructure;
using Encoding.Api.Nodes;
using Encoding.Api.Resources;
using Encoding.Contracts.Flows;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class FlowCoordinatorStressTests
{
    private static FlowComponentRegistry CreateRegistry() => StoreCatalogFixture.Create();

    [Theory]
    // 1. Non-object root elements
    [InlineData("[]")]
    [InlineData("[1, 2, 3]")]
    [InlineData("null")]
    [InlineData("\"string\"")]
    [InlineData("123")]
    [InlineData("true")]
    [InlineData("false")]
    // 2. Field type mismatches
    [InlineData("{\"name\": 123}")]
    [InlineData("{\"mediaType\": 123}")]
    [InlineData("{\"type\": 123}")]
    [InlineData("{\"id\": 123}")]
    [InlineData("{\"id\": \"not-a-guid\"}")]
    [InlineData("{\"formatVersion\": \"not-an-int\"}")]
    [InlineData("{\"version\": \"not-an-int\"}")]
    // 3. Malformed nodes array
    [InlineData("{\"nodes\": [null]}")]
    [InlineData("{\"nodes\": [123]}")]
    [InlineData("{\"nodes\": [\"foo\"]}")]
    [InlineData("{\"nodes\": [true]}")]
    [InlineData("{\"nodes\": [[]]}")]
    // 4. Malformed node property types
    [InlineData("{\"nodes\": [{\"id\": 123}]}")]
    [InlineData("{\"nodes\": [{\"name\": 123}]}")]
    [InlineData("{\"nodes\": [{\"component\": 123}]}")]
    [InlineData("{\"nodes\": [{\"configuration\": \"not-an-object\"}]}")]
    [InlineData("{\"nodes\": [{\"configuration\": 123}]}")]
    // 5. ConfigurationSchemaVersion type mismatch
    [InlineData("{\"nodes\": [{\"component\": {\"componentId\": \"c\", \"pluginId\": \"p\", \"pluginVersion\": \"1\", \"componentVersion\": \"1\", \"configurationSchemaVersion\": \"not-an-int\"}}]}")]
    [InlineData("{\"nodes\": [{\"component\": {\"componentId\": \"c\", \"pluginId\": \"p\", \"pluginVersion\": \"1\", \"componentVersion\": \"1\", \"configurationSchemaVersion\": null}}]}")]
    [InlineData("{\"nodes\": [{\"component\": {\"componentId\": \"c\", \"pluginId\": \"p\", \"pluginVersion\": \"1\", \"componentVersion\": \"1\", \"configurationSchemaVersion\": true}}]}")]
    // 6. Malformed connections array
    [InlineData("{\"connections\": [null]}")]
    [InlineData("{\"connections\": [123]}")]
    [InlineData("{\"connections\": [\"foo\"]}")]
    // 7. Malformed connection fields
    [InlineData("{\"connections\": [{\"id\": 123}]}")]
    [InlineData("{\"connections\": [{\"sourceNodeId\": 123}]}")]
    [InlineData("{\"connections\": [{\"sourceOutputId\": 123}]}")]
    [InlineData("{\"connections\": [{\"targetNodeId\": 123}]}")]
    // 8. Definition wrapper with non-object
    [InlineData("{\"definition\": \"not-an-object\"}")]
    [InlineData("{\"definition\": [1, 2, 3]}")]
    [InlineData("{\"definition\": null}")]
    [InlineData("{\"definition\": {\"name\": 123, \"nodes\": [null]}}")]
    public void FlowDefinitionParser_NeverThrows_OnMalformedJson(string json)
    {
        var registry = CreateRegistry();
        using var doc = JsonDocument.Parse(json);

        var definition = FlowDefinitionParser.Parse(doc.RootElement, registry);

        Assert.NotNull(definition);
        Assert.NotNull(definition.Name);
        Assert.NotNull(definition.MediaType);
        Assert.NotNull(definition.Nodes);
        Assert.NotNull(definition.Connections);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"string\"")]
    [InlineData("123")]
    [InlineData("true")]
    [InlineData("false")]
    public async Task CreateFlowAsync_ReturnsInvalidJsonFailure_OnNonObjectRoot(string json)
    {
        var registry = CreateRegistry();
        var validator = new FlowDefinitionValidator(registry);
        var eligibility = new RuntimeEligibility(registry, validator);

        await using var database = await TestDatabase.CreateSqliteAsync("flow-stress-nonobj");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);

        var flows = new FlowRepository(database.Db);
        var nodes = new NodeRepository(database.Db);
        var blobStore = new ResourceBlobStore(new ConfigurationBuilder().Build());
        var resources = new ResourceRepository(database.Db, blobStore);
        var templates = new FlowTemplateRepository(database.Db, registry);

        var coordinator = new FlowCoordinatorService(flows, nodes, resources, templates, validator, eligibility);

        using var doc = JsonDocument.Parse(json);
        var outcome = await coordinator.CreateFlowAsync(doc.RootElement, null, registry, CancellationToken.None);

        Assert.False(outcome.IsValid);
        Assert.NotNull(outcome.Validation);
        Assert.Contains(outcome.Validation.Issues, issue =>
            issue.Code == "invalid-json" && issue.Message == "Le corps de la requête doit être un objet JSON.");
    }

    [Theory]
    [InlineData("{\"nodes\": [null]}")]
    [InlineData("{\"nodes\": [123]}")]
    [InlineData("{\"nodes\": [{\"id\": 123}]}")]
    [InlineData("{\"connections\": [null]}")]
    [InlineData("{\"connections\": [123]}")]
    [InlineData("{\"nodes\": [{\"component\": {\"componentId\": \"c\", \"pluginId\": \"p\", \"pluginVersion\": \"1\", \"componentVersion\": \"1\", \"configurationSchemaVersion\": \"not-an-int\"}}]}")]
    public async Task CreateFlowAsync_NeverThrows_OnMalformedObjectJson(string json)
    {
        var registry = CreateRegistry();
        var validator = new FlowDefinitionValidator(registry);
        var eligibility = new RuntimeEligibility(registry, validator);

        await using var database = await TestDatabase.CreateSqliteAsync("flow-stress-malformed");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);

        var flows = new FlowRepository(database.Db);
        var nodes = new NodeRepository(database.Db);
        var blobStore = new ResourceBlobStore(new ConfigurationBuilder().Build());
        var resources = new ResourceRepository(database.Db, blobStore);
        var templates = new FlowTemplateRepository(database.Db, registry);

        var coordinator = new FlowCoordinatorService(flows, nodes, resources, templates, validator, eligibility);

        using var doc = JsonDocument.Parse(json);
        var outcome = await coordinator.CreateFlowAsync(doc.RootElement, false, registry, CancellationToken.None);

        Assert.NotNull(outcome);
    }
}
