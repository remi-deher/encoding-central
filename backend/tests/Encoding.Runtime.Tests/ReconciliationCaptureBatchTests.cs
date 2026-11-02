using System.Text.Json;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class ReconciliationCaptureBatchTests
{
    [Fact]
    public async Task MediaInventoryResumesWhenItsScanWasAlreadyCommittedBeforeCancellation()
    {
        await using var database = await CreateAsync();
        using var keys = await KeysAsync(database);
        var scanId = Guid.NewGuid();
        await using (var library = database.Db.CreateCommand("SELECT id FROM libraries LIMIT 1"))
        {
            var libraryId = await library.ExecuteScalarAsync();
            await using var connection = await database.Db.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var scan = Sql.Command("INSERT INTO library_scans(id,library_id,media_type,state,completed_at) VALUES(@id,@library,'video','completed',clock_timestamp())", connection, transaction))
            {
                scan.Parameters.AddWithValue("id", scanId);
                scan.Parameters.AddWithValue("library", libraryId);
                await scan.ExecuteNonQueryAsync();
            }
            for (var index = 0; index < 200; index++)
            {
                await using var media = Sql.Command("INSERT INTO library_media(id,library_id,relative_path,byte_length,modified_at,scan_id) VALUES(@id,@library,@path,100,clock_timestamp(),@scan)", connection, transaction);
                media.Parameters.AddWithValue("id", Guid.NewGuid());
                media.Parameters.AddWithValue("library", libraryId);
                media.Parameters.AddWithValue("path", index + ".mkv");
                media.Parameters.AddWithValue("scan", scanId);
                await media.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }
        using var cancellation = new CancellationTokenSource();
        var logger = new BatchLogger(count =>
        {
            if (count > 0 && CountAsync(database, "SELECT count(*) FROM cluster_sync_entities WHERE entity_type='library_scans'").GetAwaiter().GetResult() > 0)
                cancellation.Cancel();
        });
        var capture = new ClusterEntityReconciliation(database.Db, keys, new EphemeralDataProtectionProvider(), logger);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.CaptureLocalChangesAsync(cancellation.Token));
        Assert.InRange(await CountAsync(database, "SELECT count(*) FROM cluster_sync_entities WHERE entity_type='library_media'"), 1, 199);
        var resumed = new ClusterEntityReconciliation(database.Db, keys, new EphemeralDataProtectionProvider(), new BatchLogger(_ => { }));
        await resumed.CaptureLocalChangesAsync(CancellationToken.None);
        Assert.Equal(200, await CountAsync(database, "SELECT count(*) FROM cluster_sync_entities WHERE entity_type='library_media'"));
        Assert.Equal(0, await resumed.CaptureLocalChangesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CancellationAfterACommittedBatchResumesWithoutLostOrDuplicateEvents()
    {
        await using var database = await CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var batches = new List<int>();
        var logger = new BatchLogger(count => { batches.Add(count); if (count > 0) cancellation.Cancel(); });
        using var keys = await KeysAsync(database);
        var capture = new ClusterEntityReconciliation(database.Db, keys, new EphemeralDataProtectionProvider(), logger);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.CaptureLocalChangesAsync(cancellation.Token));
        Assert.Equal(ClusterEntityReconciliation.MaximumCaptureBatchSize, await CountAsync(database, "SELECT count(*) FROM cluster_change_log"));
        var resumed = new ClusterEntityReconciliation(database.Db, keys, new EphemeralDataProtectionProvider(), new BatchLogger(batches.Add));
        Assert.True(await resumed.CaptureLocalChangesAsync(CancellationToken.None) > 0);
        Assert.Equal(0, await resumed.CaptureLocalChangesAsync(CancellationToken.None));
        Assert.All(batches, count => Assert.InRange(count, 0, ClusterEntityReconciliation.MaximumCaptureBatchSize));
        Assert.Equal(await CountAsync(database, "SELECT count(*) FROM cluster_sync_entities"), await CountAsync(database, "SELECT count(*) FROM cluster_change_log"));
        Assert.Equal(await CountAsync(database, "SELECT count(*) FROM cluster_change_log"), await CountAsync(database, "SELECT max(sequence) FROM cluster_change_log"));
    }

    [Fact]
    public async Task AnotherWriterCanAllocateASequenceAndChangeARowBetweenBatches()
    {
        await using var database = await CreateAsync();
        using var keys = await KeysAsync(database);
        string? changedId = null;
        var interrupted = false;
        var logger = new BatchLogger(count =>
        {
            if (count == 0 || interrupted) return;
            interrupted = true;
            using var read = database.Db.CreateCommand("SELECT id FROM libraries WHERE id NOT IN(SELECT json_extract(entity_key,'$.id') FROM cluster_sync_entities WHERE entity_type='libraries') LIMIT 1");
            changedId = (string)read.ExecuteScalar()!;
            using var update = database.Db.CreateCommand("UPDATE libraries SET name='concurrent-value' WHERE id=@id");
            update.Parameters.AddWithValue("id", changedId);
            update.ExecuteNonQuery();
            new ClusterReconciliationRepository(database.Db).SetRegisterAsync("between-batches", "true", CancellationToken.None).GetAwaiter().GetResult();
        });
        var capture = new ClusterEntityReconciliation(database.Db, keys, new EphemeralDataProtectionProvider(), logger);
        await capture.CaptureLocalChangesAsync(CancellationToken.None);
        Assert.NotNull(changedId);
        await capture.CaptureLocalChangesAsync(CancellationToken.None);
        await using var events = database.Db.CreateCommand("SELECT payload FROM cluster_change_log WHERE event_type='entity.set' AND json_extract(payload,'$.table')='libraries' AND json_extract(payload,'$.row.id')=@id");
        events.Parameters.AddWithValue("id", changedId);
        await using var rows = await events.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        using var payload = JsonDocument.Parse(rows.GetString(0));
        Assert.Equal("concurrent-value", payload.RootElement.GetProperty("row").GetProperty("name").GetString());
        Assert.False(await rows.ReadAsync());
        Assert.Equal(await CountAsync(database, "SELECT count(*) FROM cluster_change_log"), await CountAsync(database, "SELECT max(sequence) FROM cluster_change_log"));
    }

    private static async Task<TestDatabase> CreateAsync()
    {
        var database = await TestDatabase.CreateSqliteAsync("capture-batches");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);
        await new ClusterIdentityRepository(database.Db).AdoptAsync(Guid.NewGuid(), "batch-tests", CancellationToken.None);
        await using var connection = await database.Db.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        for (var index = 0; index < 160; index++)
        {
            await using var insert = Sql.Command("INSERT INTO libraries(id,name) VALUES(@id,@name)", connection, transaction);
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("name", "library-" + index);
            await insert.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
        return database;
    }

    private static async Task<ClusterDataKeyStore> KeysAsync(TestDatabase database)
    {
        var identity = await new ClusterIdentityRepository(database.Db).GetAsync(CancellationToken.None);
        return new(identity.Id, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    }

    private static async Task<long> CountAsync(TestDatabase database, string sql)
    {
        await using var command = database.Db.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private sealed class BatchLogger(Action<int> committed) : ILogger<ClusterEntityReconciliation>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Name == "CaptureBatchCommitted" && state is IEnumerable<KeyValuePair<string, object?>> values)
                committed(Convert.ToInt32(values.Single(value => value.Key == "Count").Value));
        }
    }
}
