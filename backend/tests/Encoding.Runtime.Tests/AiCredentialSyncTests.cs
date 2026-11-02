using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Tools;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>Les connexions IA (Codex, Claude, OpenAI) suivent le même chemin que les autres secrets répliqués : chiffrées, jamais en clair dans le journal.</summary>
public sealed class AiCredentialSyncTests
{
    private const string CodexAuth = "{\"tokens\":{\"access_token\":\"codex-access-secret\",\"refresh_token\":\"codex-refresh-secret\"}}";

    private sealed class Node : IAsyncDisposable
    {
        public required TestDatabase Database { get; init; }
        public required ClusterEntityReconciliation Entities { get; init; }
        public required ClusterReconciliationRepository Journal { get; init; }
        public required AiCredentialStore Store { get; init; }
        public required AiCredentialSyncService Sync { get; init; }
        public required AiCliRuntime Runtime { get; init; }
        public required string Directory { get; init; }
        public string CodexPath => Runtime.CodexAuthPath;
        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            try { System.IO.Directory.Delete(Directory, true); } catch (IOException) { }
        }
    }

    private static async Task<Node> NewNodeAsync(Guid clusterId, byte[] dataKey)
    {
        var database = await TestDatabase.CreateSqliteAsync("ai-credentials");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);
        await new ClusterIdentityRepository(database.Db).AdoptAsync(clusterId, "Media", CancellationToken.None);
        var directory = Path.Combine(Path.GetTempPath(), "encoding-ai-" + Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AiTools:CredentialsDirectory"] = directory }).Build();
        var protection = new EphemeralDataProtectionProvider();
        var runtime = new AiCliRuntime(configuration, protection);
        var store = new AiCredentialStore(database.Db, protection);
        return new Node
        {
            Database = database,
            Entities = new ClusterEntityReconciliation(database.Db, new ClusterDataKeyStore(clusterId, dataKey), protection, NullLogger<ClusterEntityReconciliation>.Instance),
            Journal = new ClusterReconciliationRepository(database.Db),
            Store = store,
            Runtime = runtime,
            Directory = directory,
            Sync = new AiCredentialSyncService(store, runtime, new AiSettingsService(configuration, protection), NullLogger<AiCredentialSyncService>.Instance)
        };
    }

    private static async Task<string> ReplicateAsync(Guid clusterId, Node from, Node to)
    {
        await from.Entities.CaptureLocalChangesAsync(CancellationToken.None);
        var events = await from.Journal.GetEventsAfterAsync(clusterId, await to.Journal.GetVectorAsync(CancellationToken.None),
            ClusterReconciliationRepository.MaximumBatchSize, CancellationToken.None);
        await to.Journal.ApplyIncomingAsync(events, CancellationToken.None);
        await to.Entities.ApplyPendingAsync(CancellationToken.None);
        return string.Join("\n", events.Select(change => change.Payload));
    }

    [Fact]
    public async Task ConnectionIsEncryptedInTheJournalAndReadableOnTheOtherNode()
    {
        var clusterId = Guid.NewGuid();
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        await using var first = await NewNodeAsync(clusterId, key);
        await using var second = await NewNodeAsync(clusterId, key);

        await first.Store.PutAsync("claude-cli", "claude-long-lived-token", CancellationToken.None);
        var payloads = await ReplicateAsync(clusterId, first, second);

        Assert.DoesNotContain("claude-long-lived-token", payloads, StringComparison.Ordinal);
        Assert.Equal("claude-long-lived-token", (await second.Store.GetAsync("claude-cli", CancellationToken.None))?.Secret);

        await first.Store.PutAsync("claude-cli", null, CancellationToken.None);
        await ReplicateAsync(clusterId, first, second);
        var removed = await second.Store.GetAsync("claude-cli", CancellationToken.None);
        Assert.NotNull(removed);
        Assert.Null(removed.Secret);
    }

    [Fact]
    public async Task LoginRenewalAndLogoutFollowAcrossNodesAndSurviveLossOfTheFirstNode()
    {
        var clusterId = Guid.NewGuid();
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        await using var first = await NewNodeAsync(clusterId, key);
        await using var second = await NewNodeAsync(clusterId, key);

        File.WriteAllText(first.CodexPath, CodexAuth);
        await first.Sync.SyncOnceAsync(CancellationToken.None);
        Assert.DoesNotContain("codex-refresh-secret", await ReplicateAsync(clusterId, first, second), StringComparison.Ordinal);
        await second.Sync.SyncOnceAsync(CancellationToken.None);
        Assert.Equal(CodexAuth, File.ReadAllText(second.CodexPath));

        // Le premier nœud disparaît : le second renouvelle seul et reste autonome.
        var renewed = CodexAuth.Replace("codex-access-secret", "codex-access-secret-2", StringComparison.Ordinal);
        File.WriteAllText(second.CodexPath, renewed);
        await second.Sync.SyncOnceAsync(CancellationToken.None);
        Assert.Equal(renewed, (await second.Store.GetAsync("codex-cli", CancellationToken.None))?.Secret);

        // Un renouvellement publié ailleurs est adopté, sans republication en boucle.
        await ReplicateAsync(clusterId, second, first);
        await first.Sync.SyncOnceAsync(CancellationToken.None);
        Assert.Equal(renewed, File.ReadAllText(first.CodexPath));
        Assert.Equal(0, await first.Entities.CaptureLocalChangesAsync(CancellationToken.None));

        File.Delete(first.CodexPath);
        await first.Sync.SyncOnceAsync(CancellationToken.None);
        await ReplicateAsync(clusterId, first, second);
        await second.Sync.SyncOnceAsync(CancellationToken.None);
        Assert.False(File.Exists(second.CodexPath));
    }

    [Fact]
    public async Task ReplicationStatusReportsSyncedAbsentAndRevisionWithoutSecrets()
    {
        var clusterId = Guid.NewGuid();
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        await using var first = await NewNodeAsync(clusterId, key);
        await using var second = await NewNodeAsync(clusterId, key);

        await first.Sync.SyncOnceAsync(CancellationToken.None);
        Assert.All(first.Sync.Statuses(), status => Assert.Equal("absent", status.State));

        File.WriteAllText(first.CodexPath, CodexAuth);
        await first.Sync.SyncOnceAsync(CancellationToken.None);
        var published = Assert.Single(first.Sync.Statuses(), status => status.Provider == "codex-cli");
        Assert.Equal("synced", published.State);
        Assert.Equal(1, published.Revision);
        Assert.NotNull(published.SyncedAt);

        // Avant réception, le second nœud n'a pas la connexion ; après, il est à jour sur la même révision.
        await second.Sync.SyncOnceAsync(CancellationToken.None);
        Assert.Equal("absent", Assert.Single(second.Sync.Statuses(), status => status.Provider == "codex-cli").State);
        await ReplicateAsync(clusterId, first, second);
        await second.Sync.SyncOnceAsync(CancellationToken.None);
        var received = Assert.Single(second.Sync.Statuses(), status => status.Provider == "codex-cli");
        Assert.Equal(("synced", 1L), (received.State, received.Revision));
        Assert.DoesNotContain("codex-refresh-secret", System.Text.Json.JsonSerializer.Serialize(second.Sync.Statuses()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MostRecentChangeWinsWhenBothNodesChangedTheirConnection()
    {
        var clusterId = Guid.NewGuid();
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        await using var first = await NewNodeAsync(clusterId, key);
        await using var second = await NewNodeAsync(clusterId, key);

        File.WriteAllText(first.CodexPath, CodexAuth);
        await first.Sync.SyncOnceAsync(CancellationToken.None);
        await ReplicateAsync(clusterId, first, second);
        await second.Sync.SyncOnceAsync(CancellationToken.None);

        var older = CodexAuth.Replace("codex-access-secret", "from-first", StringComparison.Ordinal);
        var newer = CodexAuth.Replace("codex-access-secret", "from-second", StringComparison.Ordinal);
        File.WriteAllText(first.CodexPath, older);
        await first.Sync.SyncOnceAsync(CancellationToken.None);
        File.WriteAllText(second.CodexPath, newer);
        File.SetLastWriteTimeUtc(second.CodexPath, DateTime.UtcNow.AddMinutes(5));
        await ReplicateAsync(clusterId, first, second);
        await second.Sync.SyncOnceAsync(CancellationToken.None);

        Assert.Equal(newer, (await second.Store.GetAsync("codex-cli", CancellationToken.None))?.Secret);
        await ReplicateAsync(clusterId, second, first);
        await first.Sync.SyncOnceAsync(CancellationToken.None);
        Assert.Equal(newer, File.ReadAllText(first.CodexPath));
    }
}
