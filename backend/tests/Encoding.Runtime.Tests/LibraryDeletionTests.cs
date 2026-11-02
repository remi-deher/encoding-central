using System.Data.Common;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Libraries;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class LibraryDeletionTests
{
    [DatabaseFact]
    public async Task DeletingALibraryKeepsItsExecutionHistoryAndRefusesActiveWork()
    {
        await using var database = await TestDatabase.CreateAsync(RuntimeFixture.ConnectionString!, "library_delete");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);
        var repository = new LibraryRepository(database.Db);
        var libraryId = Guid.NewGuid();
        var finished = Guid.NewGuid();
        var queued = Guid.NewGuid();
        await Exec(database.Db, $"INSERT INTO libraries(id,name) VALUES('{libraryId}','Test')");
        await Exec(database.Db, $"INSERT INTO executions(id,media,library_name,flow_name,flow_version,flow_definition,state,attempt) VALUES('{finished}','a.mkv','Test','Flow',1,'{{}}','succeeded',1)");
        await Exec(database.Db, $"INSERT INTO execution_work(execution_id,library_id,media_path,media_key) VALUES('{finished}','{libraryId}','a.mkv','A.MKV')");
        await Exec(database.Db, $"INSERT INTO executions(id,media,library_name,flow_name,flow_version,flow_definition,state,attempt) VALUES('{queued}','b.mkv','Test','Flow',1,'{{}}','queued',1)");
        await Exec(database.Db, $"INSERT INTO execution_work(execution_id,library_id,media_path,media_key) VALUES('{queued}','{libraryId}','b.mkv','B.MKV')");

        await Assert.ThrowsAsync<LibraryInUseException>(() => repository.DeleteAsync(libraryId, CancellationToken.None));
        Assert.Equal(1, await Scalar(database.Db, $"SELECT count(*) FROM libraries WHERE id='{libraryId}'"));

        await Exec(database.Db, $"UPDATE executions SET state='cancelled' WHERE id='{queued}'");
        Assert.True(await repository.DeleteAsync(libraryId, CancellationToken.None));
        Assert.Equal(0, await Scalar(database.Db, $"SELECT count(*) FROM libraries WHERE id='{libraryId}'"));
        Assert.Equal(0, await Scalar(database.Db, $"SELECT count(*) FROM execution_work WHERE library_id='{libraryId}'"));
        Assert.Equal(2, await Scalar(database.Db, "SELECT count(*) FROM executions"));
        Assert.False(await repository.DeleteAsync(libraryId, CancellationToken.None));
    }

    private static async Task Exec(DbDataSource db, string sql) { await using var command = db.CreateCommand(sql); await command.ExecuteNonQueryAsync(); }
    private static async Task<long> Scalar(DbDataSource db, string sql) { await using var command = db.CreateCommand(sql); return Convert.ToInt64(await command.ExecuteScalarAsync()); }
}
