using Encoding.Api.Infrastructure.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Api.Flows;
using Encoding.Api.Nodes;
using Encoding.Api.Plugins;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Nodes;

namespace Encoding.Api.Executions;

public sealed partial class RuntimeRepository(DbDataSource dataSource,RuntimeEligibility eligibility,FlowComponentRegistry components,
    NodePresenceOptions presence,PluginConfigurationRepository pluginConfigurations,NodeGroupRepository nodeGroups,ClusterSourceWriteBarrier? sourceWrites=null,
    TaskOwnershipRepository? ownership=null,FlowPlacementPlanner? placementPlanner=null,ILogger<RuntimeRepository>? logger=null)
{
    private readonly FlowPlacementPlanner planner = placementPlanner ?? new FlowPlacementPlanner(components);
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    /// <summary>États des exécutions demandées ; les identifiants inconnus de ce serveur sont absents du résultat.</summary>
    public async Task<IReadOnlyList<RuntimeExecutionState>> StatesAsync(IReadOnlyCollection<Guid> ids,CancellationToken ct)
    {
        if(ids.Count==0) return [];
        await using var cmd=dataSource.CreateCommand("SELECT id,state FROM executions WHERE id=ANY(@ids)");
        cmd.Parameters.AddWithValue("ids",ids.ToArray());
        await using var reader=await cmd.ExecuteReaderAsync(ct);
        var states=new List<RuntimeExecutionState>();
        while(await reader.ReadAsync(ct)) states.Add(new(reader.GetGuid(0),reader.GetString(1)));
        return states;
    }
    public async Task<bool> AuthenticateAsync(Guid node,string token,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(token) || token.Length>256) return false;
        await using var cmd=dataSource.CreateCommand("SELECT EXISTS(SELECT 1 FROM cluster_nodes WHERE id=@node AND deleted_at IS NULL AND node_token_hash=@hash)");
        cmd.Parameters.AddWithValue("node",node); cmd.Parameters.AddWithValue("hash",SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
        return (await cmd.ScalarBoolAsync(ct));
    }

    /// <summary>Returns the saved configuration only when this worker owns the active invocation of the exact translation block.</summary>
    public async Task<IReadOnlyDictionary<string,JsonElement>?> AiSubtitleTranslationConfigurationAsync(
        Guid executionId, Guid workerId, RuntimeAiSubtitleTranslationRequest request, CancellationToken ct)
    {
        if (executionId == Guid.Empty || workerId == Guid.Empty || request.Epoch < 1 || request.InvocationId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.FlowNodeId) || request.FlowNodeId.Length > 128) return null;
        await using var command = dataSource.CreateCommand("""
            SELECT e.flow_definition::text
            FROM executions e
            JOIN execution_work w ON w.execution_id=e.id
            JOIN execution_steps s ON s.execution_id=e.id
            WHERE e.id=@execution AND e.state='running'
              AND w.worker_node_id=@worker AND w.lease_epoch=@epoch AND w.lease_until>clock_timestamp()
              AND s.node_id=@flow_node AND s.invocation_id=@invocation AND s.completed_at IS NULL
            """);
        command.Parameters.AddWithValue("execution", executionId);
        command.Parameters.AddWithValue("worker", workerId);
        command.Parameters.AddWithValue("epoch", request.Epoch);
        command.Parameters.AddWithValue("flow_node", request.FlowNodeId);
        command.Parameters.AddWithValue("invocation", request.InvocationId);
        if (await command.ExecuteScalarAsync(ct) is not string serialized) return null;
        var flow = JsonSerializer.Deserialize<FlowDefinition>(serialized, Json);
        var node = flow?.Nodes.SingleOrDefault(item => item.Id == request.FlowNodeId);
        if (node is null || node.Component.PluginId != "ai" || node.Component.ComponentId != "ai-translate-subtitles") return null;
        return node.Configuration;
    }

    public async Task<IReadOnlyDictionary<string,JsonElement>?> AiMetadataConfigurationAsync(
        Guid executionId, Guid workerId, RuntimeAiMetadataEnrichmentRequest request, CancellationToken ct)
    {
        if (executionId == Guid.Empty || workerId == Guid.Empty || request.Epoch < 1 || request.InvocationId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.FlowNodeId) || request.FlowNodeId.Length > 128) return null;
        await using var command = dataSource.CreateCommand("""
            SELECT e.flow_definition::text FROM executions e
            JOIN execution_work w ON w.execution_id=e.id
            JOIN execution_steps s ON s.execution_id=e.id
            WHERE e.id=@execution AND e.state='running' AND w.worker_node_id=@worker AND w.lease_epoch=@epoch
              AND w.lease_until>clock_timestamp() AND s.node_id=@flow_node AND s.invocation_id=@invocation
              AND s.completed_at IS NULL
            """);
        command.Parameters.AddWithValue("execution", executionId);
        command.Parameters.AddWithValue("worker", workerId);
        command.Parameters.AddWithValue("epoch", request.Epoch);
        command.Parameters.AddWithValue("flow_node", request.FlowNodeId);
        command.Parameters.AddWithValue("invocation", request.InvocationId);
        if (await command.ExecuteScalarAsync(ct) is not string serialized) return null;
        var flow = JsonSerializer.Deserialize<FlowDefinition>(serialized, Json);
        if (flow?.MediaType != request.MediaType) return null;
        var node = flow?.Nodes.SingleOrDefault(item => item.Id == request.FlowNodeId);
        if (node is null || node.Component.PluginId != "ai" || node.Component.ComponentId != "ai-metadata-enricher") return null;
        return node.Configuration;
    }

    public async Task<bool> EnqueueAsync(EnqueueMediaRequest request,FlowDefinition flow,CancellationToken ct)
    {
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        if(!await EnqueueInTransactionAsync(request,flow,c,tx,ct)) return false;
        if(ownership is not null) await ownership.CreateAsync([request.Id],c,tx,ct);
        await tx.CommitAsync(ct); return true;
    }

    internal static async Task<bool> EnqueueInTransactionAsync(EnqueueMediaRequest request,FlowDefinition flow,DbConnection c,DbTransaction tx,CancellationToken ct, string requestOrigin = "api")
    {
        var path=NormalizePath(request.MediaPath);
        await using var library=Sql.Command("SELECT name FROM libraries WHERE id=@id AND is_active FOR UPDATE",c,tx);
        library.Parameters.AddWithValue("id",request.LibraryId);
        if(await library.ExecuteScalarAsync(ct) is not string name) return false;
        await using var duplicate=Sql.Command("SELECT EXISTS(SELECT 1 FROM execution_work w JOIN executions e ON e.id=w.execution_id WHERE w.library_id=@library AND w.media_key=@key AND e.state IN ('queued','running','cancelling')) OR EXISTS(SELECT 1 FROM task_ownership o LEFT JOIN executions e ON e.id=o.execution_id WHERE o.cluster_id=(SELECT id FROM cluster_identity WHERE singleton=true) AND o.library_id=@library AND o.media_key=@key AND o.state IN ('active','frozen','received','transferred') AND (e.id IS NULL OR e.state IN ('queued','running','cancelling')))",
            c,tx);
        duplicate.Parameters.AddWithValue("library",request.LibraryId); duplicate.Parameters.AddWithValue("key",path.ToUpperInvariant());
        if((await duplicate.ScalarBoolAsync(ct))) return false;
        var creation=new CreateExecutionRequest(request.Id,path,name,flow.Name,flow.Version,JsonSerializer.SerializeToElement(flow,Json));
        if(!await ExecutionRepository.CreateInTransactionAsync(creation,c,tx,ct)) return false;
        await using var work=Sql.Command("INSERT INTO execution_work(execution_id,library_id,media_path,media_key) VALUES(@id,@library,@path,@key)",c,tx);
        work.Parameters.AddWithValue("id",request.Id); work.Parameters.AddWithValue("library",request.LibraryId);
        work.Parameters.AddWithValue("path",path); work.Parameters.AddWithValue("key",path.ToUpperInvariant());
        await work.ExecuteNonQueryAsync(ct);
        var origin = requestOrigin is "surveillance" or "trigger" or "manual" ? requestOrigin : "api";
        await using var audit = Sql.Command("UPDATE executions SET detail=@detail WHERE id=@id; UPDATE execution_events SET detail=@detail WHERE execution_id=@id AND revision=0", c, tx);
        audit.Parameters.AddWithValue("id", request.Id); audit.Parameters.AddWithValue("detail", "Demande créée : " + origin + ".");
        await audit.ExecuteNonQueryAsync(ct);
        return true;
    }

    public static string NormalizePath(string? path)=>Encoding.Contracts.Libraries.MediaPathRules.Normalize(path);

    /// <summary>
    /// Un nœud source n'est déclaré perdu pour une exécution en file qu'après ce délai, même si la présence est plus courte :
    /// un heartbeat retardé (verrou SQLite, redémarrage) ne doit pas faire échouer des centaines d'exécutions saines.
    /// </summary>
    internal static readonly TimeSpan SourceOfflineGrace = TimeSpan.FromMinutes(5);

    private static readonly string UnavailableSourcesSql = $"""
        SELECT e.id,e.node_id,e.revision FROM executions e JOIN execution_work w ON w.execution_id=e.id
        WHERE e.state IN ('queued','running') AND {TaskOwnershipRepository.ActiveSql("e.id")} AND NOT EXISTS(
          SELECT 1 FROM library_node_accesses a JOIN cluster_nodes n ON n.id=a.node_id
          WHERE a.library_id=w.library_id AND a.mode='direct' AND a.is_enabled AND a.can_read AND a.local_path IS NOT NULL
            AND n.deleted_at IS NULL AND n.last_seen_at>clock_timestamp()-@offline
            AND (w.handoff_selector IS NULL OR w.source_node_id IS NULL OR a.node_id=w.source_node_id OR w.handoff_selector->>'mode'='resume' OR w.handoff_selector->>'resumed' IN (1,'true')))
        LIMIT 100
        """;

    /// <summary>Lecture seule (transaction différée) : évite de prendre le verrou d'écriture quand aucune source n'est perdue.</summary>
    internal static async Task<bool> HasUnavailableSourcesAsync(DbDataSource dataSource, TimeSpan offlineAfter, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Snapshot, ct);
        await using var command = Sql.Command(UnavailableSourcesSql + " ", connection, tx);
        command.Parameters.AddWithValue("offline", offlineAfter > SourceOfflineGrace ? offlineAfter : SourceOfflineGrace);
        await using var rows = await command.ExecuteReaderAsync(ct);
        return await rows.ReadAsync(ct);
    }

    internal static async Task<int> FailUnavailableSourcesAsync(DbConnection connection, DbTransaction tx, TimeSpan offlineAfter, CancellationToken ct)
    {
        var failures = new List<(Guid Id, Guid? Worker, int Revision)>();
        await using (var command = Sql.Command(UnavailableSourcesSql + " FOR UPDATE OF e,w SKIP LOCKED", connection, tx))
        {
            command.Parameters.AddWithValue("offline", offlineAfter > SourceOfflineGrace ? offlineAfter : SourceOfflineGrace);
            await using var rows = await command.ExecuteReaderAsync(ct);
            while (await rows.ReadAsync(ct)) failures.Add((rows.GetGuid(0), rows.IsDBNull(1) ? null : rows.GetGuid(1), rows.GetInt32(2)));
        }
        foreach (var failure in failures)
        {
            await ExecutionRepository.RecordInTransactionAsync(failure.Id, new(Guid.NewGuid(), failure.Revision, "failed",
                failure.Worker, null, "Média inaccessible : aucun nœud source direct disponible. La tâche a échoué ; la source est conservée."), connection, tx, ct);
            await ReleaseAsync(failure.Id, connection, tx, ct);
        }
        return failures.Count;
    }
}
