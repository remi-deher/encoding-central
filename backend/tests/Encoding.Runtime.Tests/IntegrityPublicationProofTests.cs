using System.Text.Json;
using Encoding.Api.Executions;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class IntegrityPublicationProofTests
{
    [Fact]
    public async Task ServerRequiresCompletedCheckForExactDigestAndRejectsProofForAnotherFileVersion()
    {
        var (registry, _, _) = Harness.Services();
        var flow = Harness.Chain(Harness.Node(registry, "input", "base-input-file"), Harness.Node(registry, "check", MediaIntegrityPolicy.Component), Harness.Node(registry, "end", "base-complete-flow"));
        await using var db = await TestDatabase.CreateSqliteAsync("integrity-publication");
        await DatabaseMigrator.ApplyAsync(db.Db, Harness.RepositoryRoot(), default);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var id = Guid.NewGuid(); var invocation = Guid.NewGuid(); var sha = new string('a',64);
        await using (var insert = db.Db.CreateCommand("INSERT INTO executions(id,media,library_name,flow_name,flow_version,flow_definition,state,attempt) VALUES(@id,'synthetic','test','test',1,@flow,'running',1)"))
        {
            insert.Parameters.AddWithValue("id", id); insert.Parameters.AddWithValue("flow", SqlKind.Json, JsonSerializer.Serialize(flow,json)); await insert.ExecuteNonQueryAsync();
        }
        await using var c = await db.Db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        Assert.False(await RuntimeTransferRepository.HasIntegrityProofAsync(id,sha,c,tx,default));
        var context = new RuntimeContext(sha,".mkv",new Dictionary<string,JsonElement>(),null,null,[]);
        var checkpoint = new RuntimeCheckpoint("check",invocation,"valid",null,context,null);
        await using (var insert = Sql.Command("INSERT INTO execution_steps(execution_id,node_id,invocation_id,idempotent,completed_at,checkpoint) VALUES(@id,'check',@invocation,true,clock_timestamp(),@cp)",c,tx))
        {
            insert.Parameters.AddWithValue("id",id); insert.Parameters.AddWithValue("invocation",invocation); insert.Parameters.AddWithValue("cp",SqlKind.Json,JsonSerializer.Serialize(checkpoint,json)); await insert.ExecuteNonQueryAsync();
        }
        Assert.True(await RuntimeTransferRepository.HasIntegrityProofAsync(id,sha,c,tx,default));
        Assert.False(await RuntimeTransferRepository.HasIntegrityProofAsync(id,new string('b',64),c,tx,default));
    }
}
