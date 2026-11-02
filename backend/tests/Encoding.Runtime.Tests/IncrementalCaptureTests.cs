using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Infrastructure.Data.Sqlite;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Encoding.Runtime.Tests;

public sealed class IncrementalCaptureTests(ITestOutputHelper output)
{
    [Fact]
    public async Task InventoryInvalidatedAfterSnapshotIsStillCapturedOnTheNextPass()
    {
        await using var fixture = await Fixture.CreateAsync(1);
        var scanId = Guid.NewGuid();
        await using (var scan = fixture.Database.Db.CreateCommand("INSERT INTO library_scans(id,library_id,media_type,state,completed_at) SELECT @id,id,'video','completed',clock_timestamp() FROM libraries"))
        {
            scan.Parameters.AddWithValue("id", scanId);
            await scan.ExecuteNonQueryAsync();
        }
        await fixture.Capture.CaptureLocalChangesAsync(CancellationToken.None);
        await fixture.ExecuteAsync("UPDATE libraries SET name='prepare-another-pass'");
        var inserted = false;
        fixture.Logger.OnRead = () =>
        {
            if (inserted) return;
            inserted = true;
            using var media = fixture.Database.Db.CreateCommand("INSERT INTO library_media(id,library_id,relative_path,byte_length,modified_at,scan_id) SELECT @id,id,'été.mkv',10,clock_timestamp(),@scan FROM libraries");
            media.Parameters.AddWithValue("id", Guid.NewGuid());
            media.Parameters.AddWithValue("scan", scanId);
            media.ExecuteNonQuery();
        };
        await fixture.Capture.CaptureLocalChangesAsync(CancellationToken.None);
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM cluster_sync_entities WHERE entity_type='library_media'"));
        Assert.Equal(1, await fixture.Capture.CaptureLocalChangesAsync(CancellationToken.None));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM cluster_sync_entities WHERE entity_type='library_media' AND NOT is_deleted"));
        Assert.Equal(0, await fixture.Capture.CaptureLocalChangesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task IdleCaptureReadsNoBusinessRowsAndOneChangeReadsOnlyOneRow()
    {
        await using var fixture = await Fixture.CreateAsync(1000);
        await fixture.Capture.CaptureLocalChangesAsync(CancellationToken.None);
        var admitted = ((SqliteDataSource)fixture.Database.Db).Writer.Snapshot().Admitted;
        Assert.Equal(0, await fixture.Capture.CaptureLocalChangesAsync(CancellationToken.None));
        Assert.Equal((0, 0), fixture.Logger.LastRead);
        Assert.Equal(admitted, ((SqliteDataSource)fixture.Database.Db).Writer.Snapshot().Admitted);
        await fixture.ExecuteAsync("UPDATE libraries SET name='changed' WHERE id=(SELECT id FROM libraries LIMIT 1)");
        Assert.Equal(1, await fixture.Capture.CaptureLocalChangesAsync(CancellationToken.None));
        Assert.Equal((1, 1), fixture.Logger.LastRead);
        output.WriteLine("1000 libraries: idle capture prepares 0 rows; one changed library prepares 1 row.");
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM cluster_sync_dirty_entities"));
    }

    [Fact]
    public async Task ChangesAfterTheReadSnapshotRemainDirtyUntilTheNextCapture()
    {
        await using var fixture = await Fixture.CreateAsync(1);
        await fixture.Capture.CaptureLocalChangesAsync(CancellationToken.None);
        await fixture.ExecuteAsync("UPDATE libraries SET name='intermediate'");
        var interleaved = false;
        fixture.Logger.OnRead = () =>
        {
            if (interleaved) return;
            interleaved = true;
            fixture.ExecuteAsync("UPDATE libraries SET name='latest'").GetAwaiter().GetResult();
        };
        Assert.Equal(0, await fixture.Capture.CaptureLocalChangesAsync(CancellationToken.None));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM cluster_sync_dirty_entities WHERE entity_type='libraries'"));
        Assert.Equal(1, await fixture.Capture.CaptureLocalChangesAsync(CancellationToken.None));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM cluster_change_log WHERE json_extract(payload,'$.row.name')='intermediate'"));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM cluster_change_log WHERE json_extract(payload,'$.row.name')='latest'"));
    }

    [Fact]
    public async Task RolledBackChangesDoNotBecomeDirtyAndDeletingTheLastRowProducesATombstone()
    {
        await using var fixture = await Fixture.CreateAsync(1);
        await fixture.Capture.CaptureLocalChangesAsync(CancellationToken.None);
        await using (var connection = await fixture.Database.Db.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var update = Sql.Command("UPDATE libraries SET name='rolled-back'", connection, transaction);
            await update.ExecuteNonQueryAsync();
            await transaction.RollbackAsync();
        }
        Assert.Equal(0, await fixture.Capture.CaptureLocalChangesAsync(CancellationToken.None));
        await fixture.ExecuteAsync("DELETE FROM libraries");
        Assert.Equal(1, await fixture.Capture.CaptureLocalChangesAsync(CancellationToken.None));
        Assert.Equal((1, 0), fixture.Logger.LastRead);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM cluster_sync_entities WHERE entity_type='libraries' AND is_deleted"));
    }

    [Fact]
    public async Task RenamingAPrimaryKeyCapturesTheNewKeyAndTombstonesTheOldKey()
    {
        await using var fixture = await Fixture.CreateAsync(1);
        await fixture.Capture.CaptureLocalChangesAsync(CancellationToken.None);
        await fixture.ExecuteAsync("UPDATE libraries SET id='00000000-0000-0000-0000-000000000001'");
        Assert.Equal(2, await fixture.Capture.CaptureLocalChangesAsync(CancellationToken.None));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM cluster_sync_entities WHERE entity_type='libraries' AND is_deleted"));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM cluster_sync_entities WHERE entity_type='libraries' AND NOT is_deleted"));
    }

    [Fact]
    public async Task NonAsciiKeysUseTheSameCanonicalFormAsExistingEntityStates()
    {
        await using var fixture = await Fixture.CreateAsync(0);
        var protector = fixture.Protection.CreateProtector("Encoding.PluginConfiguration.v1");
        await using (var insert = fixture.Database.Db.CreateCommand("INSERT INTO plugin_configurations(plugin_id,revision,encrypted_values) VALUES('extension-é',1,@values)"))
        {
            insert.Parameters.AddWithValue("values", protector.Protect("{}"u8.ToArray()));
            await insert.ExecuteNonQueryAsync();
        }
        await fixture.Capture.CaptureLocalChangesAsync(CancellationToken.None);
        await fixture.ExecuteAsync("UPDATE plugin_configurations SET revision=2 WHERE plugin_id='extension-é'");
        Assert.Equal(1, await fixture.Capture.CaptureLocalChangesAsync(CancellationToken.None));
        await fixture.ExecuteAsync("DELETE FROM plugin_configurations WHERE plugin_id='extension-é'");
        Assert.Equal(1, await fixture.Capture.CaptureLocalChangesAsync(CancellationToken.None));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM cluster_sync_entities WHERE entity_type='plugin_configurations' AND is_deleted"));
    }

    [Fact]
    public async Task EveryDeclaredTableHasInsertUpdateDeleteTrackingAndLocalBookkeepingIsLocal()
    {
        await using var fixture = await Fixture.CreateAsync(0);
        foreach (var table in ClusterEntityReconciliation.CaptureTableNames)
        {
            await using var command = fixture.Database.Db.CreateCommand("SELECT count(*) FROM sqlite_master WHERE type='trigger' AND name IN(@insert,@update,@delete)");
            command.Parameters.AddWithValue("insert", "capture_" + table + "_insert");
            command.Parameters.AddWithValue("update", "capture_" + table + "_update");
            command.Parameters.AddWithValue("delete", "capture_" + table + "_delete");
            Assert.Equal(3L, await command.ExecuteScalarAsync());
            await using var trigger = fixture.Database.Db.CreateCommand("SELECT sql FROM sqlite_master WHERE name=@name AND type='trigger'");
            trigger.Parameters.AddWithValue("name", "capture_" + table + "_update");
            var definition = (string)(await trigger.ExecuteScalarAsync())!;
            var from = definition.IndexOf("UPDATE OF ", StringComparison.Ordinal) + "UPDATE OF ".Length;
            var to = definition.IndexOf(" ON ", from, StringComparison.Ordinal);
            var columns = definition[from..to].Split(',').Select(column => column.Trim()).Order(StringComparer.Ordinal);
            Assert.Equal(ClusterEntityReconciliation.CaptureTableColumns[table].Order(StringComparer.Ordinal), columns);
            foreach (var column in ClusterEntityReconciliation.CaptureTableColumns[table])
                Assert.Contains("OLD." + column + " IS NOT NEW." + column, definition, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AdoptingAnotherClusterForcesANewCaptureOfUnchangedLocalRows()
    {
        await using var fixture = await Fixture.CreateAsync(1);
        await fixture.Capture.CaptureLocalChangesAsync(CancellationToken.None);
        var nextId = Guid.NewGuid();
        await new ClusterIdentityRepository(fixture.Database.Db).AdoptAsync(nextId, "new-cluster", CancellationToken.None);
        using var keys = new ClusterDataKeyStore(nextId, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var capture = new ClusterEntityReconciliation(fixture.Database.Db, keys, fixture.Protection, fixture.Logger);
        Assert.True(await capture.CaptureLocalChangesAsync(CancellationToken.None) > 0);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM cluster_sync_entities WHERE entity_type='libraries' AND NOT is_deleted"));
    }

    private sealed class Fixture(TestDatabase database, ClusterDataKeyStore keys, EphemeralDataProtectionProvider protection) : IAsyncDisposable
    {
        public TestDatabase Database => database;
        public EphemeralDataProtectionProvider Protection => protection;
        public ReadLogger Logger { get; } = new();
        public ClusterEntityReconciliation Capture => new(database.Db, keys, protection, Logger);

        public static async Task<Fixture> CreateAsync(int libraries)
        {
            var database = await TestDatabase.CreateSqliteAsync("incremental-capture");
            await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);
            var id = Guid.NewGuid();
            await new ClusterIdentityRepository(database.Db).AdoptAsync(id, "incremental-tests", CancellationToken.None);
            await using var connection = await database.Db.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            for (var index = 0; index < libraries; index++)
            {
                await using var insert = Sql.Command("INSERT INTO libraries(id,name) VALUES(@id,@name)", connection, transaction);
                insert.Parameters.AddWithValue("id", Guid.NewGuid());
                insert.Parameters.AddWithValue("name", "library-" + index);
                await insert.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
            return new(database, new(id, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)), new());
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var command = database.Db.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<long> CountAsync(string sql)
        {
            await using var command = database.Db.CreateCommand(sql);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        public async ValueTask DisposeAsync() { keys.Dispose(); await database.DisposeAsync(); }
    }

    private sealed class ReadLogger : ILogger<ClusterEntityReconciliation>
    {
        public (int Tables, int Rows) LastRead { get; private set; }
        public Action? OnRead { get; set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Name != "CaptureSelectionRead" || state is not IEnumerable<KeyValuePair<string, object?>> values) return;
            var fields = values.ToDictionary();
            LastRead = (Convert.ToInt32(fields["Tables"]), Convert.ToInt32(fields["Rows"]));
            OnRead?.Invoke();
        }
    }
}
