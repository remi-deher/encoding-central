using Encoding.Api.Executions;
using Encoding.Api.Flows;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Libraries;
using Encoding.Api.Nodes;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class LibraryFlowChangeTests
{
    [Fact]
    public async Task ChangingTheFlowRetargetsQueuedExecutionsOnlyAndQueuesNothingNew()
    {
        await using var db = await TestDatabase.CreateSqliteAsync("library-flow-change");
        await DatabaseMigrator.ApplyAsync(db.Db, Harness.RepositoryRoot(), default);
        var (registry, validator, _) = Harness.Services();
        var flows = new FlowRepository(db.Db);
        var first = Harness.Chain(Harness.Node(registry, "entry", "base-input-file"), Harness.Node(registry, "end", "base-complete-flow")) with { Id = Guid.NewGuid(), Name = "Premier" };
        var second = first with { Id = Guid.NewGuid(), Name = "Second" };
        Assert.NotNull(await flows.SaveAsync(new(0, first), default));
        Assert.NotNull(await flows.SaveAsync(new(0, second), default));
        var library = Guid.NewGuid(); var scan = Guid.NewGuid();
        await using (var setup = db.Db.CreateCommand("""
            INSERT INTO libraries(id,name) VALUES(@library,'Synthetic');
            INSERT INTO library_scans(id,library_id,media_type,state,completed_at) VALUES(@scan,@library,'video','completed',clock_timestamp());
            """))
        { setup.Parameters.AddWithValue("library", library); setup.Parameters.AddWithValue("scan", scan); await setup.ExecuteNonQueryAsync(); }
        var modified = new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero);
        var media = new Dictionary<string, Guid>();
        foreach (var name in new[] { "queued.mkv", "done.mkv", "running.mkv" })
        {
            media[name] = Guid.NewGuid();
            await using var file = db.Db.CreateCommand("INSERT INTO library_media(id,library_id,relative_path,byte_length,modified_at,scan_id,stable_since) VALUES(@id,@library,@path,10,@modified,@scan,clock_timestamp()-interval '1 hour')");
            file.Parameters.AddWithValue("id", media[name]); file.Parameters.AddWithValue("library", library); file.Parameters.AddWithValue("path", name);
            file.Parameters.AddWithValue("modified", modified); file.Parameters.AddWithValue("scan", scan); await file.ExecuteNonQueryAsync();
        }
        var workflow = new LibraryWorkflowRepository(db.Db, validator, new NodePresenceOptions(TimeSpan.FromHours(1)));
        Assert.True(await workflow.AssignAsync(library, new(first.Id, 1, 0), default));
        Assert.NotNull(await workflow.EnqueueAsync(library, [.. media.Values], 1, default));
        await Set("UPDATE executions SET state='succeeded' WHERE media='done.mkv'");
        await Set("UPDATE executions SET state='running' WHERE media='running.mkv'");

        Assert.True(await workflow.AssignAsync(library, new(second.Id, 1, 1), default));

        Assert.Equal("Second", await Text("SELECT flow_name FROM executions WHERE media='queued.mkv'"));
        Assert.Equal("Premier", await Text("SELECT flow_name FROM executions WHERE media='done.mkv'"));
        Assert.Equal("Premier", await Text("SELECT flow_name FROM executions WHERE media='running.mkv'"));
        Assert.Equal("3", await Text("SELECT count(*) FROM executions"));
        await workflow.EnqueueAutomaticallyAsync(library, default);
        Assert.Equal("3", await Text("SELECT count(*) FROM executions"));

        async Task Set(string sql) { await using var c = db.Db.CreateCommand(sql); await c.ExecuteNonQueryAsync(); }
        async Task<string> Text(string sql) { await using var c = db.Db.CreateCommand(sql); return Convert.ToString(await c.ExecuteScalarAsync())!; }
    }
}
