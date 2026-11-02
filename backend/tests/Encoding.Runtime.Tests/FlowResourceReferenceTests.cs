using System.Text.Json;
using Encoding.Contracts.Flows;
using Encoding.NodeAgent.Services;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class FlowResourceReferenceTests
{
    [Fact]
    public void AProducedFileMustComeFromAnEarlierProducerInTheGraph()
    {
        var (registry, validator, _) = Harness.Services();
        var reference = new FlowArtifactReference("producer", 0).ToString();
        var values = new Dictionary<string, JsonElement> { ["sourceFile"] = JsonSerializer.SerializeToElement(reference) };
        var flow = new FlowDefinition(1, Guid.NewGuid(), 1, "Fichier produit", "video", [
            new("input", null, registry.FindReference("base-input-file")!, new Dictionary<string, JsonElement>()),
            new("producer", null, registry.FindReference("mkv-extract-chapters")!, new Dictionary<string, JsonElement>()),
            new("consumer", null, registry.FindReference("mkv-replace-chapters")!, values)
        ], [new("first", "input", "success", "producer"), new("second", "producer", "success", "consumer")]);
        Assert.True(validator.Validate(flow).IsValid);
        values["sourceFile"] = JsonSerializer.SerializeToElement(new FlowArtifactReference("input", 0).ToString());
        Assert.Contains(validator.Validate(flow).Issues, issue => issue.Code == "artifact.reference.invalid");
        values["sourceFile"] = JsonSerializer.SerializeToElement(reference);
        var reversed = flow with { Connections = [new("first", "input", "success", "consumer"), new("second", "consumer", "success", "producer")] };
        Assert.Contains(validator.Validate(reversed).Issues, issue => issue.Code == "artifact.reference.invalid");
    }

    [Fact]
    public void ProducedFilesHaveStableCheckpointReferencesAndMissingBranchesFailExplicitly()
    {
        var reference = new FlowArtifactReference("Extraire / chapitres", 0);
        Assert.True(FlowArtifactReference.TryParse(reference.ToString(), out var parsed));
        Assert.Equal(reference, parsed);
        var variables = new Dictionary<string, JsonElement>();
        Assert.Throws<FlowArtifactReferenceException>(() => reference.Resolve(variables));
        variables[reference.VariableKey] = JsonSerializer.SerializeToElement(new { sha256 = new string('a', 64), name = "chapters.xml" });
        var restored = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(variables))!;
        Assert.Equal("chapters.xml", reference.Resolve(restored).GetProperty("name").GetString());
        restored[reference.VariableKey] = JsonSerializer.SerializeToElement(new { sha256 = "../../outside", name = "chapters.xml" });
        Assert.Throws<FlowArtifactReferenceException>(() => reference.Resolve(restored));
        foreach (var name in new[] { "..", "../outside", "folder\\file", "drive:file" })
        {
            restored[reference.VariableKey] = JsonSerializer.SerializeToElement(new { sha256 = new string('a', 64), name });
            Assert.Throws<FlowArtifactReferenceException>(() => reference.Resolve(restored));
        }
        Assert.False(FlowArtifactReference.TryParse("artifact:producer/../outside", out _));
        Assert.False(FlowArtifactReference.TryParse("artifact:producer/64", out _));
    }

    [Fact]
    public void FontsMustExistOnTheSelectedWorkerIncludingAfterRelay()
    {
        var (registry, _, eligibility) = Harness.Services();
        var flow = new FlowDefinition(1, Guid.NewGuid(), 1, "Fonts", "video", [
            new("input", null, registry.FindReference("base-input-file")!, new Dictionary<string, JsonElement>()),
            new("fonts", null, registry.FindReference("subtitles-add-fonts")!, new Dictionary<string, JsonElement> { ["fontSet"] = JsonSerializer.SerializeToElement("Anime") })
        ], [new("next", "input", "success", "fonts")]);
        Encoding.Contracts.Nodes.NodeToolCapability[] caps = [
            new("plugin/base", "1.7.0", ["general"], []), new("plugin/subtitles", "1.1.0", ["video"], []),
            new("mkvtoolnix", "90.0", ["video"], []), new("ffprobe", "7.0", ["video"], [])];
        var worker = Guid.NewGuid();
        Assert.False(eligibility.CanRun(flow, caps, worker));
        Assert.False(eligibility.CanRun(flow, caps, worker, inventory: new("test", "x64", 1, 1, FontSets: [])));
        Assert.False(eligibility.CanRun(flow, caps, worker, inventory: new("test", "x64", 1, 1, FontSets: ["Other"])));
        Assert.True(eligibility.CanRun(flow, caps, worker, inventory: new("test", "x64", 1, 1, FontSets: ["Anime"])));
        Assert.False(eligibility.CanRun(flow, caps, worker, "fonts", true));
        Assert.True(eligibility.CanRun(flow, caps, worker, "fonts", true, inventory: new("test", "x64", 1, 1, FontSets: ["Anime"])));
    }

    [Fact]
    public void ACatalogResourceReplacesTheLocalFontSetRequirementAndNeedsTheNewPluginVersion()
    {
        var (registry, _, eligibility) = Harness.Services();
        var reference = new ResourceReference(Guid.NewGuid(), 2).ToString();
        var fonts = registry.FindReference("subtitles-add-fonts")!;
        var values = new Dictionary<string, JsonElement> { ["resource"] = JsonSerializer.SerializeToElement(reference) };
        Assert.Equal("1.3.0", registry.MinimumRuntime(fonts, values)!.Value.Version);
        Assert.Null(registry.MinimumRuntime(fonts, new Dictionary<string, JsonElement> { ["resource"] = JsonSerializer.SerializeToElement("") }));

        var flow = new FlowDefinition(1, Guid.NewGuid(), 1, "Fonts", "video", [
            new("input", null, registry.FindReference("base-input-file")!, new Dictionary<string, JsonElement>()),
            new("fonts", null, fonts, values)
        ], [new("next", "input", "success", "fonts")]);
        Encoding.Contracts.Nodes.NodeToolCapability[] Caps(string subtitlesVersion) => [
            new("plugin/base", "1.7.0", ["general"], []), new("plugin/subtitles", subtitlesVersion, ["video"], []),
            new("mkvtoolnix", "90.0", ["video"], []), new("ffprobe", "7.0", ["video"], [])];
        var worker = Guid.NewGuid();
        // Aucun jeu local n'est exigé, mais un worker dont le plugin ignore les ressources ne peut pas prendre le segment.
        Assert.True(eligibility.CanRun(flow, Caps("1.3.0"), worker, inventory: new("test", "x64", 1, 1, FontSets: [])));
        Assert.False(eligibility.CanRun(flow, Caps("1.2.0"), worker, inventory: new("test", "x64", 1, 1, FontSets: [])));
    }

    [Fact]
    public void AResourceReferenceIsAnIdentifierAndAPositiveVersion()
    {
        var id = Guid.NewGuid();
        Assert.True(ResourceReference.TryParse($"{id}:3", out var parsed));
        Assert.Equal(new ResourceReference(id, 3), parsed);
        Assert.Equal($"{id:D}:3", parsed.ToString());
        foreach (var bad in new[] { "", $"{id}", $"{id}:0", $"{id}:-1", $"{id}:1:2", "nope:1", $"{Guid.Empty}:1", $"{id}:x" })
            Assert.False(ResourceReference.TryParse(bad, out _), bad);
    }

    [Fact]
    public void FontInventoryIsBoundedAndContainsNamesRatherThanPaths()
    {
        var directory = Path.Combine(Path.GetTempPath(), "encoding-font-inventory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            for (var index = 0; index < 70; index++)
            {
                var set = Directory.CreateDirectory(Path.Combine(directory, "Set " + index.ToString("D2")));
                File.WriteAllText(Path.Combine(set.FullName, "font.ttf"), "synthetic inventory entry");
            }
            Directory.CreateDirectory(Path.Combine(directory, "Empty"));
            Directory.CreateDirectory(Path.Combine(directory, "invalid..set"));
            var sets = FontInventory.Read(directory)!;
            Assert.Equal(64, sets.Count);
            Assert.Equal("Set 00", sets[0]);
            Assert.DoesNotContain("invalid..set", sets);
            Assert.DoesNotContain("Empty", sets);
            Assert.All(sets, name => Assert.DoesNotContain(Path.DirectorySeparatorChar, name));
        }
        finally { Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(directory)); Directory.Delete(directory, true); }
    }
}
