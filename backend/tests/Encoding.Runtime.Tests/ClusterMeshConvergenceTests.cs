using System.Text.Json;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>Le maillage converge sans quorum, quel que soit l'ordre, la répétition ou le relais des échanges, y compris après une partition.</summary>
public sealed class ClusterMeshConvergenceTests
{
    private sealed record Node(TestDatabase Database, ClusterReconciliationRepository Journal);

    private static async Task<Node[]> NewMeshAsync(int count)
    {
        var clusterId = Guid.NewGuid();
        var nodes = new List<Node>();
        for (var index = 0; index < count; index++)
        {
            var database = await TestDatabase.CreateSqliteAsync("mesh-convergence");
            await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);
            await new ClusterIdentityRepository(database.Db).AdoptAsync(clusterId, "Media", CancellationToken.None);
            nodes.Add(new(database, new(database.Db)));
        }
        return [.. nodes];
    }

    /// <summary>Un échange unidirectionnel : tout ce que <paramref name="from"/> a et que <paramref name="to"/> n'a pas, par lots comme le fait le transport HTTP.</summary>
    private static async Task PullAsync(Node from, Node to, Guid clusterId)
    {
        while (true)
        {
            var events = await from.Journal.GetEventsAfterAsync(clusterId, await to.Journal.GetVectorAsync(CancellationToken.None),
                ClusterReconciliationRepository.MaximumBatchSize, CancellationToken.None);
            if (events.Count == 0) return;
            await to.Journal.ApplyIncomingAsync(events, CancellationToken.None);
        }
    }

    private static async Task<Guid> ClusterIdAsync(Node node)
    {
        await using var command = node.Database.Db.CreateCommand("SELECT id FROM cluster_identity WHERE singleton = true");
        return Guid.Parse(Convert.ToString(await command.ExecuteScalarAsync())!);
    }

    private static async Task<string> StateAsync(Node node)
    {
        await using var command = node.Database.Db.CreateCommand("SELECT register_key, value, origin_id, sequence FROM cluster_sync_registers ORDER BY register_key");
        await using var reader = await command.ExecuteReaderAsync();
        var lines = new List<string>();
        while (await reader.ReadAsync())
            lines.Add($"{reader.GetString(0)}={reader.GetValue(1)}@{Convert.ToString(reader.GetValue(2))}#{Convert.ToInt64(reader.GetValue(3))}");
        return string.Join("\n", lines);
    }

    private static async Task<long> EventCountAsync(Node node)
    {
        await using var command = node.Database.Db.CreateCommand("SELECT count(*) FROM cluster_change_log");
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task DisposeAllAsync(IEnumerable<Node> nodes)
    {
        foreach (var node in nodes) await node.Database.DisposeAsync();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(2026)]
    public async Task ThreeServersWritingDuringAPartitionConvergeInAnyExchangeOrder(int seed)
    {
        var nodes = await NewMeshAsync(3);
        try
        {
            var clusterId = await ClusterIdAsync(nodes[0]);
            var random = new Random(seed);

            // Partition totale : chaque serveur écrit seul, y compris sur les mêmes clés.
            for (var round = 0; round < 12; round++)
                foreach (var node in nodes.OrderBy(_ => random.Next()))
                    await node.Journal.SetRegisterAsync($"test.key{random.Next(0, 4)}", JsonSerializer.Serialize($"{seed}-{round}-{random.Next()}"), CancellationToken.None);
            for (var index = 0; index < nodes.Length; index++)
                Assert.NotEqual(await StateAsync(nodes[(index + 1) % 3]), await StateAsync(nodes[index]));

            // Le réseau revient : échanges dans un ordre aléatoire, avec doublons, jusqu'à stabilité.
            for (var pass = 0; pass < 6; pass++)
            {
                var pairs = (from source in Enumerable.Range(0, 3) from target in Enumerable.Range(0, 3) where source != target select (source, target)).OrderBy(_ => random.Next()).ToArray();
                foreach (var (source, target) in pairs.Take(pass == 0 ? 2 : pairs.Length))
                {
                    await PullAsync(nodes[source], nodes[target], clusterId);
                    if (random.Next(0, 2) == 0) await PullAsync(nodes[source], nodes[target], clusterId);
                }
            }

            var reference = await StateAsync(nodes[0]);
            Assert.NotEmpty(reference);
            Assert.Equal(reference, await StateAsync(nodes[1]));
            Assert.Equal(reference, await StateAsync(nodes[2]));
            Assert.Equal(36L, await EventCountAsync(nodes[0]));
            Assert.Equal(36L, await EventCountAsync(nodes[1]));
            Assert.Equal(36L, await EventCountAsync(nodes[2]));
            var vector = await nodes[0].Journal.GetVectorAsync(CancellationToken.None);
            Assert.Equal(vector, await nodes[1].Journal.GetVectorAsync(CancellationToken.None));
            Assert.Equal(vector, await nodes[2].Journal.GetVectorAsync(CancellationToken.None));
        }
        finally { await DisposeAllAsync(nodes); }
    }

    [Fact]
    public async Task AServerThatWasOfflineCatchesUpThroughARelayWithoutTheOriginalAuthor()
    {
        var nodes = await NewMeshAsync(3);
        try
        {
            var clusterId = await ClusterIdAsync(nodes[0]);
            await nodes[0].Journal.SetRegisterAsync("test.a", "\"from-a\"", CancellationToken.None);
            await PullAsync(nodes[0], nodes[1], clusterId);

            // Le serveur A disparaît ; C se rattrape uniquement auprès de B, qui relaie les événements de A.
            await nodes[1].Journal.SetRegisterAsync("test.b", "\"from-b\"", CancellationToken.None);
            await PullAsync(nodes[1], nodes[2], clusterId);

            var state = await StateAsync(nodes[2]);
            Assert.Contains("test.a=\"from-a\"", state, StringComparison.Ordinal);
            Assert.Contains("test.b=\"from-b\"", state, StringComparison.Ordinal);
            Assert.Equal(await StateAsync(nodes[1]), state);

            // A revient avec ses propres vieilles écritures : rien n'est perdu ni dupliqué.
            await PullAsync(nodes[1], nodes[0], clusterId);
            await PullAsync(nodes[2], nodes[0], clusterId);
            Assert.Equal(state, await StateAsync(nodes[0]));
            Assert.Equal(2L, await EventCountAsync(nodes[0]));
        }
        finally { await DisposeAllAsync(nodes); }
    }

    [Fact]
    public async Task EventsOfAnUnknownTypeAreKeptAndRelayedWithoutBreakingTheExchange()
    {
        var nodes = await NewMeshAsync(3);
        try
        {
            var clusterId = await ClusterIdAsync(nodes[0]);
            var future = new ClusterChangeEvent(Guid.NewGuid(), clusterId, Guid.NewGuid(), 1, "feature.from-a-newer-version",
                "{\"anything\":[1,2,3],\"unknown\":true}", DateTimeOffset.UtcNow);
            await nodes[1].Journal.ApplyIncomingAsync([future], CancellationToken.None);
            await nodes[0].Journal.SetRegisterAsync("test.known", "\"ok\"", CancellationToken.None);

            // Le nœud plus ancien ne comprend pas l'événement, mais le conserve pour le relayer et applique les autres.
            await PullAsync(nodes[0], nodes[1], clusterId);
            await PullAsync(nodes[1], nodes[2], clusterId);

            Assert.Equal(2L, await EventCountAsync(nodes[2]));
            Assert.Contains("test.known=\"ok\"", await StateAsync(nodes[2]), StringComparison.Ordinal);
            var relayed = await nodes[2].Journal.GetEventsAfterAsync(clusterId, new Dictionary<Guid, long>(), ClusterReconciliationRepository.MaximumBatchSize, CancellationToken.None);
            Assert.Contains(relayed, change => change.EventId == future.EventId && change.EventType == future.EventType);
        }
        finally { await DisposeAllAsync(nodes); }
    }

    [Fact]
    public async Task MalformedOrForeignClusterEventsAreRefusedAndLeaveTheJournalUntouched()
    {
        var nodes = await NewMeshAsync(1);
        try
        {
            var clusterId = await ClusterIdAsync(nodes[0]);
            var origin = Guid.NewGuid();
            var good = new ClusterChangeEvent(Guid.NewGuid(), clusterId, origin, 1, "register.set", "{\"key\":\"test.k\",\"value\":\"\\\"v\\\"\"}", DateTimeOffset.UtcNow);
            var notJson = good with { EventId = Guid.NewGuid(), Sequence = 2, Payload = "{pas du json" };
            var foreign = good with { EventId = Guid.NewGuid(), Sequence = 3, ClusterId = Guid.NewGuid() };
            var noSequence = good with { EventId = Guid.NewGuid(), Sequence = 0 };

            await Assert.ThrowsAnyAsync<Exception>(() => nodes[0].Journal.ApplyIncomingAsync([good, notJson], CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => nodes[0].Journal.ApplyIncomingAsync([good, foreign], CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => nodes[0].Journal.ApplyIncomingAsync([noSequence], CancellationToken.None));

            // Un lot refusé est annulé en entier : même le bon événement du lot n'est pas appliqué.
            Assert.Equal(0L, await EventCountAsync(nodes[0]));
            Assert.Equal(string.Empty, await StateAsync(nodes[0]));
            await nodes[0].Journal.ApplyIncomingAsync([good], CancellationToken.None);
            Assert.Equal(1L, await EventCountAsync(nodes[0]));
        }
        finally { await DisposeAllAsync(nodes); }
    }
}
