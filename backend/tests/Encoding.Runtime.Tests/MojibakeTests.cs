using System.Text.Json;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Text;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class MojibakeTests
{
    [Theory]
    [InlineData("Fichier vidÃ©o", "Fichier vidéo")]
    [InlineData("Ã‰tape terminÃ©e : Fichier vidÃ©o.", "Étape terminée : Fichier vidéo.")]
    [InlineData("Sous-titres â€“ prÃªts", "Sous-titres – prêts")]
    [InlineData("Fichier vidéo", "Fichier vidéo")]
    [InlineData("Ça va très bien", "Ça va très bien")]
    [InlineData("Ã", "Ã")]
    [InlineData("", "")]
    public void RepairsOnlyUtf8ReadAsWindows1252(string input, string expected) =>
        Assert.Equal(expected, Mojibake.Repair(input));

    [Fact]
    public void FlowDefinitionNamesAndConfigurationAreRepaired()
    {
        var config = new Dictionary<string, JsonElement>
        {
            ["label"] = JsonSerializer.SerializeToElement("PrÃ©parer"),
            ["list"] = JsonSerializer.SerializeToElement(new[] { "Ã©", "ok" }),
            ["count"] = JsonSerializer.SerializeToElement(3),
        };
        var flow = new FlowDefinition(1, Guid.NewGuid(), 1, "HÃ©vc", "video",
            [new("n1", "Fichier vidÃ©o", new("p", "1", "c", "1", 1), config)], []);
        var repaired = flow.RepairText();
        Assert.Equal("Hévc", repaired.Name);
        Assert.Equal("Fichier vidéo", repaired.Nodes[0].Name);
        Assert.Equal("Préparer", repaired.Nodes[0].Configuration["label"].GetString());
        Assert.Equal("é", repaired.Nodes[0].Configuration["list"][0].GetString());
        Assert.Equal(3, repaired.Nodes[0].Configuration["count"].GetInt32());
    }

    [Fact]
    public void RepairKeepsUnchangedArraysAndNestedObjectsWithoutThrowing()
    {
        var config = new Dictionary<string, JsonElement>
        {
            ["languages"] = JsonSerializer.SerializeToElement(new[] { "fra", "jpn" }),
            ["rules"] = JsonSerializer.SerializeToElement(new[] { new { codec = "aac", tags = new[] { "a", "b" } } }),
        };
        var flow = new FlowDefinition(1, Guid.NewGuid(), 1, "Flow", "video",
            [new("n1", null, new("p", "1", "c", "1", 1), config)], []);
        var repaired = flow.RepairText();
        Assert.Equal("jpn", repaired.Nodes[0].Configuration["languages"][1].GetString());
        Assert.Equal("b", repaired.Nodes[0].Configuration["rules"][0].GetProperty("tags")[1].GetString());
    }
}
