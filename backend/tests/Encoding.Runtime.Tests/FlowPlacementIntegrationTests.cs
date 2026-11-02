using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Api.Executions;
using Encoding.Api.Flows;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Nodes;
using Encoding.Api.Plugins;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class FlowPlacementIntegrationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value, Json);

    private static FlowNodeDefinition Node(FlowComponentRegistry registry, string id, string componentId, Dictionary<string, JsonElement>? config = null)
    {
        var reference = registry.FindReference(componentId) ?? throw new InvalidOperationException($"Component {componentId} not found.");
        return new FlowNodeDefinition(id, id, reference, config ?? []);
    }

    private static FlowDefinition CreateChainFlow(string name, params FlowNodeDefinition[] nodes)
    {
        var connections = new List<FlowConnectionDefinition>();
        for (var i = 0; i < nodes.Length - 1; i++)
        {
            connections.Add(new FlowConnectionDefinition($"conn_{i}", nodes[i].Id, "success", nodes[i + 1].Id));
        }
        return new FlowDefinition(1, Guid.NewGuid(), 1, name, "video", nodes, connections);
    }

    private static NodeToolCapability[] StandardCapabilities(params string[] encoders) =>
    [
        new("plugin/base", "1.9.0", ["general"], []),
        new("plugin/conditions", "1.1.0", ["general"], []),
        new("ffmpeg", "7.0.0", ["video", "audio"], ["cpu"], encoders.Length > 0 ? encoders : ["libx264", "aac"]),
        new("ffprobe", "7.0.0", ["probe"], []),
        new("mkvmerge", "85.0.0", ["mkv"], [])
    ];

    [Fact]
    public async Task DirectUnifiedClaim_DirectAccessNodeClaims_AndTransferNodeSkips()
    {
        await using var database = await TestDatabase.CreateSqliteAsync("placement-direct-unified");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);

        var (registry, validator, eligibility) = Harness.Services();
        var presence = new NodePresenceOptions(TimeSpan.FromHours(1));
        var configs = new PluginConfigurationRepository(database.Db, new EphemeralDataProtectionProvider());
        var groups = new NodeGroupRepository(database.Db);
        var planner = new FlowPlacementPlanner(registry);
        var runtime = new RuntimeRepository(database.Db, eligibility, registry, presence, configs, groups, placementPlanner: planner);

        var libId = Guid.NewGuid();
        var nodeA = Guid.NewGuid(); // Direct node
        var nodeB = Guid.NewGuid(); // Transfer node

        await using (var c = await database.Db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            await using var cmd = Sql.Command("""
                INSERT INTO libraries(id, name, is_active) VALUES(@lib, 'TestLib', true);
                INSERT INTO cluster_nodes(id, name, roles, max_concurrent_tasks, capabilities, last_seen_at)
                VALUES(@nodeA, 'Node-A-Direct', ARRAY['worker'], 4, @capsA, clock_timestamp()),
                      (@nodeB, 'Node-B-Transfer', ARRAY['worker'], 4, @capsB, clock_timestamp());
                INSERT INTO library_node_accesses(library_id, node_id, mode, local_path, can_read, can_write, is_enabled, transfer_source_node_id)
                VALUES(@lib, @nodeA, 'direct', '/media/library', true, true, true, NULL),
                      (@lib, @nodeB, 'transfer', NULL, true, true, true, @nodeA);
                """, c, tx);
            cmd.Parameters.AddWithValue("lib", libId);
            cmd.Parameters.AddWithValue("nodeA", nodeA);
            cmd.Parameters.AddWithValue("nodeB", nodeB);
            cmd.Parameters.AddWithValue("capsA", SqlKind.Json, JsonSerializer.Serialize(StandardCapabilities("libx264", "aac"), Json));
            cmd.Parameters.AddWithValue("capsB", SqlKind.Json, JsonSerializer.Serialize(StandardCapabilities("libx264", "aac"), Json));
            await cmd.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        }

        var flow = CreateChainFlow("Direct Unified Test",
            Node(registry, "input", "base-input-file"),
            Node(registry, "remux", "base-remux-media"),
            Node(registry, "complete", "base-complete-flow"));

        var execId = Guid.NewGuid();
        await using (var c = await database.Db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            var enqueued = await RuntimeRepository.EnqueueInTransactionAsync(
                new EnqueueMediaRequest(execId, libId, "movie.mkv", flow.Id, flow.Version),
                flow, c, tx, CancellationToken.None);
            Assert.True(enqueued);
            await tx.CommitAsync();
        }

        // Node B (transfer) tries to claim -> must be skipped because Node A covers everything directly
        var claimB = await runtime.ClaimAsync(nodeB, CancellationToken.None);
        Assert.Null(claimB);

        // Node A (direct) tries to claim -> must succeed
        var claimA = await runtime.ClaimAsync(nodeA, CancellationToken.None);
        Assert.NotNull(claimA);
        Assert.Equal(execId, claimA.ExecutionId);
        Assert.Equal(FlowPlacementPlan.StrategyDirectUnified,claimA.PlacementPlan?.Strategy);
        await using(var beforePlan=database.Db.CreateCommand("SELECT placement_plan::text FROM execution_work WHERE execution_id=@id"))
        {beforePlan.Parameters.AddWithValue("id",execId);var value=await beforePlan.ExecuteScalarAsync();Assert.True(value is null or DBNull || string.IsNullOrWhiteSpace(value.ToString()),$"Claim persisted an unexpected placement plan: {value}");}
        Assert.Equal(FlowPlacementPlan.StrategyDirectUnified,(await runtime.PersistPlacementPlanAsync(execId,nodeA,claimA.Epoch,CancellationToken.None))?.Strategy);
        await runtime.RecordSourceMetadataAsync(execId,nodeA,claimA.Epoch,JsonSerializer.SerializeToElement(new { streams=Array.Empty<object>() },Json),CancellationToken.None);

        // Verify stored placement plan
        await using (var c = await database.Db.OpenConnectionAsync())
        {
            await using var verifyCmd = Sql.Command("SELECT placement_plan::text FROM execution_work WHERE execution_id=@id", c);
            verifyCmd.Parameters.AddWithValue("id", execId);
            var jsonText = await verifyCmd.ExecuteScalarAsync() as string;
            Assert.NotNull(jsonText);
            var plan = JsonSerializer.Deserialize<FlowPlacementPlan>(jsonText, Json);
            Assert.NotNull(plan);
            Assert.Equal(FlowPlacementPlan.StrategyDirectUnified, plan.Strategy);
            Assert.Equal(nodeA, plan.AssignedNodeId);
        }
    }

    [Fact]
    public async Task ImpossibleFlow_FailsEarly_WithoutLease()
    {
        await using var database = await TestDatabase.CreateSqliteAsync("placement-impossible");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);

        var (registry, validator, eligibility) = Harness.Services();
        var presence = new NodePresenceOptions(TimeSpan.FromHours(1));
        var configs = new PluginConfigurationRepository(database.Db, new EphemeralDataProtectionProvider());
        var groups = new NodeGroupRepository(database.Db);
        var planner = new FlowPlacementPlanner(registry);
        var runtime = new RuntimeRepository(database.Db, eligibility, registry, presence, configs, groups, placementPlanner: planner);

        var libId = Guid.NewGuid();
        var nodeA = Guid.NewGuid();

        await using (var c = await database.Db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            await using var cmd = Sql.Command("""
                INSERT INTO libraries(id, name, is_active) VALUES(@lib, 'TestLib', true);
                INSERT INTO cluster_nodes(id, name, roles, max_concurrent_tasks, capabilities, last_seen_at)
                VALUES(@nodeA, 'Node-A', ARRAY['worker'], 4, @capsA, clock_timestamp());
                INSERT INTO library_node_accesses(library_id, node_id, mode, local_path, can_read, can_write, is_enabled)
                VALUES(@lib, @nodeA, 'direct', '/media/library', true, true, true);
                """, c, tx);
            cmd.Parameters.AddWithValue("lib", libId);
            cmd.Parameters.AddWithValue("nodeA", nodeA);
            cmd.Parameters.AddWithValue("capsA", SqlKind.Json, JsonSerializer.Serialize(StandardCapabilities("libx264"), Json));
            await cmd.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        }

        // Flow requiring a nonexistent capability
        var flow = CreateChainFlow("Impossible Flow",
            Node(registry, "input", "base-input-file"),
            Node(registry, "req", "nodes-require-capability", new()
            {
                ["toolId"] = J("nonexistent-tool"),
                ["requiredCapability"] = J("custom-hw")
            }),
            Node(registry, "complete", "base-complete-flow"));

        var execId = Guid.NewGuid();
        await using (var c = await database.Db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            var enqueued = await RuntimeRepository.EnqueueInTransactionAsync(
                new EnqueueMediaRequest(execId, libId, "movie.mkv", flow.Id, flow.Version),
                flow, c, tx, CancellationToken.None);
            Assert.True(enqueued);
            await tx.CommitAsync();
        }

        // An impossible placement remains queued until the existing read-only placement grace expires.
        var claim = await runtime.ClaimAsync(nodeA, CancellationToken.None);
        Assert.Null(claim);
        await using(var age=database.Db.CreateCommand("UPDATE executions SET queued_at=clock_timestamp()-interval '3 minutes' WHERE id=@id"))
        { age.Parameters.AddWithValue("id",execId); await age.ExecuteNonQueryAsync(); }
        var recovery=new RuntimeRecoveryService(database.Db,Microsoft.Extensions.Logging.Abstractions.NullLogger<RuntimeRecoveryService>.Instance,groups,eligibility);
        await recovery.ProcessOnceAsync(CancellationToken.None);

        // Verify execution state was marked 'failed' with diagnostic
        await using (var c = await database.Db.OpenConnectionAsync())
        {
            await using var verifyCmd = Sql.Command("SELECT state, detail FROM executions WHERE id=@id", c);
            verifyCmd.Parameters.AddWithValue("id", execId);
            await using var r = await verifyCmd.ExecuteReaderAsync();
            Assert.True(await r.ReadAsync());
            Assert.Equal("failed", r.GetString(0));
            Assert.Contains("placement_impossible", r.GetString(1));
        }
    }

    [Fact]
    public async Task ConditionEvaluation_ElidesTranscode_AndKeepsTaskOnDirectNode()
    {
        await using var database = await TestDatabase.CreateSqliteAsync("placement-condition-pruning");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);

        var (registry, validator, eligibility) = Harness.Services();
        var presence = new NodePresenceOptions(TimeSpan.FromHours(1));
        var configs = new PluginConfigurationRepository(database.Db, new EphemeralDataProtectionProvider());
        var groups = new NodeGroupRepository(database.Db);
        var planner = new FlowPlacementPlanner(registry);
        var runtime = new RuntimeRepository(database.Db, eligibility, registry, presence, configs, groups, placementPlanner: planner);

        var libId = Guid.NewGuid();
        var nodeA = Guid.NewGuid(); // Direct node (CPU only)
        var nodeB = Guid.NewGuid(); // Transfer node (has hevc_nvenc)

        await using (var c = await database.Db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            await using var cmd = Sql.Command("""
                INSERT INTO libraries(id, name, is_active) VALUES(@lib, 'TestLib', true);
                INSERT INTO cluster_nodes(id, name, roles, max_concurrent_tasks, capabilities, last_seen_at)
                VALUES(@nodeA, 'Node-A-Direct', ARRAY['worker'], 4, @capsA, clock_timestamp()),
                      (@nodeB, 'Node-B-Transfer', ARRAY['worker'], 4, @capsB, clock_timestamp());
                INSERT INTO library_node_accesses(library_id, node_id, mode, local_path, can_read, can_write, is_enabled, transfer_source_node_id)
                VALUES(@lib, @nodeA, 'direct', '/media/library', true, true, true, NULL),
                      (@lib, @nodeB, 'transfer', NULL, true, true, true, @nodeA);
                """, c, tx);
            cmd.Parameters.AddWithValue("lib", libId);
            cmd.Parameters.AddWithValue("nodeA", nodeA);
            cmd.Parameters.AddWithValue("nodeB", nodeB);
            cmd.Parameters.AddWithValue("capsA", SqlKind.Json, JsonSerializer.Serialize(StandardCapabilities("libx264", "aac"), Json));
            cmd.Parameters.AddWithValue("capsB", SqlKind.Json, JsonSerializer.Serialize(StandardCapabilities("libx264", "hevc_nvenc", "aac"), Json));
            await cmd.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        }

        // Flow with condition: if video is hevc, copy (base-remux-media); else transcode with hevc_nvenc
        var inputNode = Node(registry, "input", "base-input-file");
        var condNode = Node(registry, "cond", "conditions-video-codec", new()
        {
            ["matchMode"] = J("is"),
            ["codec"] = J("hevc")
        });
        var copyNode = Node(registry, "copy", "base-remux-media");
        var encodeNode = Node(registry, "encode", "video-transcode", new()
        {
            ["encoder"] = J("hevc_nvenc")
        });
        var completeNode = Node(registry, "complete", "base-complete-flow");

        var connections = new FlowConnectionDefinition[]
        {
            new("c1", "input", "success", "cond"),
            new("c2", "cond", "yes", "copy"),
            new("c3", "cond", "no", "encode"),
            new("c4", "copy", "success", "complete"),
            new("c5", "encode", "success", "complete")
        };

        var flow = new FlowDefinition(1, Guid.NewGuid(), 1, "Conditional Transcode Flow", "video",
            [inputNode, condNode, copyNode, encodeNode, completeNode], connections);
        var beforeMeta = J(new
        {
            streams = new object[]
            {
                new { codec_type = "video", codec_name = "hevc", pix_fmt = "yuv420p10le", bits_per_raw_sample = "10", width = 1920, height = 1080 }
            }
        });

        var execId = Guid.NewGuid();
        await using (var c = await database.Db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            var enqueued = await RuntimeRepository.EnqueueInTransactionAsync(
                new EnqueueMediaRequest(execId, libId, "already_hevc.mkv", flow.Id, flow.Version),
                flow, c, tx, CancellationToken.None);
            Assert.True(enqueued);

            // Simulate probe step having stored before_metadata with video.codec = "hevc"
            await using var metaCmd = Sql.Command("UPDATE execution_work SET before_metadata=@meta WHERE execution_id=@id", c, tx);
            metaCmd.Parameters.AddWithValue("id", execId);
            metaCmd.Parameters.AddWithValue("meta", SqlKind.Json, beforeMeta.GetRawText());
            await metaCmd.ExecuteNonQueryAsync();

            await tx.CommitAsync();
        }

        // Because the media is already HEVC, the transcode branch is pruned!
        // Node B (transfer) must skip:
        var claimB = await runtime.ClaimAsync(nodeB, CancellationToken.None);
        Assert.Null(claimB);

        // Node A (direct access) claims and executes locally:
        var claimA = await runtime.ClaimAsync(nodeA, CancellationToken.None);
        Assert.NotNull(claimA);
        Assert.Equal(execId, claimA.ExecutionId);
        await runtime.RecordSourceMetadataAsync(execId,nodeA,claimA.Epoch,beforeMeta,CancellationToken.None);

        // Verify the plan is DirectUnified
        await using (var c = await database.Db.OpenConnectionAsync())
        {
            await using var verifyCmd = Sql.Command("SELECT placement_plan::text FROM execution_work WHERE execution_id=@id", c);
            verifyCmd.Parameters.AddWithValue("id", execId);
            var jsonText = await verifyCmd.ExecuteScalarAsync() as string;
            Assert.NotNull(jsonText);
            var plan = JsonSerializer.Deserialize<FlowPlacementPlan>(jsonText, Json);
            Assert.NotNull(plan);
            Assert.Equal(FlowPlacementPlan.StrategyDirectUnified, plan.Strategy);
            Assert.Equal(nodeA, plan.AssignedNodeId);
        }
    }

    [Fact]
    public async Task SegmentedPlan_IsAppliedAtCheckpointAndRoutesTheNextClaimToTheSpecialist()
    {
        await using var database=await TestDatabase.CreateSqliteAsync("placement-segmented-runtime");
        await DatabaseMigrator.ApplyAsync(database.Db,Harness.RepositoryRoot(),CancellationToken.None);
        var (registry,_,eligibility)=Harness.Services();
        var presence=new NodePresenceOptions(TimeSpan.FromHours(1));
        var configurations=new PluginConfigurationRepository(database.Db,new EphemeralDataProtectionProvider());
        var groups=new NodeGroupRepository(database.Db);
        var runtime=new RuntimeRepository(database.Db,eligibility,registry,presence,configurations,groups);
        var library=Guid.NewGuid();var direct=Guid.NewGuid();var gpu=Guid.NewGuid();
        NodeToolCapability[] directCapabilities=
        [
            new("plugin/base","1.9.0",["general"],[]),new("plugin/video","1.2.0",["general"],[]),new("plugin/mkv","1.0.0",["general"],[]),
            new("ffprobe","7.0.0",["probe"],[]),new("ffmpeg","7.0.0",["video"],["cpu"],["libx264"]),new("mkvtoolnix","90.0",["mkv"],[])
        ];
        NodeToolCapability[] gpuCapabilities=
        [
            new("plugin/video","1.2.0",["general"],[]),new("ffprobe","7.0.0",["probe"],[]),
            new("ffmpeg","7.0.0",["video"],["qsv"],["hevc_qsv"])
        ];
        await using(var c=await database.Db.OpenConnectionAsync())
        await using(var tx=await c.BeginTransactionAsync())
        {
            await using var insert=Sql.Command("""
                INSERT INTO libraries(id,name,is_active) VALUES(@library,'Synthetic',true);
                INSERT INTO cluster_nodes(id,name,roles,max_concurrent_tasks,capabilities,last_seen_at)
                VALUES(@direct,'Direct',ARRAY['worker'],4,@directCapabilities,clock_timestamp()),
                      (@gpu,'GPU',ARRAY['worker'],4,@gpuCapabilities,clock_timestamp());
                INSERT INTO library_node_accesses(library_id,node_id,mode,local_path,can_read,can_write,is_enabled,transfer_source_node_id)
                VALUES(@library,@direct,'direct','/synthetic',true,true,true,NULL),(@library,@gpu,'transfer',NULL,true,true,true,@direct);
                """,c,tx);
            insert.Parameters.AddWithValue("library",library);insert.Parameters.AddWithValue("direct",direct);insert.Parameters.AddWithValue("gpu",gpu);
            insert.Parameters.AddWithValue("directCapabilities",SqlKind.Json,JsonSerializer.Serialize(directCapabilities,Json));
            insert.Parameters.AddWithValue("gpuCapabilities",SqlKind.Json,JsonSerializer.Serialize(gpuCapabilities,Json));
            await insert.ExecuteNonQueryAsync();await tx.CommitAsync();
        }
        var flow=CreateChainFlow("segmented synthetic",
            Node(registry,"input","base-input-file"),
            Node(registry,"encode","video-transcode",new(){["videoCodec"]=JsonSerializer.SerializeToElement("hevc",Json),["hardwareAcceleration"]=JsonSerializer.SerializeToElement("qsv",Json)}),
            Node(registry,"remux","mkv-remux-matroska"),Node(registry,"complete","base-complete-flow"));
        var id=Guid.NewGuid();
        await using(var c=await database.Db.OpenConnectionAsync())
        await using(var tx=await c.BeginTransactionAsync())
        {
            Assert.True(await RuntimeRepository.EnqueueInTransactionAsync(new(id,library,"synthetic.mkv",flow.Id,flow.Version),flow,c,tx,CancellationToken.None));
            await tx.CommitAsync();
        }
        Assert.Null(await runtime.ClaimAsync(gpu,CancellationToken.None));
        var lease=await runtime.ClaimAsync(direct,CancellationToken.None);
        Assert.NotNull(lease);
        Assert.Equal(FlowPlacementPlan.StrategySegmented,lease.PlacementPlan?.Strategy);
        Assert.Equal(gpu,lease.PlacementPlan!.NodeAssignments!["encode"]);
        Assert.Equal(FlowPlacementPlan.StrategySegmented,(await runtime.PersistPlacementPlanAsync(id,direct,lease.Epoch,CancellationToken.None))?.Strategy);
        var metadata=JsonSerializer.SerializeToElement(new { streams=new[]{new{codec_type="video",codec_name="h264",width=16,height=16}} },Json);
        await runtime.RecordSourceMetadataAsync(id,direct,lease.Epoch,metadata,CancellationToken.None);
        var started=await runtime.StartStepAsync(id,direct,new(lease.Epoch,"input"),CancellationToken.None);
        Assert.NotNull(started);
        const string sha="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        await using(var artifact=database.Db.CreateCommand("INSERT INTO execution_artifacts(execution_id,sha256,byte_length) VALUES(@id,@sha,1)"))
        {artifact.Parameters.AddWithValue("id",id);artifact.Parameters.AddWithValue("sha",sha);await artifact.ExecuteNonQueryAsync();}
        var context=new RuntimeContext(sha,".mkv",new Dictionary<string,JsonElement>(),metadata,metadata,[]);
        var checkpoint=new RuntimeCheckpoint("input",started!.InvocationId,"success","encode",context,null,new RuntimeHandoffSelector("node",gpu),null,true);
        var status=await runtime.RenewAsync(id,direct,lease.Epoch,CancellationToken.None);
        Assert.NotNull(status);
        Assert.True(await runtime.CompleteStepAsync(id,direct,new(lease.Epoch,Guid.NewGuid(),status!.Revision,checkpoint),CancellationToken.None));
        var received=await runtime.ClaimAsync(gpu,CancellationToken.None);
        Assert.NotNull(received);
        Assert.Equal(id,received.ExecutionId);
        Assert.Equal("encode",received.Checkpoint?.NextNodeId);
    }
}
