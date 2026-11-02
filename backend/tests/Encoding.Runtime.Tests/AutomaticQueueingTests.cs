using Encoding.Api.Flows;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Libraries;
using Encoding.Api.Nodes;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>
/// Mise en file automatique par la surveillance : un serveur ne crée des exécutions que pour les bibliothèques qu'il peut servir.
/// Quand un autre nœud détient l'accès direct, c'est lui qui les met en file ; sinon l'exécution naîtrait chez un propriétaire
/// qui ne peut pas la traiter et resterait en file sans fin.
/// </summary>
public sealed class AutomaticQueueingTests
{
    [Theory]
    [InlineData("direct", "server,worker", 1)]
    [InlineData("transfer", "server,worker", 0)]
    // Seul un worker monte la bibliothèque : aucun serveur n'y a accès, le serveur local met en file et l'exécution passe par transfert.
    [InlineData("transfer", "worker", 1)]
    public async Task ServerQueuesAutomaticallyOnlyWhenItsLocalNodeHoldsTheDirectAccessOfTheLibrary(string localMode, string otherRoles, int expected)
    {
        await using var db = await TestDatabase.CreateSqliteAsync("automatic-queueing");
        await DatabaseMigrator.ApplyAsync(db.Db, Harness.RepositoryRoot(), default);
        var (registry, validator, _) = Harness.Services();
        var flow = Harness.Chain(Harness.Node(registry, "entry", "base-input-file"), Harness.Node(registry, "end", "base-complete-flow")) with { Id = Guid.NewGuid(), Name = "Synthétique" };
        Assert.NotNull(await new FlowRepository(db.Db).SaveAsync(new(0, flow), default));
        var library = Guid.NewGuid(); var scan = Guid.NewGuid(); var local = Guid.NewGuid(); var other = Guid.NewGuid();
        await Run("INSERT INTO libraries(id,name) VALUES(@library,'Synthétique'); INSERT INTO library_scans(id,library_id,media_type,state,completed_at) VALUES(@scan,@library,'video','completed',clock_timestamp());", ("library", library), ("scan", scan));
        await Run("INSERT INTO library_media(id,library_id,relative_path,byte_length,modified_at,scan_id,stable_since) VALUES(@id,@library,'episode.mkv',10,clock_timestamp()-interval '1 hour',@scan,clock_timestamp()-interval '1 hour')", ("id", Guid.NewGuid()), ("library", library), ("scan", scan));
        // Deux nœuds : le nœud de ce serveur (mode variable) et un autre nœud qui monte la bibliothèque en direct.
        foreach (var (node, name, roles) in new[] { (local, "local", "server,worker"), (other, "autre", otherRoles) })
            await Run("INSERT INTO cluster_nodes(id,name,roles,max_concurrent_tasks,capabilities,last_seen_at) VALUES(@node,@name,@roles,1,'[]',clock_timestamp())", ("node", node), ("name", name), ("roles", roles.Split(',')));
        await Run("INSERT INTO library_node_accesses(library_id,node_id,mode,local_path,can_read,can_write) VALUES(@library,@node,'direct','/media/a',true,true)", ("library", library), ("node", other));
        if (localMode == "direct") await Run("INSERT INTO library_node_accesses(library_id,node_id,mode,local_path,can_read,can_write) VALUES(@library,@node,'direct','/media/b',true,true)", ("library", library), ("node", local));
        else await Run("INSERT INTO library_node_accesses(library_id,node_id,mode,can_read,can_write,transfer_source_node_id) VALUES(@library,@node,'transfer',true,true,@source)", ("library", library), ("node", local), ("source", other));
        var localNode = new LocalNodeRegistry(new NodeRepository(db.Db)); await localNode.RegisterAsync(local, default);
        var workflow = new LibraryWorkflowRepository(db.Db, validator, new NodePresenceOptions(TimeSpan.FromHours(1)), localNode: localNode);
        Assert.True(await workflow.AssignAsync(library, new(flow.Id, 1, 0), default));

        await workflow.EnqueueAutomaticallyAsync(library, default);

        await using var count = db.Db.CreateCommand("SELECT count(*) FROM executions");
        Assert.Equal(expected, Convert.ToInt32(await count.ExecuteScalarAsync()));

        async Task Run(string sql, params (string Name, object Value)[] parameters)
        {
            await using var command = db.Db.CreateCommand(sql);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            await command.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task ServerQueuesAutomaticallyWhenNoNodeMountsTheLibraryDirectly()
    {
        await using var db = await TestDatabase.CreateSqliteAsync("automatic-queueing-transfer-only");
        await DatabaseMigrator.ApplyAsync(db.Db, Harness.RepositoryRoot(), default);
        var (registry, validator, _) = Harness.Services();
        var flow = Harness.Chain(Harness.Node(registry, "entry", "base-input-file"), Harness.Node(registry, "end", "base-complete-flow")) with { Id = Guid.NewGuid(), Name = "Synthétique" };
        Assert.NotNull(await new FlowRepository(db.Db).SaveAsync(new(0, flow), default));
        var library = Guid.NewGuid(); var scan = Guid.NewGuid(); var local = Guid.NewGuid();
        async Task Run(string sql, params (string Name, object Value)[] parameters)
        {
            await using var command = db.Db.CreateCommand(sql);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            await command.ExecuteNonQueryAsync();
        }
        await Run("INSERT INTO libraries(id,name) VALUES(@library,'Synthétique'); INSERT INTO library_scans(id,library_id,media_type,state,completed_at) VALUES(@scan,@library,'video','completed',clock_timestamp());", ("library", library), ("scan", scan));
        await Run("INSERT INTO library_media(id,library_id,relative_path,byte_length,modified_at,scan_id,stable_since) VALUES(@id,@library,'episode.mkv',10,clock_timestamp()-interval '1 hour',@scan,clock_timestamp()-interval '1 hour')", ("id", Guid.NewGuid()), ("library", library), ("scan", scan));
        await Run("INSERT INTO cluster_nodes(id,name,roles,max_concurrent_tasks,capabilities,last_seen_at) VALUES(@node,'local',ARRAY['server','worker'],1,'[]',clock_timestamp())", ("node", local));
        var localNode = new LocalNodeRegistry(new NodeRepository(db.Db)); await localNode.RegisterAsync(local, default);
        var workflow = new LibraryWorkflowRepository(db.Db, validator, new NodePresenceOptions(TimeSpan.FromHours(1)), localNode: localNode);
        Assert.True(await workflow.AssignAsync(library, new(flow.Id, 1, 0), default));

        await workflow.EnqueueAutomaticallyAsync(library, default);

        await using var count = db.Db.CreateCommand("SELECT count(*) FROM executions");
        Assert.Equal(1, Convert.ToInt32(await count.ExecuteScalarAsync()));
    }
}
