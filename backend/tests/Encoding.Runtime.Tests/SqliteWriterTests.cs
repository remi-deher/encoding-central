using System.Data;
using System.Data.Common;
using System.Net;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data.Sqlite;
using Encoding.Api.Infrastructure.Operations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class SqliteWriterTests
{
    [Fact]
    public async Task PriorityHasReservedCapacityAndOrdinaryWorkCannotStarve()
    {
        var writer = new SqliteWriter(new() { Capacity = 1, UrgentCapacity = 16 });
        using var active = await writer.EnterAsync();
        var ordinary = writer.EnterAsync().AsTask();
        await Assert.ThrowsAsync<SqliteWriteUnavailableException>(() => writer.EnterAsync().AsTask());
        var urgent = new List<Task<IDisposable>>();
        using (SqliteWriter.Prioritize())
            for (var index = 0; index < 9; index++) urgent.Add(writer.EnterAsync().AsTask());
        Assert.Equal(1, writer.Snapshot().Pending);
        Assert.Equal(9, writer.Snapshot().UrgentPending);
        active.Dispose();
        for (var index = 0; index < 8; index++)
        {
            using var lease = await urgent[index].WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(ordinary.IsCompleted);
        }
        using (await ordinary.WaitAsync(TimeSpan.FromSeconds(5))) Assert.False(urgent[8].IsCompleted);
        using (await urgent[8].WaitAsync(TimeSpan.FromSeconds(5))) { }
        Assert.False(writer.Snapshot().Active);
        Assert.Equal(1, writer.Snapshot().Rejected);
    }

    [Fact]
    public async Task ExpiredAndCancelledWaitersNeverAcquireAndActiveCancellationDoesNotRelease()
    {
        var writer = new SqliteWriter(new() { Capacity = 1, WaitTimeout = TimeSpan.FromMilliseconds(50) });
        using var cancellation = new CancellationTokenSource();
        using var active = await writer.EnterAsync(cancellation.Token);
        cancellation.Cancel();
        Assert.True(writer.Snapshot().Active);
        using var waitingCancellation = new CancellationTokenSource();
        var cancelled = writer.EnterAsync(waitingCancellation.Token).AsTask();
        waitingCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        await Assert.ThrowsAsync<SqliteWriteUnavailableException>(() => writer.EnterAsync().AsTask());
        Assert.Equal(0, writer.Snapshot().Pending);
        Assert.Equal(1, writer.Snapshot().Expired);
        active.Dispose();
        using (await writer.EnterAsync()) { }
        Assert.False(writer.Snapshot().Active);
    }

    [Fact]
    public async Task SnapshotReadsCommittedStateDuringWriteAndCannotUpgradeToWriter()
    {
        await using var database = await CreateAsync();
        await using var writer = await database.Db.OpenConnectionAsync();
        await using var tx = await writer.BeginTransactionAsync();
        await ExecuteAsync(writer, "UPDATE sample SET value=1", tx);
        await using var reader = await database.Db.OpenConnectionAsync();
        await using (var snapshot = await reader.BeginTransactionAsync(IsolationLevel.Snapshot))
        {
            await using var read = Sql.Command("SELECT value FROM sample", reader, snapshot);
            Assert.Equal(0L, await read.ExecuteScalarAsync());
            await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(reader, "WITH x AS (SELECT 2) UPDATE sample SET value=2", snapshot));
            await tx.CommitAsync();
            Assert.Equal(0L, await read.ExecuteScalarAsync());
            await snapshot.CommitAsync();
        }
        // query_only must be reset before this connection is reused for a writer.
        await ExecuteAsync(reader, "UPDATE sample SET value=3");
        Assert.False(((SqliteDataSource)database.Db).Writer.Snapshot().Active);
    }

    [Fact]
    public async Task ReturningReaderKeepsWriterUntilDisposedAndCancellationRemovesQueuedWrite()
    {
        await using var database = await CreateAsync();
        var source = (SqliteDataSource)database.Db;
        await using var connection = await source.OpenConnectionAsync();
        await using var update = Sql.Command("UPDATE sample SET value=1 RETURNING value", connection);
        await using var rows = await update.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        using var cancelled = new CancellationTokenSource();
        await using var other = source.CreateCommand("UPDATE sample SET value=2");
        var pending = other.ExecuteNonQueryAsync(cancelled.Token);
        Assert.Equal(1, source.Writer.Snapshot().Pending);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(source.Writer.Snapshot().Active);
        await rows.DisposeAsync();
        await using var check = source.CreateCommand("SELECT value FROM sample");
        Assert.Equal(1L, await check.ExecuteScalarAsync());
        Assert.False(source.Writer.Snapshot().Active);
    }

    [Fact]
    public async Task FailedTransactionRollsBackBeforeNextIndependentWriterAndCloseReleasesAdmission()
    {
        await using var database = await CreateAsync();
        await using var connection = await database.Db.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, "UPDATE sample SET value=1", tx);
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "INSERT INTO sample VALUES (1)", tx));
        await using var next = database.Db.OpenConnection();
        var pending = next.BeginTransactionAsync().AsTask();
        Assert.False(pending.IsCompleted);
        await connection.CloseAsync();
        await using var nextTx = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        await using var read = Sql.Command("SELECT value FROM sample", next, nextTx);
        Assert.Equal(0L, await read.ExecuteScalarAsync());
        await ExecuteAsync(next, "UPDATE sample SET value=2", nextTx);
        await nextTx.CommitAsync();
        Assert.False(((SqliteDataSource)database.Db).Writer.Snapshot().Active);
    }

    [Fact]
    public async Task ConcurrentUnitsAreAtomicAndNoUpdatesAreLost()
    {
        await using var database = await CreateAsync();
        var source = (SqliteDataSource)database.Db;
        var jobs = Enumerable.Range(0, 64).Select(async index =>
        {
            await using var connection = await source.OpenConnectionAsync();
            using var priority = index % 4 == 0 ? SqliteWriter.Prioritize() : null;
            await using var tx = await connection.BeginTransactionAsync();
            await using var read = Sql.Command("SELECT value FROM sample", connection, tx);
            var value = (long)(await read.ExecuteScalarAsync())!;
            await Task.Yield();
            await using var write = Sql.Command("UPDATE sample SET value=@value", connection, tx);
            write.Parameters.AddWithValue("value", value + 1);
            await write.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        });
        await Task.WhenAll(jobs).WaitAsync(TimeSpan.FromSeconds(15));
        await using var check = source.CreateCommand("SELECT value FROM sample");
        Assert.Equal(64L, await check.ExecuteScalarAsync());
        var metrics = source.Writer.Snapshot();
        Assert.False(metrics.Active);
        Assert.Equal(0, metrics.Pending + metrics.UrgentPending);
        Assert.Equal(0, metrics.Retries);
    }

    [Fact]
    public async Task ClosingConnectionWithReturningReaderReleasesAdmission()
    {
        await using var database = await CreateAsync();
        var source = (SqliteDataSource)database.Db;
        await using var connection = await source.OpenConnectionAsync();
        await using var command = Sql.Command("UPDATE sample SET value=1 RETURNING value", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(source.Writer.Snapshot().Active);
        await connection.CloseAsync();
        Assert.True(reader.IsClosed);
        Assert.False(source.Writer.Snapshot().Active);
        await using var next = source.CreateCommand("UPDATE sample SET value=2");
        Assert.Equal(1, await next.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task BeginRetriesExternalLockBeforeExecutingBusinessWork()
    {
        await using var database = await CreateAsync();
        var source = (SqliteDataSource)database.Db;
        await using var external = new SqliteConnection(database.ConnectionString);
        await external.OpenAsync();
        await using var held = external.BeginTransaction();
        var executed = 0;
        var waiting = Task.Run(async () =>
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var tx = await connection.BeginTransactionAsync();
            Interlocked.Increment(ref executed);
            await ExecuteAsync(connection, "UPDATE sample SET value=1", tx);
            await tx.CommitAsync();
        });
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (source.Writer.Snapshot().Retries == 0)
                await Task.Delay(10, deadline.Token);
            Assert.Equal(0, Volatile.Read(ref executed));
        }
        finally { await held.RollbackAsync(); }
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, executed);
        Assert.InRange(source.Writer.Snapshot().Retries, 1, 2);
    }

    [Fact]
    public async Task MeasurementModeObservesUnitsWithoutSerializingThem()
    {
        var writer = new SqliteWriter(new() { Enabled = false });
        using var first = await writer.EnterAsync();
        using var second = await writer.EnterAsync();
        Assert.True(writer.Snapshot().Active);
        Assert.Equal(2, writer.Snapshot().Admitted);
        Assert.Equal(0, writer.Snapshot().Pending);
        first.Dispose();
        Assert.True(writer.Snapshot().Active);
        second.Dispose();
        Assert.False(writer.Snapshot().Active);
    }

    [Fact]
    public async Task CriticalRolloutQueuesOnlyUrgentUnitsAndMeasuresOtherWriters()
    {
        var writer = new SqliteWriter(new() { CriticalOnly = true });
        using var priority = SqliteWriter.Prioritize();
        using var active = await writer.EnterAsync();
        var queued = writer.EnterAsync().AsTask();
        Assert.False(queued.IsCompleted);
        active.Dispose();
        using var admitted = await queued;
        Assert.Equal(0, writer.Snapshot().Pending);
        priority.Dispose();
        using var ordinary = await writer.EnterAsync();
        Assert.Equal(3, writer.Snapshot().Admitted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueueAndSqliteBusyFailuresReturn503WithRetryAfterWithoutSql(bool busy)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddApiProblemDetails();
        await using var app = builder.Build();
        app.UseRequestObservability();
        app.MapGet("/write", () => Task.FromException<IResult>(busy
            ? new SqliteException("sensitive SQL", 5)
            : new SqliteWriteUnavailableException("file saturée")));
        await app.StartAsync();
        using var client = app.GetTestClient();
        var response = await client.GetAsync("/write");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("sqlite_write_unavailable", body, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive SQL", body, StringComparison.Ordinal);
    }

    private static async Task<TestDatabase> CreateAsync()
    {
        var database = await TestDatabase.CreateSqliteAsync("writer");
        await using var create = database.Db.CreateCommand("CREATE TABLE sample(value INTEGER UNIQUE); INSERT INTO sample VALUES(0)");
        await create.ExecuteNonQueryAsync();
        return database;
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql, DbTransaction? tx = null)
    {
        await using var command = Sql.Command(sql, connection, tx);
        await command.ExecuteNonQueryAsync();
    }
}
