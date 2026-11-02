using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using Encoding.Api.Executions;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Nodes;
using Encoding.Contracts.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>
/// Régressions de charge sur la base SQLite d'un nœud (verrou d'écriture unique) : une grosse file ne doit ni bloquer les heartbeats,
/// ni produire d'erreur « database is locked », ni faire échouer des exécutions saines quand un heartbeat est en retard.
/// Les bornes sont larges : elles détectent une régression, pas une performance précise.
/// </summary>
public sealed class LoadContentionTests
{
    private const string Token = "load-token";

    private sealed record Cluster(TestDatabase Database, Guid[] Nodes, Guid Library);

    private static async Task<Cluster> SeedAsync(string prefix, int nodeCount, int queued)
    {
        var database = await TestDatabase.CreateSqliteAsync(prefix);
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);
        var repository = new NodeRepository(database.Db);
        var nodes = new Guid[nodeCount];
        for (var i = 0; i < nodeCount; i++)
        {
            nodes[i] = Guid.NewGuid();
            Assert.NotNull(await repository.EnrollAsync(new(nodes[i], "node-" + i, [NodeRole.Server, NodeRole.Worker], 1, [], Token), true, CancellationToken.None));
        }
        var library = Guid.NewGuid();
        await using var connection = await database.Db.OpenConnectionAsync(CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);
        await Run(connection, transaction, "INSERT INTO libraries(id, name) VALUES (@id, 'load')", ("id", library.ToString("D")));
        await Run(connection, transaction, "INSERT INTO library_node_accesses(library_id, node_id, mode, local_path, can_read, can_write) VALUES (@l, @n, 'direct', '/media/load', 1, 1)",
            ("l", library.ToString("D")), ("n", nodes[0].ToString("D")));
        for (var i = 0; i < queued; i++)
        {
            var id = Guid.NewGuid().ToString("D");
            await Run(connection, transaction,
                "INSERT INTO executions(id, media, library_name, flow_name, flow_version, flow_definition, state, attempt) VALUES (@id, @media, 'load', 'flow', 1, '{}', 'queued', 1)",
                ("id", id), ("media", $"film-{i}.mkv"));
            await Run(connection, transaction, "INSERT INTO execution_work(execution_id, library_id, media_path, media_key) VALUES (@id, @l, @media, @key)",
                ("id", id), ("l", library.ToString("D")), ("media", $"film-{i}.mkv"), ("key", $"FILM-{i}.MKV"));
        }
        await transaction.CommitAsync(CancellationToken.None);
        return new(database, nodes, library);
    }

    private static async Task Run(DbConnection connection, DbTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    internal static async Task RunMixedLoadAsync(int queued, int nodeCount, TimeSpan duration, TimeSpan heartbeatBound, TimeSpan overviewBound)
    {
        var cluster = await SeedAsync("load-mixed", nodeCount, queued);
        await using var _ = cluster.Database;
        var db = cluster.Database.Db;
        var nodes = new NodeRepository(db);
        var executions = new ExecutionRepository(db);
        var recovery = new RuntimeRecoveryService(db, NullLogger<RuntimeRecoveryService>.Instance, presence: new NodePresenceOptions(TimeSpan.FromSeconds(30)));
        var failures = new ConcurrentQueue<string>();
        var slowestHeartbeat = 0L; var slowestOverview = 0L; var heartbeats = 0; var overviews = 0; var passes = 0;
        using var stop = new CancellationTokenSource(duration);

        void Max(ref long target, long value) { long seen; while (value > (seen = Interlocked.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen) { } }

        async Task Loop(string name, Func<Task> action, int pauseMilliseconds)
        {
            while (!stop.IsCancellationRequested)
            {
                try { await action(); }
                catch (Exception exception) when (!stop.IsCancellationRequested) { failures.Enqueue(name + ": " + exception.GetType().Name + " " + exception.Message.Split('\n')[0]); }
                try { await Task.Delay(pauseMilliseconds, stop.Token); } catch (OperationCanceledException) { break; }
            }
        }

        var work = new List<Task>();
        for (var i = 0; i < cluster.Nodes.Length; i++)
        {
            var node = cluster.Nodes[i];
            var request = new NodeHeartbeatRequest("node-" + i, [NodeRole.Server, NodeRole.Worker], 1, [], ProtocolVersion: ClusterProtocol.Current, SessionNonce: "session-" + i);
            work.Add(Loop("heartbeat", async () =>
            {
                var clock = Stopwatch.StartNew();
                Assert.NotNull(await nodes.RecordHeartbeatAsync(node, Token, request, CancellationToken.None));
                Max(ref slowestHeartbeat, clock.ElapsedMilliseconds); Interlocked.Increment(ref heartbeats);
            }, 50));
        }
        for (var i = 0; i < 3; i++)
            work.Add(Loop("overview", async () =>
            {
                var clock = Stopwatch.StartNew();
                var overview = await executions.OverviewAsync(CancellationToken.None);
                Assert.Equal(queued, overview.QueuedCount);
                Max(ref slowestOverview, clock.ElapsedMilliseconds); Interlocked.Increment(ref overviews);
            }, 20));
        work.Add(Loop("recovery", async () => { await recovery.ProcessOnceAsync(CancellationToken.None); Interlocked.Increment(ref passes); }, 100));
        await Task.WhenAll(work);

        Assert.True(failures.IsEmpty, "Erreurs sous charge : " + string.Join(" | ", failures.Distinct().Take(5)) + $" ({failures.Count} au total)");
        Assert.True(heartbeats >= nodeCount && overviews >= 3 && passes >= 1, $"La charge n'a pas progressé (heartbeats {heartbeats}, aperçus {overviews}, récupérations {passes}).");
        Assert.True(slowestHeartbeat <= heartbeatBound.TotalMilliseconds, $"Un heartbeat a attendu {slowestHeartbeat} ms (borne {heartbeatBound.TotalMilliseconds} ms).");
        Assert.True(slowestOverview <= overviewBound.TotalMilliseconds, $"Un aperçu des exécutions a pris {slowestOverview} ms (borne {overviewBound.TotalMilliseconds} ms).");
        Assert.Equal(queued, (int)await Scalar(db, "SELECT count(*) FROM executions WHERE state = 'queued'"));
    }

    [Fact]
    public Task HeartbeatsOverviewAndRecoveryShareALargeQueueWithoutLocksOrFailures() =>
        RunMixedLoadAsync(queued: 3000, nodeCount: 6, duration: TimeSpan.FromSeconds(8), heartbeatBound: TimeSpan.FromSeconds(5), overviewBound: TimeSpan.FromSeconds(10));

    [Fact]
    public async Task ADelayedHeartbeatDoesNotFailQueuedExecutionsButALostNodeDoes()
    {
        var cluster = await SeedAsync("load-delayed", 2, 40);
        await using var _ = cluster.Database;
        var db = cluster.Database.Db;
        var recovery = new RuntimeRecoveryService(db, NullLogger<RuntimeRecoveryService>.Instance, presence: new NodePresenceOptions(TimeSpan.FromSeconds(30)));

        // Le nœud qui détient l'accès direct ne s'est pas manifesté depuis 90 s (heartbeat retardé par un verrou ou un redémarrage).
        await Exec(db, "UPDATE cluster_nodes SET last_seen_at = strftime('%Y-%m-%d %H:%M:%f', 'now', '-90 seconds') || '000Z'");
        for (var i = 0; i < 3; i++) await recovery.ProcessOnceAsync(CancellationToken.None);
        Assert.Equal(40L, await Scalar(db, "SELECT count(*) FROM executions WHERE state = 'queued'"));
        Assert.Equal(0L, await Scalar(db, "SELECT count(*) FROM executions WHERE state = 'failed'"));

        // Un nœud réellement perdu (absent depuis 30 minutes) finit par faire échouer les exécutions qui dépendent de lui.
        await Exec(db, "UPDATE cluster_nodes SET last_seen_at = strftime('%Y-%m-%d %H:%M:%f', 'now', '-30 minutes') || '000Z'");
        for (var i = 0; i < 3; i++) await recovery.ProcessOnceAsync(CancellationToken.None);
        Assert.Equal(40L, await Scalar(db, "SELECT count(*) FROM executions WHERE state = 'failed'"));
    }

    [Fact]
    public async Task ASnapshotReadNeverBlocksAWriterWhileADefaultTransactionDoes()
    {
        await using var database = await TestDatabase.CreateSqliteAsync("load-snapshot");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);
        var writer = database.OpenAnother();

        await using (var reader = await database.Db.OpenConnectionAsync(CancellationToken.None))
        await using (var snapshot = await reader.BeginTransactionAsync(System.Data.IsolationLevel.Snapshot, CancellationToken.None))
        {
            await using (var read = reader.CreateCommand()) { read.Transaction = snapshot; read.CommandText = "SELECT count(*) FROM executions"; await read.ExecuteScalarAsync(CancellationToken.None); }
            var clock = Stopwatch.StartNew();
            await Exec(writer, "INSERT INTO libraries(id, name) VALUES ('" + Guid.NewGuid() + "', 'during-snapshot')");
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Une lecture cohérente a bloqué un écrivain pendant {clock.ElapsedMilliseconds} ms.");
        }

        // Une transaction ordinaire prend, elle, le verrou d'écriture (comportement voulu pour les écritures).
        await using var holder = await database.Db.OpenConnectionAsync(CancellationToken.None);
        await using var ordinary = await holder.BeginTransactionAsync(CancellationToken.None);
        await using (var read = holder.CreateCommand()) { read.Transaction = ordinary; read.CommandText = "SELECT count(*) FROM executions"; await read.ExecuteScalarAsync(CancellationToken.None); }
        await using var probe = new Microsoft.Data.Sqlite.SqliteConnection(database.ConnectionString);
        await probe.OpenAsync(CancellationToken.None);
        await using var busy = probe.CreateCommand();
        busy.CommandText = "BEGIN IMMEDIATE;";
        busy.CommandTimeout = 1;
        var exception = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => busy.ExecuteNonQueryAsync(CancellationToken.None));
        Assert.Equal(5, exception.SqliteErrorCode);
    }

    private static async Task Exec(DbDataSource db, string sql)
    {
        await using var command = db.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<long> Scalar(DbDataSource db, string sql)
    {
        await using var command = db.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None));
    }
}

/// <summary>
/// Charge lourde, exclue des vérifications de PR (<c>Category!=Stress</c>) et lancée chaque nuit par <c>stress.yml</c>.
/// Volumes réglables : ENCODING_STRESS_EXECUTIONS, ENCODING_STRESS_NODES, ENCODING_STRESS_SECONDS.
/// </summary>
[Trait("Category", "Stress")]
public sealed class LoadStressTests
{
    private static int Setting(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    [Fact]
    public Task LargeQueueWithManyNodesStaysResponsive() =>
        LoadContentionTests.RunMixedLoadAsync(
            queued: Setting("ENCODING_STRESS_EXECUTIONS", 20000), nodeCount: Setting("ENCODING_STRESS_NODES", 30),
            duration: TimeSpan.FromSeconds(Setting("ENCODING_STRESS_SECONDS", 60)), heartbeatBound: TimeSpan.FromSeconds(15), overviewBound: TimeSpan.FromSeconds(60));
}
