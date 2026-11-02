using System.Text.Json;
using Encoding.Api.Infrastructure;
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
    public async Task ResourceMetadataReplicatesButTheFilesStayInTheLocalStore()
    {
        await using var source = await Scope.CreateAsync();
        await using var target = await Scope.CreateAsync();
        var clusterId = Guid.NewGuid();
        await new ClusterIdentityRepository(source.Source).AdoptAsync(clusterId, "Media", CancellationToken.None);
        await new ClusterIdentityRepository(target.Source).AdoptAsync(clusterId, "Media", CancellationToken.None);
        var file = await source.Blobs.PutAsync(new MemoryStream(Font), "Arial.ttf", "font", CancellationToken.None);
        var id = Guid.NewGuid();
        await source.Repository.CreateAsync(id, "font", "Crunchyroll", "", [new(file.Name, file.Sha256)], CancellationToken.None);

        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        using var sourceKey = new ClusterDataKeyStore(clusterId, key);
        using var targetKey = new ClusterDataKeyStore(clusterId, key);
        var sourceMesh = new ClusterEntityReconciliation(source.Source, sourceKey, new EphemeralDataProtectionProvider(), NullLogger<ClusterEntityReconciliation>.Instance);
        var targetMesh = new ClusterEntityReconciliation(target.Source, targetKey, new EphemeralDataProtectionProvider(), NullLogger<ClusterEntityReconciliation>.Instance);
        await sourceMesh.CaptureLocalChangesAsync(CancellationToken.None);
        var changes = await new ClusterReconciliationRepository(source.Source).GetEventsAfterAsync(clusterId, new Dictionary<Guid, long>(), ClusterReconciliationRepository.MaximumBatchSize, CancellationToken.None);
        await new ClusterReconciliationRepository(target.Source).ApplyIncomingAsync(changes, CancellationToken.None);
        await targetMesh.ApplyPendingAsync(CancellationToken.None);

        var replicated = await target.Repository.GetAsync(id, CancellationToken.None);
        Assert.Equal("Crunchyroll", replicated!.Summary.Name);
        Assert.Equal(file.Sha256, Assert.Single(Assert.Single(replicated.Versions).Files).Sha256);
        Assert.False(target.Blobs.Exists(file.Sha256));
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
    }
}
