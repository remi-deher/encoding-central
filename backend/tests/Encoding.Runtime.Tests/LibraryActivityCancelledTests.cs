using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Libraries;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class LibraryActivityCancelledTests
{
    [Fact]
    public async Task CancelledExecutionIsClassifiedUnderCancelled()
    {
        await using var db = await TestDatabase.CreateSqliteAsync("activity-cancelled");
        await DatabaseMigrator.ApplyAsync(db.Db, Harness.RepositoryRoot(), default);
        var library = Guid.NewGuid(); var flow = Guid.NewGuid(); var scan = Guid.NewGuid();
        var media = Guid.NewGuid(); var execution = Guid.NewGuid();
        await using (var insert = db.Db.CreateCommand("""
            INSERT INTO libraries(id,name) VALUES(@library,'Synthetic');
            INSERT INTO flows(id,current_version) VALUES(@flow,1);
            INSERT INTO flow_versions(flow_id,version,definition) VALUES(@flow,1,'{"name":"Synthetic"}');
            INSERT INTO library_flow_assignments(library_id,flow_id,flow_version,revision) VALUES(@library,@flow,1,1);
            INSERT INTO library_scans(id,library_id,media_type,state,completed_at) VALUES(@scan,@library,'video','completed',clock_timestamp());
            INSERT INTO library_media(id,library_id,relative_path,byte_length,modified_at,scan_id) VALUES(@media,@library,'synthetic.mkv',1,clock_timestamp(),@scan);
            INSERT INTO executions(id,media,library_name,flow_name,flow_version,flow_definition,state,attempt,detail)
              VALUES(@execution,'synthetic.mkv','Synthetic','Synthetic',1,'{}','cancelled',1,'Interrupted by user.');
            INSERT INTO execution_work(execution_id,library_id,media_path,media_key)
              VALUES(@execution,@library,'synthetic.mkv','SYNTHETIC.MKV');
            """))
        {
            insert.Parameters.AddWithValue("library",library); insert.Parameters.AddWithValue("flow",flow);
            insert.Parameters.AddWithValue("scan",scan); insert.Parameters.AddWithValue("media",media);
            insert.Parameters.AddWithValue("execution",execution);

            await insert.ExecuteNonQueryAsync();
        }

        var response = await new LibraryActivityRepository(db.Db).GetAsync("cancelled",library,null,null,null,10,0,default);
        var file = Assert.Single(response.Files);
        Assert.Equal("cancelled",file.Status);
        Assert.Equal(execution,file.ExecutionId);
        Assert.Equal("cancelled",file.ExecutionState);
        Assert.Equal(1, response.Counts.Cancelled);
    }
}
