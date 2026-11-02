using System.Text.Json;
using Encoding.Api.Flows;
using Encoding.Api.Plugins;
using Encoding.Contracts.Flows;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class PluginConfigurationPrerequisitesTests
{
    [Fact]
    public void IdentifiesBlockAndMissingFieldsWithoutExposingConfiguredSecrets()
    {
        var registry = StoreCatalogFixture.Create();
        var plex = registry.Plugins.Single(plugin => plugin.Id == "plex");
        var component = plex.Components.First();
        var reference = new FlowComponentReference(plex.Id, plex.Version, component.Id, component.Version, component.ConfigurationSchema.Version);
        var flow = new FlowDefinition(1, Guid.NewGuid(), 1, "Synthetic", "video",
            [new("guard", "Guard", reference, new Dictionary<string,JsonElement>())], []);
        var configurations = new Dictionary<string,IReadOnlyDictionary<string,JsonElement>>();
        var missing = PluginConfigurationPrerequisites.FindMissing(flow, registry.Plugins, configurations);
        Assert.Contains(missing, item => item.NodeId == "guard" && item.FieldId == "baseUrl");
        Assert.Contains(missing, item => item.FieldId == "apiKey");
        configurations["plex"] = new Dictionary<string,JsonElement> {
            ["baseUrl"] = JsonSerializer.SerializeToElement("http://synthetic.invalid"),
            ["apiKey"] = JsonSerializer.SerializeToElement("synthetic-secret")
        };
        Assert.Empty(PluginConfigurationPrerequisites.FindMissing(flow, registry.Plugins, configurations));
        Assert.DoesNotContain("synthetic-secret", JsonSerializer.Serialize(missing));
    }

    [Fact]
    public async Task ReadSnapshot_WithCorruptedOrUnknownKey_ReturnsEmptyAndDoesNotThrow()
    {
        await using var db = await TestDatabase.CreateSqliteAsync("plugin-snapshot-cryptofail");
        var repo = new PluginConfigurationRepository(db.Db, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());

        // Des octets protégés avec une clé inconnue ou corrompus
        byte[] invalidData = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        var result = repo.ReadSnapshot(invalidData);

        Assert.NotNull(result);
        Assert.Empty(result);
    }
}
