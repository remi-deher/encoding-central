using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Xunit;
using Xunit.Abstractions;

namespace Encoding.Runtime.Tests;

public sealed class RuntimeQueryPlanTests(ITestOutputHelper output)
{
    [Fact]
    public async Task QueueCursorSeeksInOrderWithoutATemporarySort()
    {
        await using var database = await CreateAsync();
        var plan = await PlanAsync(database, "SELECT id FROM executions WHERE (queued_at,id)>('2000-01-01 00:00:00.000000Z',@id) ORDER BY queued_at,id LIMIT 100");
        Assert.Contains(plan, line => line.Contains("SEARCH executions USING COVERING INDEX ix_executions_queue_order", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.Contains("TEMP B-TREE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DuplicateLookupSeeksBothLibraryAndMediaKey()
    {
        await using var database = await CreateAsync();
        const string sql = "SELECT EXISTS(SELECT 1 FROM execution_work w JOIN executions e ON e.id=w.execution_id WHERE w.library_id=@id AND w.media_key=@key AND e.state IN ('queued','running','cancelling'))";
        async Task<List<string>> Plan()
        {
            await using var command = database.Db.CreateCommand("EXPLAIN QUERY PLAN " + sql);
            command.Parameters.AddWithValue("id", Guid.Empty);
            command.Parameters.AddWithValue("key", "MISSING.MKV");
            await using var rows = await command.ExecuteReaderAsync();
            var plan = new List<string>();
            while (await rows.ReadAsync()) plan.Add(rows.GetString(3));
            return plan;
        }
        var after = await Plan();
        Assert.Contains(after, line => line.Contains("COVERING INDEX ix_execution_work_library_media (library_id=? AND media_key=?)", StringComparison.Ordinal));
        await using (var drop = database.Db.CreateCommand("DROP INDEX ix_execution_work_library_media")) await drop.ExecuteNonQueryAsync();
        var before = await Plan();
        Assert.DoesNotContain(before, line => line.Contains("media_key=?", StringComparison.Ordinal));
        output.WriteLine("Before: " + string.Join("; ", before));
        output.WriteLine("After: " + string.Join("; ", after));
    }

    [Theory]
    [InlineData("worker_node_id", "ix_execution_work_worker_lease")]
    [InlineData("library_id", "ix_execution_work_library_lease")]
    public async Task LoadQueriesUseACoveringIndexInsteadOfScanningAllWork(string column, string index)
    {
        await using var database = await CreateAsync();
        var sql = $"SELECT count(*) FROM execution_work w JOIN executions e ON e.id=w.execution_id WHERE w.{column}=@id AND w.lease_until>clock_timestamp() AND e.state IN ('running','cancelling')";
        var after = await PlanAsync(database, sql);
        Assert.Contains(after, line => line.Contains("COVERING INDEX " + index, StringComparison.Ordinal));
        Assert.DoesNotContain(after, line => line.Contains("SCAN w", StringComparison.Ordinal));
        await using (var drop = database.Db.CreateCommand("DROP INDEX " + index)) await drop.ExecuteNonQueryAsync();
        var before = await PlanAsync(database, sql);
        // Other runtime indexes can still serve a less selective fallback (including
        // skip-scans after ANALYZE); only the indexed plan above is the requirement.
        output.WriteLine("Before: " + string.Join("; ", before));
        output.WriteLine("After: " + string.Join("; ", after));
    }

    [Fact]
    public async Task LatestCheckpointLookupDoesNotSortAllSteps()
    {
        await using var database = await CreateAsync();
        const string sql = "SELECT checkpoint FROM execution_steps WHERE execution_id=@id AND completed_at IS NOT NULL ORDER BY completed_revision DESC LIMIT 1";
        var after = await PlanAsync(database, sql);
        Assert.Contains(after, line => line.Contains("ix_execution_steps_latest_checkpoint", StringComparison.Ordinal));
        Assert.DoesNotContain(after, line => line.Contains("TEMP B-TREE", StringComparison.Ordinal));
        await using (var drop = database.Db.CreateCommand("DROP INDEX ix_execution_steps_latest_checkpoint")) await drop.ExecuteNonQueryAsync();
        var before = await PlanAsync(database, sql);
        Assert.Contains(before, line => line.Contains("TEMP B-TREE", StringComparison.Ordinal));
        output.WriteLine("Before: " + string.Join("; ", before));
        output.WriteLine("After: " + string.Join("; ", after));
    }

    private static async Task<TestDatabase> CreateAsync()
    {
        var database = await TestDatabase.CreateSqliteAsync("query-plans");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);
        // Give the planner representative cardinalities: on empty tables two indexes with
        // the same first column can be chosen arbitrarily, regardless of the range predicate.
        await using var seed = database.Db.CreateCommand("""
            INSERT INTO libraries(id,name) VALUES('library-1','One'),('library-2','Two');
            WITH RECURSIVE numbers(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM numbers WHERE n<2000)
            INSERT INTO executions(id,media,library_name,flow_name,flow_version,flow_definition,state,attempt)
                SELECT 'execution-'||n,'file-'||n||'.mkv','Fixture','Fixture',1,'{}','running',1 FROM numbers;
            INSERT INTO execution_work(execution_id,library_id,media_path,media_key,lease_until)
                SELECT id,CASE WHEN rowid%2=0 THEN 'library-1' ELSE 'library-2' END,media,upper(media),
                    CASE WHEN rowid%100=0 THEN '2100-01-01 00:00:00.000000Z' ELSE '2000-01-01 00:00:00.000000Z' END
                FROM executions;
            ANALYZE;
            """);
        await seed.ExecuteNonQueryAsync();
        return database;
    }

    private static async Task<List<string>> PlanAsync(TestDatabase database, string sql)
    {
        await using var command = database.Db.CreateCommand("EXPLAIN QUERY PLAN " + sql);
        command.Parameters.AddWithValue("id", Guid.Empty);
        await using var rows = await command.ExecuteReaderAsync();
        var plan = new List<string>();
        while (await rows.ReadAsync()) plan.Add(rows.GetString(3));
        return plan;
    }
}
