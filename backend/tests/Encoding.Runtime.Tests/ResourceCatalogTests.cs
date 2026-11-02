using System.Text.Json;
using Encoding.Api.Infrastructure;
using Encoding.Api.Plugins;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Infrastructure.Data.Sqlite;
using Encoding.Api.Resources;
using Encoding.Contracts.Flows;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class ResourceCatalogTests
{
    private static readonly byte[] Font = [0x00, 0x01, 0x00, 0x00, 1, 2, 3, 4, 5, 6, 7, 8];

    private sealed class Scope : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "encoding-resources-" + Guid.NewGuid().ToString("N"));
        public SqliteDataSource Source { get; private set; } = null!;
        public ResourceBlobStore Blobs { get; private set; } = null!;
        public ResourceRepository Repository { get; private set; } = null!;

        public static async Task<Scope> CreateAsync()
        {
            var scope = new Scope();
            Directory.CreateDirectory(scope.root);
            scope.Source = new SqliteDataSource($"Data Source={Path.Combine(scope.root, "cluster.db")};Pooling=False");
            await DatabaseMigrator.ApplyAsync(scope.Source, Harness.RepositoryRoot(), CancellationToken.None);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Resources:Directory"] = Path.Combine(scope.root, "blobs") }).Build();
            scope.Blobs = new ResourceBlobStore(configuration);
            scope.Repository = new ResourceRepository(scope.Source, scope.Blobs);
            return scope;
        }

        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task AFontIsStoredByDigestAndVersionedWithoutRewritingHistory()
    {
        await using var scope = await Scope.CreateAsync();
        var file = await scope.Blobs.PutAsync(new MemoryStream(Font), "Arial.ttf", "font", CancellationToken.None);
        Assert.Equal(Font.Length, file.Bytes);
        Assert.True(scope.Blobs.Exists(file.Sha256));
        var again = await scope.Blobs.PutAsync(new MemoryStream(Font), "Autre.ttf", "font", CancellationToken.None);
        Assert.Equal(file.Sha256, again.Sha256);

        var id = Guid.NewGuid();
        var created = await scope.Repository.CreateAsync(id, "font", "Crunchyroll", "", [new(file.Name, file.Sha256)], CancellationToken.None);
        Assert.Equal(1, created!.Summary.LatestVersion);

        var second = await scope.Blobs.PutAsync(new MemoryStream([.. Font, 9]), "Arial.ttf", "font", CancellationToken.None);
        var version = await scope.Repository.AddVersionAsync(id, [new("Arial.ttf", second.Sha256)], CancellationToken.None);
        Assert.Equal(2, version!.Version);
        var details = await scope.Repository.GetAsync(id, CancellationToken.None);
        Assert.Equal([2, 1], details!.Versions.Select(item => item.Version));
        Assert.Equal(file.Sha256, details.Versions[1].Files[0].Sha256);
    }

    [Fact]
    public async Task UploadsAreCheckedByContentNameAndReference()
    {
        await using var scope = await Scope.CreateAsync();
        await Assert.ThrowsAsync<ResourceException>(() => scope.Blobs.PutAsync(new MemoryStream("MZ not a font"u8.ToArray()), "Fake.ttf", "font", CancellationToken.None));
        await Assert.ThrowsAsync<ResourceException>(() => scope.Blobs.PutAsync(new MemoryStream(Font), "Arial.exe", "font", CancellationToken.None));
        await Assert.ThrowsAsync<ResourceException>(() => scope.Blobs.PutAsync(new MemoryStream(Font), "../Arial.ttf", "font", CancellationToken.None));
        await Assert.ThrowsAsync<ResourceException>(() => scope.Blobs.PutAsync(new MemoryStream(), "Vide.ttf", "font", CancellationToken.None));
        await Assert.ThrowsAsync<ResourceException>(() => scope.Repository.CreateAsync(Guid.NewGuid(), "font", "Inconnue", "", [new("A.ttf", new string('a', 64))], CancellationToken.None));
        Assert.Empty(await scope.Repository.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DeletionIsRefusedWhileAFlowReferencesTheResourceThenRemovesUnsharedFiles()
    {
        await using var scope = await Scope.CreateAsync();
        var file = await scope.Blobs.PutAsync(new MemoryStream(Font), "Arial.ttf", "font", CancellationToken.None);
        var id = Guid.NewGuid();
        await scope.Repository.CreateAsync(id, "font", "Polices", "", [new(file.Name, file.Sha256)], CancellationToken.None);

        var flowId = Guid.NewGuid();
        var definition = JsonSerializer.Serialize(new
        {
            formatVersion = 1, id = flowId, version = 1, name = "Mon flow", mediaType = "video",
            nodes = new[] { new { id = "fonts", name = "Polices", component = new { id = "subtitles-add-fonts", version = "1.0.0" }, configuration = new { resource = id } } },
            connections = Array.Empty<object>()
        });
        await using (var connection = await scope.Source.OpenConnectionAsync(CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO flows(id,current_version) VALUES(@f,1); INSERT INTO flow_versions(flow_id,version,definition) VALUES(@f,1,@d)";
            foreach (var (name, value) in new[] { ("f", flowId.ToString()), ("d", definition) })
            {
                var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter);
            }
            await command.ExecuteNonQueryAsync();
        }

        var usage = Assert.Single(await scope.Repository.UsageAsync(id, CancellationToken.None));
        Assert.Equal("Mon flow", usage.FlowName);
        Assert.Equal("Polices", usage.NodeName);

        Assert.True(await scope.Repository.DeleteAsync(id, CancellationToken.None));
        Assert.False(scope.Blobs.Exists(file.Sha256));
        Assert.Null(await scope.Repository.GetAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task PublishingChecksThatTheReferencedResourceVersionExists()
    {
        await using var scope = await Scope.CreateAsync();
        var file = await scope.Blobs.PutAsync(new MemoryStream(Font), "Arial.ttf", "font", CancellationToken.None);
        var id = Guid.NewGuid();
        await scope.Repository.CreateAsync(id, "font", "Polices", "", [new(file.Name, file.Sha256)], CancellationToken.None);
        var (registry, _, _) = Harness.Services();
        FlowDefinition Flow(string resource) => new(1, Guid.NewGuid(), 1, "Flow", "video", [
            new("input", null, registry.FindReference("base-input-file")!, new Dictionary<string, JsonElement>()),
            new("fonts", "Polices", registry.FindReference("subtitles-add-fonts")!, new Dictionary<string, JsonElement> { ["resource"] = JsonSerializer.SerializeToElement(resource) })
        ], [new("next", "input", "success", "fonts")]);

        Assert.Empty(await ResourceReferenceCheck.ProblemsAsync(Flow(""), scope.Repository, CancellationToken.None));
        Assert.Empty(await ResourceReferenceCheck.ProblemsAsync(Flow($"{id}:1"), scope.Repository, CancellationToken.None));
        Assert.Contains("n'a pas de version 2", Assert.Single(await ResourceReferenceCheck.ProblemsAsync(Flow($"{id}:2"), scope.Repository, CancellationToken.None)));
        Assert.Contains("n'a pas de version 1", Assert.Single(await ResourceReferenceCheck.ProblemsAsync(Flow($"{Guid.NewGuid()}:1"), scope.Repository, CancellationToken.None)));
        Assert.Contains("invalide", Assert.Single(await ResourceReferenceCheck.ProblemsAsync(Flow("n'importe quoi"), scope.Repository, CancellationToken.None)));
        var other = Guid.NewGuid();
        await scope.Repository.CreateAsync(other, "font", "Autre.ttf", "", [new(file.Name, file.Sha256)], CancellationToken.None);
        Assert.Empty(await ResourceReferenceCheck.ProblemsAsync(Flow($"{id}:1, {other}:1"), scope.Repository, CancellationToken.None));
        Assert.Contains("n'a pas de version 2", Assert.Single(await ResourceReferenceCheck.ProblemsAsync(Flow($"{id}:1, {other}:2"), scope.Repository, CancellationToken.None)));
    }

    [Fact]
    public async Task ABundleIsCopiedIntoOneResourcePerFileAndHiddenFromTheCatalog()
    {
        await using var scope = await Scope.CreateAsync();
        var bundle = Guid.Parse("45a83fc2-376a-4c58-9d7d-e06fe567c19f");
        var files = """
            [{"name":"Arial Bold.ttf","sha256":"893af24f334b23a68f0233ccfa5b19a1c87b9fc6050ba01c34e91fc8e8f62a77","bytes":751104},{"name":"Trebuchet MS Bold.ttf","sha256":"23a470ae5174e63654ecd69d5e1c128ddcaabd43653c06fc9c1835f02a956933","bytes":143336}]
            """;
        await using (var connection = await scope.Source.OpenConnectionAsync(CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO flow_resources(id,kind,name,description) VALUES(@id,'font','Polices ASS','lot');
                INSERT INTO flow_resource_versions(resource_id,version,files,total_bytes) VALUES(@id,1,@files,894440);
                DELETE FROM schema_migrations WHERE version='0064_flat_flow_resources';
                """;
            var id = command.CreateParameter(); id.ParameterName = "id"; id.Value = bundle.ToString(); command.Parameters.Add(id);
            var document = command.CreateParameter(); document.ParameterName = "files"; document.Value = files; command.Parameters.Add(document);
            await command.ExecuteNonQueryAsync();
        }

        await DatabaseMigrator.ApplyAsync(scope.Source, Harness.RepositoryRoot(), CancellationToken.None);
        await DatabaseMigrator.ApplyAsync(scope.Source, Harness.RepositoryRoot(), CancellationToken.None);

        var listed = await scope.Repository.ListAsync(CancellationToken.None);
        Assert.DoesNotContain(listed, item => item.Id == bundle);
        Assert.Contains(listed, item => item.Name == "Arial Bold.ttf");
        Assert.Contains(listed, item => item.Name == "Trebuchet MS Bold.ttf");
        var arial = await scope.Repository.GetAsync(listed.Single(item => item.Name == "Arial Bold.ttf").Id, CancellationToken.None);
        Assert.Equal("893af24f334b23a68f0233ccfa5b19a1c87b9fc6050ba01c34e91fc8e8f62a77", Assert.Single(Assert.Single(arial!.Versions).Files).Sha256);
        var kept = await scope.Repository.GetVersionAsync(bundle, 1, CancellationToken.None);
        Assert.Equal(2, kept!.Files.Count);
        await Assert.ThrowsAsync<ResourceException>(() => scope.Repository.CreateAsync(Guid.NewGuid(), "font", "Deux", "", [new("A.ttf", new string('a', 64)), new("B.ttf", new string('b', 64))], CancellationToken.None));
    }

    [Fact]
    public async Task AProfileAndASubtitleFileAreStoredAndCitedByKind()
    {
        await using var scope = await Scope.CreateAsync();
        var profile = """{"formatVersion":1,"profileVersion":"1","reference":{"width":640,"height":360},"styles":[{"name":"Default","fontName":"Arial","fontSize":23,"primaryColour":"&H00FFFFFF","secondaryColour":"&H000000FF","outlineColour":"&H00000000","backColour":"&H00000000"}]}""";
        var file = await scope.Blobs.PutAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(profile)), "Profil.json", "subtitle-profile", CancellationToken.None);
        await Assert.ThrowsAsync<ResourceException>(() => scope.Blobs.PutAsync(new MemoryStream("{}"u8.ToArray()), "Mauvais.json", "subtitle-profile", CancellationToken.None));
        var subtitle = await scope.Blobs.PutAsync(new MemoryStream("1\n00:00:00,000 --> 00:00:01,000\nBonjour\n"u8.ToArray()), "Francais.srt", "subtitle", CancellationToken.None);
        await Assert.ThrowsAsync<ResourceException>(() => scope.Blobs.PutAsync(new MemoryStream([0, 1, 2]), "Binaire.srt", "subtitle", CancellationToken.None));

        var profileId = Guid.NewGuid();
        var subtitleId = Guid.NewGuid();
        await scope.Repository.CreateAsync(profileId, "subtitle-profile", "Profil", "", [new(file.Name, file.Sha256)], CancellationToken.None);
        await scope.Repository.CreateAsync(subtitleId, "subtitle", "Francais.srt", "", [new(subtitle.Name, subtitle.Sha256)], CancellationToken.None);
        var (registry, _, _) = Harness.Services();
        FlowDefinition Flow(string component, string field, string value) => new(1, Guid.NewGuid(), 1, "Flow", "video", [
            new("input", null, registry.FindReference("base-input-file")!, new Dictionary<string, JsonElement>()),
            new("node", "Sous-titres", registry.FindReference(component)!, new Dictionary<string, JsonElement> { [field] = JsonSerializer.SerializeToElement(value) })
        ], [new("next", "input", "success", "node")]);

        Assert.Empty(await ResourceReferenceCheck.ProblemsAsync(Flow("subtitles-convert-ass", "profile", $"{profileId}:1"), scope.Repository, CancellationToken.None));
        Assert.Contains("font", Assert.Single(await ResourceReferenceCheck.ProblemsAsync(Flow("subtitles-add-fonts", "resource", $"{profileId}:1"), scope.Repository, CancellationToken.None)));
        Assert.Empty(await ResourceReferenceCheck.ProblemsAsync(Flow("subtitles-add-track", "resource", $"{subtitleId}:1"), scope.Repository, CancellationToken.None));
        Assert.Contains("subtitle", Assert.Single(await ResourceReferenceCheck.ProblemsAsync(Flow("subtitles-add-track", "resource", $"{profileId}:1"), scope.Repository, CancellationToken.None)));
    }

    [Fact]
    public void RepositoryIndexKeepsContiguousResourcesAndFlows()
    {
        var profileId = Guid.NewGuid();
        var flowId = Guid.NewGuid();
        var sha = new string('a', 64);
        var json = $$"""
            {
              "schemaVersion": "1.0.0",
              "name": "Dépôt",
              "resources": [
                {
                  "id": "{{profileId}}", "name": "Profil", "kind": "subtitle-profile", "summary": "Styles", "latestVersion": 2,
                  "versions": [
                    { "version": 1, "fileName": "a.json", "downloadUrl": "https://example.com/a.json", "sha256": "{{sha}}" },
                    { "version": 2, "fileName": "b.json", "downloadUrl": "https://example.com/b.json", "sha256": "{{sha}}" }
                  ]
                },
                {
                  "id": "{{Guid.NewGuid()}}", "name": "Trou", "kind": "subtitle", "summary": "", "latestVersion": 2,
                  "versions": [ { "version": 2, "fileName": "x.srt", "downloadUrl": "https://example.com/x.srt", "sha256": "{{sha}}" } ]
                }
              ],
              "flows": [
                {
                  "id": "{{flowId}}", "name": "Flow", "summary": "Import", "latestVersion": 1,
                  "versions": [ { "version": 1, "downloadUrl": "https://example.com/flow.json", "sha256": "{{sha}}" } ]
                }
              ]
            }
            """;
        var parsed = PluginRepositoryService.ParseIndex(
            new PluginRepository("depot", "Dépôt", "https://example.com/repository.json", "", false, true, null, null, []), json);
        var resource = Assert.Single(parsed.Resources!);
        Assert.Equal(profileId, resource.Id);
        Assert.Equal(2, resource.Versions.Count);
        Assert.Equal(flowId, Assert.Single(parsed.Flows!).Id);
        Assert.Contains("ignorée", parsed.LastError);
    }
}
