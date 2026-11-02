using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Plugins;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
namespace Encoding.Api.Executions;

public sealed class TaskCheckpointRepository(ClusterPeerIdentityStore identity,ClusterDataKeyStore keys,PluginConfigurationRepository settings,RuntimeArtifactStore artifacts)
{
    internal static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    internal const string Prefix="tasks/v2/";
    public string Sign(ClusterTaskEvent value)
    {
        var data=JsonSerializer.Serialize(value,Json);
        return JsonSerializer.Serialize(new SignedTaskOwnershipPlan(data,Convert.ToBase64String(identity.Sign(System.Text.Encoding.UTF8.GetBytes(data)))),Json);
    }
    internal async Task<(ClusterTaskEvent? Event,bool Pending)> VerifyAsync(string body,DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        try
        {
            if(body.Length>ClusterReconciliationRepository.MaximumPayloadCharacters/2) return(null,false);
            var signed=JsonSerializer.Deserialize<SignedTaskOwnershipPlan>(body,Json);
            if(signed?.Data is null) return(null,false);
            var e=JsonSerializer.Deserialize<ClusterTaskEvent>(signed.Data,Json);
            if(e is null || !ClusterTaskMessageVersions.IsSupported(e.Version) || e.EventId==Guid.Empty || e.AuthorPeerId==Guid.Empty) return(null,false);
            await using var cluster=Sql.Command("SELECT id FROM cluster_identity WHERE singleton=true",c,tx);
            if(await cluster.ScalarGuidAsync(ct)!=e.ClusterId) return(null,false);
            await using var admission=Sql.Command("SELECT public_key FROM cluster_peer_admissions WHERE cluster_id=@cluster AND peer_id=@peer AND revoked_at IS NULL",c,tx);
            admission.Parameters.AddWithValue("cluster",e.ClusterId);admission.Parameters.AddWithValue("peer",e.AuthorPeerId);
            var key=e.AuthorPeerId==identity.PeerId?identity.PublicKey:await admission.ExecuteScalarAsync(ct) as string;
            if(key is null)
            {
                await using var revoked=Sql.Command("SELECT EXISTS(SELECT 1 FROM cluster_peer_admissions WHERE cluster_id=@cluster AND peer_id=@peer AND revoked_at IS NOT NULL)",c,tx);
                revoked.Parameters.AddWithValue("cluster",e.ClusterId);revoked.Parameters.AddWithValue("peer",e.AuthorPeerId);
                return(null,!await revoked.ScalarBoolAsync(ct));
            }
            return ClusterPeerIdentityStore.Verify(key,System.Text.Encoding.UTF8.GetBytes(signed.Data),Convert.FromBase64String(signed.Signature))?(e,false):(null,false);
        }
        catch(Exception ex) when(ex is JsonException or FormatException or ArgumentException) {return(null,false);}
    }
    internal async Task<ClusterTaskEvent?> CaptureAsync(Guid id,DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        ClusterTaskSnapshot snapshot;Guid cluster,previous;long epoch;bool freezeSettings;
        await using(var read=Sql.Command("""
          SELECT w.library_id,w.media_path,w.media_key,e.library_name,e.flow_definition,e.attempt,e.revision,e.queued_at,w.lease_epoch,w.source_sha,w.before_metadata,w.plugin_configuration_snapshot,w.media_policy,o.cluster_id,o.last_event_id,o.epoch
          FROM execution_work w JOIN executions e ON e.id=w.execution_id JOIN task_ownership o ON o.execution_id=e.id
          WHERE e.id=@id AND e.state IN ('queued','running') AND o.owner_peer_id=@peer AND o.state IN ('active','frozen')
            AND NOT w.publication_requested AND w.published_path IS NULL
            AND NOT EXISTS(SELECT 1 FROM pending_source_writes p WHERE p.execution_id=e.id AND p.state IN ('waiting','claimed'))
            AND NOT EXISTS(SELECT 1 FROM execution_steps s WHERE s.execution_id=e.id AND s.completed_at IS NULL AND NOT s.idempotent)
            AND NOT EXISTS(SELECT 1 FROM execution_waits x WHERE x.execution_id=e.id AND x.state='waiting')
          """,c,tx))
        {
            read.Parameters.AddWithValue("id",id);read.Parameters.AddWithValue("peer",identity.PeerId);await using var r=await read.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct)) return null;
            snapshot=new(id,r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetString(3),JsonSerializer.Deserialize<FlowDefinition>(r.GetString(4),Json)!,
                r.GetInt32(5),r.GetInt32(6),r.GetFieldValue<DateTimeOffset>(7),r.GetInt64(8),r.IsDBNull(9)?null:r.GetString(9),
                r.IsDBNull(10)?null:JsonSerializer.Deserialize<JsonElement>(r.GetString(10)),[],[],new Dictionary<string,string>(),
                r.IsDBNull(11)?new Dictionary<string,IReadOnlyDictionary<string,JsonElement>>():settings.ReadSnapshot((byte[])r[11]),r.GetString(12));
            freezeSettings=r.IsDBNull(11);cluster=r.GetGuid(13);previous=r.GetGuid(14);epoch=r.GetInt64(15);
        }
        if(!keys.HasKey(cluster))
        {
            await using var standalone=Sql.Command("SELECT NOT established FROM cluster_identity WHERE singleton=true",c,tx);
            if(await standalone.ScalarBoolAsync(ct)) keys.GetOrCreate(cluster);else return null;
        }
        if(freezeSettings)
        {
            var frozen=await settings.ResolveEffectiveAsync(snapshot.Flow.Id,snapshot.Flow.Nodes.Select(n=>n.Component.PluginId).Where(p=>p!="base"),c,tx,ct);
            await using var store=Sql.Command("UPDATE execution_work SET plugin_configuration_snapshot=@settings WHERE execution_id=@id AND plugin_configuration_snapshot IS NULL",
                c,tx);store.Parameters.AddWithValue("settings",SqlKind.Bytes,settings.ProtectSnapshot(frozen));store.Parameters.AddWithValue("id",id);
                await store.ExecuteNonQueryAsync(ct);snapshot=snapshot with {PluginConfigurations=frozen};
        }
        var steps=new List<ClusterTaskStep>();
        await using(var read=Sql.Command("SELECT node_id,invocation_id,completed_revision,started_at,checkpoint,idempotent FROM execution_steps WHERE execution_id=@id AND completed_at IS NOT NULL ORDER BY completed_revision LIMIT 1001",
            c,tx))
        {read.Parameters.AddWithValue("id",id);await using var r=await read.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct)) steps.Add(new(r.GetString(0),
            r.GetGuid(1),r.GetInt32(2),r.GetFieldValue<DateTimeOffset>(3),JsonSerializer.Deserialize<RuntimeCheckpoint>(r.GetString(4),Json)!,r.GetBoolean(5)));}
        if(steps.Count>1000 || steps.LastOrDefault()?.Checkpoint.Context.DirectSource is not null) return null;
        var blobs=new List<ClusterTaskArtifact>();var inputs=new Dictionary<string,string>();
        await using(var read=Sql.Command("SELECT sha256,byte_length FROM execution_artifacts WHERE execution_id=@id LIMIT 1001",c,tx))
        {read.Parameters.AddWithValue("id",id);await using var r=await read.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct)) blobs.Add(new(r.GetString(0),r.GetInt64(1)));}
        if(blobs.Count>1000) return null;
        await using(var read=Sql.Command("SELECT input_path,sha256 FROM execution_inputs WHERE execution_id=@id",c,tx))
        {read.Parameters.AddWithValue("id",id);await using var r=await read.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct)) inputs.Add(r.GetString(0),r.GetString(1));}
        snapshot=snapshot with {Steps=steps,Artifacts=blobs,Inputs=inputs};
        var plain=JsonSerializer.SerializeToUtf8Bytes(snapshot,Json);if(plain.Length>192*1024) return null;
        return ProtectSnapshot(cluster,id,epoch,previous,snapshot);
    }
    internal ClusterTaskEvent ProtectSnapshot(Guid cluster,Guid id,long epoch,Guid previous,ClusterTaskSnapshot snapshot)
    {
        if(!keys.HasKey(cluster)) keys.GetOrCreate(cluster);
        var plain=JsonSerializer.SerializeToUtf8Bytes(snapshot,Json);
        return new(ClusterTaskMessageVersions.Current,Guid.NewGuid(),cluster,identity.PeerId,"checkpoint",id,epoch,previous,Snapshot:ClusterPayloadProtection.Protect(keys.GetRequired(cluster),
            plain,"task-checkpoint/"+id.ToString("D")),Revision:snapshot.Revision);
    }
    public ClusterTaskSnapshot Decode(ClusterTaskEvent e)
    {
        var data=ClusterPayloadProtection.Unprotect(keys.GetRequired(e.ClusterId),e.Snapshot!,"task-checkpoint/"+e.ExecutionId.ToString("D"));
        var value=JsonSerializer.Deserialize<ClusterTaskSnapshot>(data,Json)??throw new InvalidDataException("Checkpoint absent.");
        if(value.ExecutionId!=e.ExecutionId || value.Revision!=e.Revision || value.Artifacts.Count>1000 || value.Steps.Count>1000
            || value.Artifacts.Any(a=>!RuntimeArtifactStore.ValidDigest(a.Sha256)||a.Bytes<=0) || value.Artifacts.Select(a=>a.Sha256).Distinct().Count()!=value.Artifacts.Count
            || value.SourceSha256 is not null && !value.Artifacts.Any(a=>a.Sha256==value.SourceSha256)
            || value.Steps.Any(s=>s.Checkpoint.Context.DirectSource is not null
                || !value.Artifacts.Any(a=>a.Sha256==s.Checkpoint.Context.CurrentSha256))) throw new InvalidDataException("Checkpoint incomplet.");
        return value;
    }
    internal async Task MarkDirtyAsync(Guid[] ids,DbConnection c,DbTransaction tx,CancellationToken ct)
    {await using var cmd=Sql.Command("INSERT INTO task_checkpoint_dirty(execution_id) SELECT execution_id FROM execution_work WHERE execution_id=ANY(@ids) ON CONFLICT DO NOTHING",
        c,tx);cmd.Parameters.AddWithValue("ids",ids);await cmd.ExecuteNonQueryAsync(ct);}
    internal async Task SaveAsync(Guid id,DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        var e=await CaptureAsync(id,c,tx,ct);if(e is null) return;
        var body=Sign(e);await ClusterReconciliationRepository.SetRegisterInTransactionAsync(Prefix+e.EventId.ToString("N"),body,c,tx,ct);
        await StoreAsync(e,body,c,tx,ct);
        await using var clean=Sql.Command("DELETE FROM task_checkpoint_dirty WHERE execution_id=@id",c,tx);clean.Parameters.AddWithValue("id",id);await clean.ExecuteNonQueryAsync(ct);
        await using var applied=Sql.Command("DELETE FROM task_coordination_inbox WHERE register_key=@key AND body=@body",c,tx);
            applied.Parameters.AddWithValue("key",Prefix+e.EventId.ToString("N"));applied.Parameters.AddWithValue("body",body);
            await applied.ExecuteNonQueryAsync(ct);
    }
    internal static async Task StoreAsync(ClusterTaskEvent e,string body,DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        await using var cmd=Sql.Command("INSERT INTO task_checkpoints(execution_id,epoch,revision,author_peer_id,body) VALUES(@id,@epoch,@revision,@peer,@body) ON CONFLICT(execution_id,epoch) DO UPDATE SET revision=excluded.revision,author_peer_id=excluded.author_peer_id,body=excluded.body WHERE excluded.revision>=task_checkpoints.revision",
            c,tx);
        cmd.Parameters.AddWithValue("id",e.ExecutionId);cmd.Parameters.AddWithValue("epoch",e.Epoch);cmd.Parameters.AddWithValue("revision",e.Revision);
            cmd.Parameters.AddWithValue("peer",e.AuthorPeerId);cmd.Parameters.AddWithValue("body",body);await cmd.ExecuteNonQueryAsync(ct);
    }
    internal async Task<bool> HasFilesAsync(ClusterTaskSnapshot value,CancellationToken ct)
    {
        foreach(var a in value.Artifacts)
        {
            if(!await artifacts.IsCachedAsync(value.ExecutionId,a.Sha256,a.Bytes,ct)) return false;
        }
        return true;
    }
    internal async Task ImportAsync(ClusterTaskSnapshot value,Guid sourceNode,DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        await using(var execution=Sql.Command("""
          INSERT INTO executions(id,media,library_name,flow_name,flow_version,flow_definition,state,attempt,revision,queued_at,detail)
          VALUES(@id,@path,@libraryName,@flowName,@version,@flow,'queued',@attempt,@revision,@queued,'Reçu à un checkpoint durable ; attribution locale requise.')
          ON CONFLICT(id) DO UPDATE SET state='queued',node_id=NULL,revision=MAX(executions.revision,@revision)+1,detail='Reçu à un checkpoint durable ; attribution locale requise.',finished_at=NULL,updated_at=clock_timestamp();
          INSERT INTO execution_work(execution_id,library_id,media_path,media_key,source_node_id,lease_epoch,source_sha,before_metadata,plugin_configuration_snapshot,media_policy)
          VALUES(@id,@library,@path,@key,@source,@epoch,@sha,@before,@settings,@policy)
          ON CONFLICT(execution_id) DO UPDATE SET worker_node_id=NULL,source_node_id=@source,lease_until=NULL,lease_epoch=@epoch,source_sha=@sha,handoff_pending=false,handoff_selector=NULL,previous_worker_node_id=NULL,available_at=clock_timestamp(),before_metadata=@before,plugin_configuration_snapshot=@settings;
          DELETE FROM execution_steps WHERE execution_id=@id AND completed_at IS NULL; DELETE FROM execution_inputs WHERE execution_id=@id;
          """,c,tx))
        {
            execution.Parameters.AddWithValue("id",value.ExecutionId);execution.Parameters.AddWithValue("path",
                RuntimeRepository.NormalizePath(value.MediaPath));execution.Parameters.AddWithValue("libraryName",value.LibraryName);
                execution.Parameters.AddWithValue("flowName",value.Flow.Name);execution.Parameters.AddWithValue("version",value.Flow.Version);
                execution.Parameters.AddWithValue("flow",SqlKind.Json,JsonSerializer.Serialize(value.Flow,Json));
            execution.Parameters.AddWithValue("attempt",value.Attempt);execution.Parameters.AddWithValue("revision",value.Revision);
                execution.Parameters.AddWithValue("queued",value.QueuedAt);execution.Parameters.AddWithValue("library",value.LibraryId);
                execution.Parameters.AddWithValue("key",value.MediaKey);execution.Parameters.AddWithValue("source",sourceNode);
                execution.Parameters.AddWithValue("epoch",value.LeaseEpoch+1);execution.Parameters.AddWithValue("sha",
                (object?)value.SourceSha256??DBNull.Value);execution.Parameters.AddWithValue("before",SqlKind.Json,
                (object?)value.BeforeMetadata?.GetRawText()??DBNull.Value);execution.Parameters.AddWithValue("settings",SqlKind.Bytes,
                settings.ProtectSnapshot(value.PluginConfigurations));execution.Parameters.AddWithValue("policy",value.MediaPolicy);
                await execution.ExecuteNonQueryAsync(ct);
        }
        foreach(var a in value.Artifacts)
        {
            await using var cmd=Sql.Command("INSERT INTO execution_artifacts(execution_id,sha256,byte_length) VALUES(@id,@sha,@bytes) ON CONFLICT DO NOTHING",c,
                tx);cmd.Parameters.AddWithValue("id",value.ExecutionId);cmd.Parameters.AddWithValue("sha",a.Sha256);cmd.Parameters.AddWithValue("bytes",
                a.Bytes);await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach(var step in value.Steps)
        {
            await using var cmd=Sql.Command("INSERT INTO execution_steps(execution_id,node_id,invocation_id,started_at,completed_at,completed_revision,idempotent,checkpoint) VALUES(@id,@node,@invocation,@started,@started,@revision,@safe,@checkpoint) ON CONFLICT(execution_id,node_id) DO UPDATE SET invocation_id=excluded.invocation_id,started_at=excluded.started_at,completed_at=excluded.completed_at,completed_revision=excluded.completed_revision,idempotent=excluded.idempotent,checkpoint=excluded.checkpoint,command_logs=CASE WHEN execution_steps.invocation_id=excluded.invocation_id THEN execution_steps.command_logs ELSE '[]' END",
                c,tx);cmd.Parameters.AddWithValue("id",value.ExecutionId);cmd.Parameters.AddWithValue("node",step.NodeId);
                cmd.Parameters.AddWithValue("invocation",step.InvocationId);cmd.Parameters.AddWithValue("started",step.StartedAt);
                cmd.Parameters.AddWithValue("revision",step.Revision);cmd.Parameters.AddWithValue("safe",step.Idempotent);
                cmd.Parameters.AddWithValue("checkpoint",SqlKind.Json,JsonSerializer.Serialize(step.Checkpoint,Json));await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach(var input in value.Inputs)
        {
            await using var cmd=Sql.Command("INSERT INTO execution_inputs(execution_id,input_path,sha256) VALUES(@id,@path,@sha)",c,tx);
                cmd.Parameters.AddWithValue("id",value.ExecutionId);cmd.Parameters.AddWithValue("path",RuntimeRepository.NormalizePath(input.Key));
                cmd.Parameters.AddWithValue("sha",input.Value);await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
