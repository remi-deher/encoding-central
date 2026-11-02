using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Nodes;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Nodes;
namespace Encoding.Api.Executions;

public sealed class ClusterTaskCoordinator(DbDataSource db,ClusterPeerIdentityStore identity,ClusterReconciliationHealth health,TaskOwnershipRepository ownership,
 TaskCheckpointRepository checkpoints,TaskCoordinationProjection projection,ClusterTaskTransport transport,ClusterSourceWriteBarrier barrier,
     LocalNodeRegistry local,RuntimeEligibility eligibility,NodeGroupRepository groups,TimeProvider? timeProvider=null)
{
    private readonly TimeProvider clock=timeProvider??TimeProvider.System;
    private static string Digest(string value)=>Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    internal async Task AppendAsync(ClusterTaskEvent value,DbConnection c,DbTransaction tx,CancellationToken ct)
    {await ClusterReconciliationRepository.SetRegisterInTransactionAsync(TaskCheckpointRepository.Prefix+value.EventId.ToString("N"),checkpoints.Sign(value),c,tx,ct);}
    internal async Task<ClusterTaskPeerReply> ControlAsync(Guid peer,string kind,Guid id,int? revision,CancellationToken ct)
    {
        try {return await transport.CallAsync(peer,kind,JsonSerializer.Serialize(new ClusterTaskControlRequest(id,revision),TaskCheckpointRepository.Json),
            ct)??new(2,Guid.Empty,peer,Guid.Empty,false,Detail:"Propriétaire injoignable.");}
        catch(Exception ex) when(ex is HttpRequestException or IOException or InvalidOperationException or OperationCanceledException or JsonException)
        {return new(2,Guid.Empty,peer,Guid.Empty,false,Detail:"Action distante non confirmée ; réconcilier le propriétaire.");}
    }
    public async Task<ClusterTaskResult> MoveAsync(Guid id,Guid target,CancellationToken ct)
    {
        if(target==Guid.Empty||target==identity.PeerId) return new(false,"Choisir un autre serveur.");
        if(!await barrier.VerifyAsync(ct)) return new(false,"Déplacement refusé : synchronisation indisponible.");
        await ownership.PrepareClaimsAsync(ct);if(await barrier.CheckAsync(ct) is {} reason) return new(false,reason);
        var transfer=Guid.NewGuid();ClusterTaskEvent proposal;string signed;
        await using(var c=await db.OpenConnectionAsync(ct))
        await using(var tx=await c.BeginTransactionAsync(ct))
        {
            proposal=(await checkpoints.CaptureAsync(id,c,tx,ct))!;
            if(proposal is null) return new(false,"Checkpoint non transférable : effet incertain, publication ou attente en cours.");
            await using(var freeze=Sql.Command("UPDATE task_ownership SET state='frozen' WHERE execution_id=@id AND owner_peer_id=@peer AND state='active';",c,tx))
            {freeze.Parameters.AddWithValue("id",id);freeze.Parameters.AddWithValue("peer",identity.PeerId);
                if(await freeze.ExecuteNonQueryAsync(ct)!=1) return new(false,"Ce serveur n’est pas propriétaire actif.");}
            proposal=proposal with {Kind="proposal",TargetPeerId=target,TransferId=transfer};signed=checkpoints.Sign(proposal);
            await InsertTransferAsync(proposal,signed,"proposed",null,c,tx,ct);
            await using var fence=Sql.Command("UPDATE execution_work SET worker_node_id=NULL,lease_until=NULL,lease_epoch=lease_epoch+1 WHERE execution_id=@id; DELETE FROM disk_locks WHERE execution_id=@id; UPDATE media_reservations SET lease_epoch=(SELECT lease_epoch FROM execution_work WHERE execution_id=@id),lease_until='infinity' WHERE execution_id=@id",
                c,tx);fence.Parameters.AddWithValue("id",id);await fence.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);
        }
        try
        {
            var reply=await transport.CallAsync(target,"receive",signed,ct);
            if(reply is not {Success:true,Payload:not null}) {await AbortAsync(proposal,ct);return new(false,reply?.Detail??"Accusé absent : la source conserve la tâche.",transfer);}
            await using(var c=await db.OpenConnectionAsync(ct))
            await using(var tx=await c.BeginTransactionAsync(ct))
            {
                var(receipt,_)=await checkpoints.VerifyAsync(reply.Payload,c,tx,ct);
                if(receipt is null||receipt.Kind!="receipt"||receipt.AuthorPeerId!=target||receipt.TransferId!=transfer||receipt.ExecutionId!=id||receipt.Epoch!=proposal.Epoch||receipt.PreviousEventId!=proposal.PreviousEventId||receipt.Snapshot!=Digest(signed)) throw new InvalidDataException("Accusé invalide.");
                await using(var current=Sql.Command("SELECT EXISTS(SELECT 1 FROM task_ownership WHERE execution_id=@id AND owner_peer_id=@owner AND epoch=@epoch AND last_event_id=@previous AND state='frozen' AND EXISTS(SELECT 1 FROM executions e WHERE e.id=@id AND e.revision=@revision AND e.state IN ('queued','running')))",
                    c,tx)) {current.Parameters.AddWithValue("id",id);current.Parameters.AddWithValue("owner",identity.PeerId);
                    current.Parameters.AddWithValue("epoch",proposal.Epoch);current.Parameters.AddWithValue("previous",proposal.PreviousEventId);
                    current.Parameters.AddWithValue("revision",proposal.Revision);
                    if(!await current.ScalarBoolAsync(ct)) throw new InvalidOperationException("La propriété a changé pendant le transfert.");}
                await using var ack=Sql.Command("UPDATE task_transfers SET receipt=@receipt WHERE id=@id AND state='proposed'",c,tx);
                    ack.Parameters.AddWithValue("id",transfer);ack.Parameters.AddWithValue("receipt",reply.Payload);
                    if(await ack.ExecuteNonQueryAsync(ct)!=1) throw new InvalidOperationException("Proposition déjà close.");
                var activation=proposal with {EventId=Guid.NewGuid(),Kind="activate",Epoch=proposal.Epoch+1,Snapshot=null};
                await AppendAsync(activation,c,tx,ct);await tx.CommitAsync(ct);
            }
            // Once activation is committed, NEVER unfreeze the old owner, even if delivery is lost.
            await projection.ProjectAsync(ct);
            await using var find=db.CreateCommand("SELECT value FROM cluster_sync_registers WHERE register_key LIKE 'tasks/v2/%' AND json_extract(json_extract(value,'$.data'),'$.kind')='activate' AND json_extract(json_extract(value,'$.data'),'$.transferId')=@transfer ORDER BY sequence DESC LIMIT 1");
                find.Parameters.AddWithValue("transfer",transfer.ToString("D"));
            var activationBody=await find.ExecuteScalarAsync(ct) as string;
            if(activationBody is not null) {try {await transport.CallAsync(target,"activate",activationBody,ct);
                } catch(Exception ex) when(ex is HttpRequestException or IOException or OperationCanceledException) { }}
            return new(true,"Activation journalisée ; la cible reprend après réception et validation.",transfer);
        }
        catch(Exception ex) when(ex is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or OperationCanceledException or JsonException)
        {
            // AbortAsync verifies durable ownership and refuses to undo an emitted activation.
            await AbortAsync(proposal,CancellationToken.None);return new(false,"Liaison interrompue ; consulter l’état durable du transfert.",transfer);
        }
    }
    internal Task AbortExpiredAsync(ClusterTaskEvent proposal,CancellationToken ct)=>AbortAsync(proposal,ct);
    private async Task AbortAsync(ClusterTaskEvent proposal,CancellationToken ct)
    {
        await using var c=await db.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        await using var emitted=Sql.Command("SELECT EXISTS(SELECT 1 FROM cluster_sync_registers WHERE register_key LIKE 'tasks/v2/%' AND json_extract(json_extract(value,'$.data'),'$.kind')='activate' AND json_extract(json_extract(value,'$.data'),'$.transferId')=@transfer)",
            c,tx);emitted.Parameters.AddWithValue("transfer",proposal.TransferId!.Value.ToString("D"));if(await emitted.ScalarBoolAsync(ct)) return;
        await AppendAsync(proposal with {Kind="abort",EventId=Guid.NewGuid(),Snapshot=null},c,tx,ct);await tx.CommitAsync(ct);await projection.ProjectAsync(ct);
    }
    internal static async Task InsertTransferAsync(ClusterTaskEvent proposal,string signed,string state,Guid? source,DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        await using var insert=Sql.Command("INSERT INTO task_transfers(id,execution_id,source_peer_id,target_peer_id,epoch,previous_event_id,snapshot,state,target_source_node_id) VALUES(@id,@execution,@from,@to,@epoch,@previous,@snapshot,@state,@source) ON CONFLICT(id) DO NOTHING",
            c,tx);
        insert.Parameters.AddWithValue("id",proposal.TransferId!.Value);insert.Parameters.AddWithValue("execution",proposal.ExecutionId);
            insert.Parameters.AddWithValue("from",proposal.AuthorPeerId);insert.Parameters.AddWithValue("to",proposal.TargetPeerId!.Value);
            insert.Parameters.AddWithValue("epoch",proposal.Epoch);insert.Parameters.AddWithValue("previous",proposal.PreviousEventId);
            insert.Parameters.AddWithValue("snapshot",signed);insert.Parameters.AddWithValue("state",state);insert.Parameters.AddWithValue("source",
            (object?)source??DBNull.Value);await insert.ExecuteNonQueryAsync(ct);
    }
    internal async Task<Guid?> ResolveLocalNodeIdAsync(CancellationToken ct)
    {
        if(local.NodeId is {} known) return known;
        await using var c=await db.OpenConnectionAsync(ct);
        await using(var cmd=Sql.Command("SELECT node_id FROM cluster_peer_admissions WHERE peer_id=@local AND revoked_at IS NULL AND node_id IS NOT NULL LIMIT 1",c))
        {
            cmd.Parameters.AddWithValue("local",identity.PeerId);
            if(await cmd.ScalarGuidAsync(ct) is {} admitted) {await local.RegisterAsync(admitted,ct);return admitted;}
        }
        await using(var cmd=Sql.Command("SELECT n.id FROM cluster_nodes n WHERE n.deleted_at IS NULL AND 'server'=ANY(n.roles) AND NOT EXISTS(SELECT 1 FROM cluster_peer_admissions a WHERE a.node_id=n.id AND a.peer_id<>@local AND a.revoked_at IS NULL) ORDER BY n.created_at LIMIT 2",
            c))
        {
            cmd.Parameters.AddWithValue("local",identity.PeerId);
            var candidates=new List<Guid>();
            await using var r=await cmd.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) candidates.Add(r.GetGuid(0));
            if(candidates.Count==1) {await local.RegisterAsync(candidates[0],ct);return candidates[0];}
        }
        return null;
    }
    internal async Task<Guid?> EligibleSourceAsync(ClusterTaskSnapshot value,CancellationToken ct)
    {
        if(await ResolveLocalNodeIdAsync(ct) is not {} node) return null;
        await using var cmd=db.CreateCommand("SELECT n.capabilities,n.inventory FROM cluster_nodes n JOIN library_node_accesses a ON a.node_id=n.id WHERE n.id=@node AND n.deleted_at IS NULL AND n.accepts_work AND 'worker'=ANY(n.roles) AND n.last_seen_at>clock_timestamp()-interval '45 seconds' AND a.library_id=@library AND a.mode='direct' AND a.is_enabled AND a.can_read AND a.can_write AND a.local_path IS NOT NULL");
            cmd.Parameters.AddWithValue("node",node);cmd.Parameters.AddWithValue("library",value.LibraryId);await using var r=await cmd.ExecuteReaderAsync(ct);
            if(!await r.ReadAsync(ct)) return null;
        var caps=JsonSerializer.Deserialize<NodeToolCapability[]>(r.GetString(0),TaskCheckpointRepository.Json)??[];
            var inventory=r.IsDBNull(1)?null:JsonSerializer.Deserialize<NodeInventory>(r.GetString(1),TaskCheckpointRepository.Json);
        var cp=value.Steps.LastOrDefault()?.Checkpoint;var selector=await groups.GetNodeIdentityAsync(node,ct);
        return eligibility.CanRun(value.Flow,caps,node,cp?.NextNodeId,cp is not null,cp?.Context,selector,inventory)?node:null;
    }
    public async Task<ClusterTaskPeerReply> ReceiveAsync(Guid peer,ClusterTaskPeerRequest request,CancellationToken ct)
    {
        await ownership.PrepareClaimsAsync(ct);ClusterTaskEvent proposal;
        await using(var c=await db.OpenConnectionAsync(ct))
        await using(var tx=await c.BeginTransactionAsync(ct))
        {
            var(value,_)=await checkpoints.VerifyAsync(request.Payload??"",c,tx,ct);
                if(value is null||value.AuthorPeerId!=peer||value.Kind!="proposal"||value.TargetPeerId!=identity.PeerId||value.TransferId is null) return Reply(false,
                "Proposition invalide.");proposal=value;
            await using var owner=Sql.Command("SELECT EXISTS(SELECT 1 FROM task_ownership WHERE execution_id=@id AND owner_peer_id=@peer AND epoch=@epoch AND last_event_id=@previous AND state='active')",
                c,tx);owner.Parameters.AddWithValue("id",proposal.ExecutionId);owner.Parameters.AddWithValue("peer",peer);owner.Parameters.AddWithValue("epoch",
                proposal.Epoch);owner.Parameters.AddWithValue("previous",proposal.PreviousEventId);if(!await owner.ScalarBoolAsync(ct)) return Reply(false,
                "Propriété à réconcilier.");
        }
        var snapshot=checkpoints.Decode(proposal);var source=await EligibleSourceAsync(snapshot,ct);if(source is null) return Reply(false,"Aucun accès direct local ou worker compatible.");
        if(!await checkpoints.HasFilesAsync(snapshot,ct)) foreach(var blob in snapshot.Artifacts) await transport.FetchAsync(peer,snapshot.ExecutionId,blob,ct);
        await using(var c=await db.OpenConnectionAsync(ct))
        await using(var tx=await c.BeginTransactionAsync(ct))
        {
            await using(var valid=Sql.Command("SELECT EXISTS(SELECT 1 FROM task_ownership WHERE execution_id=@id AND owner_peer_id=@peer AND epoch=@epoch AND last_event_id=@previous AND state='active') AND NOT EXISTS(SELECT 1 FROM task_transfer_aborts WHERE transfer_id=@transfer)",
                c,tx))
            {valid.Parameters.AddWithValue("id",proposal.ExecutionId);valid.Parameters.AddWithValue("peer",peer);valid.Parameters.AddWithValue("epoch",
                proposal.Epoch);valid.Parameters.AddWithValue("previous",proposal.PreviousEventId);valid.Parameters.AddWithValue("transfer",
                proposal.TransferId!.Value);if(!await valid.ScalarBoolAsync(ct)) return Reply(false,
                "Proposition abandonnée ou propriété modifiée pendant le transfert.");}
            await InsertTransferAsync(proposal,request.Payload!,"received",source,c,tx,ct);
            var receipt=checkpoints.Sign(proposal with {EventId=Guid.NewGuid(),Kind="receipt",AuthorPeerId=identity.PeerId,Snapshot=Digest(request.Payload!)});
            await using var ack=Sql.Command("UPDATE task_transfers SET receipt=COALESCE(receipt,@receipt) WHERE id=@id AND state='received' RETURNING receipt",
                c,tx);ack.Parameters.AddWithValue("id",proposal.TransferId!.Value);ack.Parameters.AddWithValue("receipt",receipt);
                var saved=await ack.ExecuteScalarAsync(ct) as string;
            await tx.CommitAsync(ct);return saved is null?Reply(false,"Réception déjà close."):Reply(true,null,saved);
        }
        ClusterTaskPeerReply Reply(bool success,string? detail,string? payload=null)=>new(2,request.ClusterId,identity.PeerId,request.Nonce,success,payload,detail);
    }
    private async Task<bool> TwoServerRecoveryAsync(CancellationToken ct)
    {
        await using var cmd=db.CreateCommand("SELECT count(DISTINCT a.peer_id) FROM cluster_peer_admissions a JOIN cluster_identity i ON i.id=a.cluster_id LEFT JOIN cluster_nodes n ON n.id=a.node_id WHERE a.revoked_at IS NULL AND a.peer_id<>@local AND (a.node_id IS NULL OR n.deleted_at IS NULL AND 'server'=ANY(n.roles))");
        cmd.Parameters.AddWithValue("local",identity.PeerId);return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct))<=1;
    }
    public async Task<ClusterTaskResult> ReadmitAsync(Guid peer,CancellationToken ct)
    {
        var localVector=await new ClusterReconciliationRepository(db).GetVectorAsync(ct);
        if(!await barrier.VerifyAsync(ct)||!health.HasFreshCatchUp(peer,TimeSpan.FromSeconds(45))) return new(false,"Réconcilier directement le serveur revenu avant de lever sa neutralisation.");
        await ownership.PrepareClaimsAsync(ct);
        await projection.ProjectAsync(ct);
        var ready=await transport.CallAsync(peer,"ready",null,ct);
        if(ready is not {Success:true,Payload:not null}) return new(false,"Le serveur revenu doit projeter son journal avant réadmission.");
        var remoteVector=JsonSerializer.Deserialize<Dictionary<Guid,long>>(ready.Payload,TaskCheckpointRepository.Json);
        if(remoteVector is null||localVector.Any(p=>remoteVector.GetValueOrDefault(p.Key)<p.Value)) return new(false,"Rattrapage incomplet du serveur revenu.");
        await using var c=await db.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        await using var read=Sql.Command("SELECT n.event_id,i.id FROM task_neutralizations n CROSS JOIN cluster_identity i WHERE n.peer_id=@peer AND n.author_peer_id=@local AND n.released_at IS NULL",
            c,tx);read.Parameters.AddWithValue("peer",peer);read.Parameters.AddWithValue("local",identity.PeerId);
            await using var r=await read.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct)) return new(false,"Neutralisation active de ce serveur requise.");
            var proof=r.GetGuid(0);var cluster=r.GetGuid(1);await r.DisposeAsync();
        await AppendAsync(new(2,Guid.NewGuid(),cluster,identity.PeerId,"readmit",Guid.Empty,0,Guid.Empty,SubjectPeerId:peer,RecordedAt:clock.GetUtcNow(),
            NeutralizationId:proof),c,tx,ct);await tx.CommitAsync(ct);await projection.ProjectAsync(ct);return new(true,
            "Serveur réconcilié ; neutralisation levée. Les tâches cédées restent à leur nouveau propriétaire.");
    }
    public async Task<ClusterTaskResult> NeutralizeAsync(Guid peer,ClusterTaskNeutralizeRequest request,CancellationToken ct)
    {
        if(!await TwoServerRecoveryAsync(ct)) return new(false,"À trois serveurs ou plus, la reprise exige un témoin ou une décision majoritaire dédiée (non activée).");
        if(peer==identity.PeerId||!request.ConfirmedStopped||request.Reason is not {Length:>=8 and <=1000}) return new(false,
            "Confirmer l’arrêt effectif de l’installation et de ses workers, avec une justification.");
        await using var c=await db.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);Guid cluster;
        await using(var read=Sql.Command("SELECT cluster_id FROM cluster_peer_admissions WHERE peer_id=@peer AND revoked_at IS NULL",c,
            tx)) {read.Parameters.AddWithValue("peer",peer);if(await read.ScalarGuidAsync(ct) is not {} id) return new(false,"Serveur non admis.");cluster=id;}
        await AppendAsync(new(2,Guid.NewGuid(),cluster,identity.PeerId,"neutralize",Guid.Empty,0,Guid.Empty,SubjectPeerId:peer,RecordedAt:clock.GetUtcNow(),
            Reason:request.Reason),c,tx,ct);await tx.CommitAsync(ct);await projection.ProjectAsync(ct);return new(true,
            "Neutralisation attestée et journalisée ; délai de grâce de 135 secondes avant reprise.");
    }
    public async Task<ClusterTaskResult> ForceSourceRetryAsync(Guid id,ClusterTaskForceSourceRetryRequest request,CancellationToken ct)
    {
        if(!await TwoServerRecoveryAsync(ct)) return new(false,"La reprise forcée depuis la source est disponible uniquement dans un cluster à deux serveurs.");
        if(!request.ConfirmedStopped||string.IsNullOrWhiteSpace(request.Reason)||request.Reason.Trim().Length<8||request.Reason.Length>1000) return new(false,
            "Confirmer l’arrêt effectif de l’installation et de ses workers, avec une justification d’au moins huit caractères.");

        await projection.ProjectAsync(ct);
        await using(var released=db.CreateCommand("SELECT EXISTS(SELECT 1 FROM task_ownership o JOIN executions e ON e.id=o.execution_id JOIN execution_work w ON w.execution_id=e.id JOIN libraries l ON l.id=o.library_id WHERE o.execution_id=@id AND o.owner_peer_id=@local AND o.state='active' AND e.state='queued' AND e.revision=0 AND e.started_at IS NULL AND e.finished_at IS NULL AND w.published_path IS NULL AND NOT w.publication_requested AND l.is_active AND NOT l.is_deleted AND NOT EXISTS(SELECT 1 FROM task_quarantines q WHERE q.execution_id=e.id) AND NOT EXISTS(SELECT 1 FROM execution_steps s WHERE s.execution_id=e.id)"))
        {released.Parameters.AddWithValue("id",id);released.Parameters.AddWithValue("local",identity.PeerId);
            if(await released.ScalarBoolAsync(ct)) return new(true,"La tentative source est déjà libérée et en file.");}
        if(await IsQuarantinedCancelledBeforeStartAsync(id,ct))
            return await ForceCancelledSourceRetryAsync(id,request,ct);
        Guid oldOwner;DateTimeOffset? neutralizedAt=null;bool alreadyNeutralized=false;
        await using(var c=await db.OpenConnectionAsync(ct))
        await using(var tx=await c.BeginTransactionAsync(ct))
        {
            Guid library;string mediaKey;
            await using(var candidate=Sql.Command("SELECT o.library_id,o.media_key FROM task_ownership o JOIN executions e ON e.id=o.execution_id JOIN execution_work cw ON cw.execution_id=e.id JOIN task_quarantines q ON q.execution_id=o.execution_id JOIN libraries l ON l.id=o.library_id AND l.is_active AND NOT l.is_deleted WHERE o.execution_id=@id AND o.state='quarantined' AND e.state='queued' AND e.revision=0 AND e.started_at IS NULL AND e.finished_at IS NULL AND cw.published_path IS NULL AND NOT cw.publication_requested AND NOT EXISTS(SELECT 1 FROM execution_steps s WHERE s.execution_id=e.id) FOR UPDATE OF o,e,q",
                c,tx))
            {
                candidate.Parameters.AddWithValue("id",id);await using var r=await candidate.ExecuteReaderAsync(ct);
                if(!await r.ReadAsync(ct)) return new(false,"Seule une tentative source en quarantaine, en file, jamais démarrée et sans résultat publié peut être forcée.");
                library=r.GetGuid(0);mediaKey=r.GetString(1);
            }

            Guid root;
            await using(var chain=Sql.Command("WITH RECURSIVE conflicts(execution_id,depth) AS (SELECT winner_id,1 FROM task_quarantines WHERE execution_id=@id UNION ALL SELECT q.winner_id,c.depth+1 FROM conflicts c JOIN task_quarantines q ON q.execution_id=c.execution_id WHERE c.depth<32) SELECT c.execution_id FROM conflicts c WHERE NOT EXISTS(SELECT 1 FROM task_quarantines q WHERE q.execution_id=c.execution_id) ORDER BY c.depth DESC LIMIT 1",
                c,tx))
            {
                chain.Parameters.AddWithValue("id",id);if(await chain.ScalarGuidAsync(ct) is not {} rootId) return new(false,
                    "La tentative en quarantaine n’a plus de conflit propriétaire identifiable.");root=rootId;
            }

            string rootState;string? publishedPath;bool publicationRequested;string ownershipState;
            await using(var owner=Sql.Command("SELECT o.owner_peer_id,o.state,e.state,w.published_path,w.publication_requested FROM task_ownership o JOIN executions e ON e.id=o.execution_id LEFT JOIN execution_work w ON w.execution_id=e.id WHERE o.execution_id=@id AND o.library_id=@library AND o.media_key=@media",
                c,tx))
            {
                owner.Parameters.AddWithValue("id",root);owner.Parameters.AddWithValue("library",library);owner.Parameters.AddWithValue("media",mediaKey);
                    await using var r=await owner.ExecuteReaderAsync(ct);
                if(!await r.ReadAsync(ct)) return new(false,"Le conflit d’origine n’est plus présent dans cette bibliothèque.");
                oldOwner=r.GetGuid(0);ownershipState=r.GetString(1);rootState=r.GetString(2);publishedPath=r.IsDBNull(3)?null:r.GetString(3);publicationRequested=!r.IsDBNull(4)&&r.GetBoolean(4);
            }
            if(oldOwner==identity.PeerId||ownershipState is not ("active" or "quarantined")) return new(false,"Le conflit ne dépend pas d’un propriétaire distant neutralisable.");
            if(rootState=="succeeded"||publishedPath is not null||publicationRequested) return new(false,
                "Une publication est confirmée ou incertaine ; vérifiez le média avant toute nouvelle tentative.");

            await using(var sibling=Sql.Command("SELECT EXISTS(SELECT 1 FROM task_ownership o JOIN executions e ON e.id=o.execution_id LEFT JOIN execution_work w ON w.execution_id=e.id WHERE o.library_id=@library AND o.media_key=@media AND o.execution_id<>@candidate AND o.execution_id<>@root AND (e.state='succeeded' OR w.published_path IS NOT NULL OR COALESCE(w.publication_requested,FALSE) OR e.state IN ('queued','running','cancelling')))" ,
                c,tx))
            {
                sibling.Parameters.AddWithValue("library",library);sibling.Parameters.AddWithValue("media",mediaKey);
                    sibling.Parameters.AddWithValue("candidate",id);sibling.Parameters.AddWithValue("root",root);
                if(await sibling.ScalarBoolAsync(ct)) return new(false,
                    "Une autre tentative du même média peut encore produire ou avoir publié un résultat ; réconciliez-la avant de forcer la reprise.");
            }

            await using(var existing=Sql.Command("SELECT author_peer_id,recorded_at FROM task_neutralizations WHERE peer_id=@peer AND released_at IS NULL",c,tx))
            {
                existing.Parameters.AddWithValue("peer",oldOwner);await using var r=await existing.ExecuteReaderAsync(ct);
                if(await r.ReadAsync(ct))
                {
                    if(r.GetGuid(0)!=identity.PeerId) return new(false,"Ce serveur est déjà neutralisé par une autre décision ; faites converger les journaux avant de continuer.");
                    alreadyNeutralized=true;neutralizedAt=r.GetFieldValue<DateTimeOffset>(1);
                }
            }
            if(!alreadyNeutralized)
            {
                Guid cluster;
                await using(var admitted=Sql.Command("SELECT cluster_id FROM cluster_peer_admissions WHERE peer_id=@peer AND revoked_at IS NULL",c,tx))
                {admitted.Parameters.AddWithValue("peer",oldOwner);if(await admitted.ScalarGuidAsync(ct) is not {} clusterId) return new(false,
                    "Le serveur propriétaire n’est plus admis dans ce cluster.");cluster=clusterId;}
                var reason=request.Reason.Trim();
                if(reason.Length<8) return new(false,"La justification doit contenir au moins huit caractères.");
                neutralizedAt=clock.GetUtcNow();
                await AppendAsync(new(2,Guid.NewGuid(),cluster,identity.PeerId,"neutralize",Guid.Empty,0,Guid.Empty,SubjectPeerId:oldOwner,RecordedAt:neutralizedAt,Reason:reason),c,tx,ct);
            }
            await tx.CommitAsync(ct);
        }

        await projection.ProjectAsync(ct);
        var current=await ownership.GetAsync(id,ct);
        if(current?.OwnerPeerId==identity.PeerId&&current.State=="active") return new(true,"La tentative source est libérée et peut reprendre normalement.");
        var remaining=Math.Max(0,(int)Math.Ceiling((neutralizedAt!.Value+TaskCoordinationProjection.RecoveryGrace-clock.GetUtcNow()).TotalSeconds));
        return new(true,remaining==0?"Neutralisation attestée ; la projection libérera automatiquement la tentative source après convergence.":$"Neutralisation attestée et journalisée ; reprise automatique depuis la source après le délai de grâce restant ({remaining} s).");
    }
    private async Task<bool> IsQuarantinedCancelledBeforeStartAsync(Guid id,CancellationToken ct)
    {
        await using var command=db.CreateCommand("SELECT EXISTS(SELECT 1 FROM task_ownership o JOIN executions e ON e.id=o.execution_id JOIN task_quarantines q ON q.execution_id=e.id WHERE e.id=@id AND o.state='quarantined' AND e.state='cancelled' AND e.started_at IS NULL)");
        command.Parameters.AddWithValue("id",id);return await command.ScalarBoolAsync(ct);
    }
    private async Task<ClusterTaskResult> ForceCancelledSourceRetryAsync(Guid id,ClusterTaskForceSourceRetryRequest request,CancellationToken ct)
    {
        Guid oldOwner;Guid library;string mediaKey;string sourcePath;int attempt;DateTimeOffset? neutralizedAt=null;bool alreadyNeutralized=false;
        await using(var c=await db.OpenConnectionAsync(ct))
        await using(var tx=await c.BeginTransactionAsync(ct))
        {
            await using(var cancelled=Sql.Command("SELECT o.library_id,o.media_key,e.attempt FROM task_ownership o JOIN executions e ON e.id=o.execution_id JOIN task_quarantines q ON q.execution_id=e.id JOIN libraries l ON l.id=o.library_id AND l.is_active AND NOT l.is_deleted LEFT JOIN execution_work w ON w.execution_id=e.id WHERE e.id=@id AND o.state='quarantined' AND e.state='cancelled' AND e.started_at IS NULL AND e.finished_at IS NOT NULL AND w.published_path IS NULL AND NOT COALESCE(w.publication_requested,FALSE) AND NOT EXISTS(SELECT 1 FROM execution_steps s WHERE s.execution_id=e.id) FOR UPDATE OF o,e,q",
                c,tx))
            {
                cancelled.Parameters.AddWithValue("id",id);await using var r=await cancelled.ExecuteReaderAsync(ct);
                if(!await r.ReadAsync(ct)) return new(false,"Seule une exécution annulée avant démarrage, en quarantaine et sans publication peut repartir de la source.");
                library=r.GetGuid(0);mediaKey=r.GetString(1);attempt=r.GetInt32(2);
            }

            Guid root;
            await using(var chain=Sql.Command("WITH RECURSIVE conflicts(execution_id,depth) AS (SELECT winner_id,1 FROM task_quarantines WHERE execution_id=@id UNION ALL SELECT q.winner_id,c.depth+1 FROM conflicts c JOIN task_quarantines q ON q.execution_id=c.execution_id WHERE c.depth<32) SELECT c.execution_id FROM conflicts c WHERE NOT EXISTS(SELECT 1 FROM task_quarantines q WHERE q.execution_id=c.execution_id) ORDER BY c.depth DESC LIMIT 1",
                c,tx))
            {chain.Parameters.AddWithValue("id",id);if(await chain.ScalarGuidAsync(ct) is not {} rootId) return new(false,
                "La tentative en quarantaine n’a plus de conflit propriétaire identifiable.");root=rootId;}

            string rootState;string? publishedPath;bool publicationRequested;string ownershipState;
            await using(var owner=Sql.Command("SELECT o.owner_peer_id,o.state,e.state,w.published_path,w.publication_requested FROM task_ownership o JOIN executions e ON e.id=o.execution_id LEFT JOIN execution_work w ON w.execution_id=e.id WHERE o.execution_id=@id AND o.library_id=@library AND o.media_key=@media",
                c,tx))
            {
                owner.Parameters.AddWithValue("id",root);owner.Parameters.AddWithValue("library",library);owner.Parameters.AddWithValue("media",mediaKey);
                    await using var r=await owner.ExecuteReaderAsync(ct);
                if(!await r.ReadAsync(ct)) return new(false,"Le conflit d’origine n’est plus présent dans cette bibliothèque.");
                oldOwner=r.GetGuid(0);ownershipState=r.GetString(1);rootState=r.GetString(2);publishedPath=r.IsDBNull(3)?null:r.GetString(3);publicationRequested=!r.IsDBNull(4)&&r.GetBoolean(4);
            }
            if(ownershipState is not ("active" or "quarantined")) return new(false,"Le conflit ne dépend pas d’un propriétaire distant neutralisable.");
            if(oldOwner==identity.PeerId && rootState is not ("cancelled" or "failed")) return new(false,"Le conflit ne dépend pas d’un propriétaire distant neutralisable.");
            if(rootState=="succeeded"||publishedPath is not null||publicationRequested) return new(false,
                "Une publication est confirmée ou incertaine ; vérifiez le média avant toute nouvelle tentative.");

            await using(var sibling=Sql.Command("SELECT EXISTS(SELECT 1 FROM task_ownership o JOIN executions e ON e.id=o.execution_id LEFT JOIN execution_work w ON w.execution_id=e.id WHERE o.library_id=@library AND o.media_key=@media AND o.execution_id<>@candidate AND o.execution_id<>@root AND (e.state='succeeded' OR w.published_path IS NOT NULL OR COALESCE(w.publication_requested,FALSE) OR e.state IN ('queued','running','cancelling')))",
                c,tx))
            {sibling.Parameters.AddWithValue("library",library);sibling.Parameters.AddWithValue("media",mediaKey);sibling.Parameters.AddWithValue("candidate",
                id);sibling.Parameters.AddWithValue("root",root);if(await sibling.ScalarBoolAsync(ct)) return new(false,
                "Une autre tentative du même média peut encore produire ou avoir publié un résultat ; réconciliez-la avant de forcer la reprise.");}

            await using(var existingChild=Sql.Command("SELECT id,state FROM executions WHERE retry_of=@id",c,tx))
            {
                existingChild.Parameters.AddWithValue("id",id);await using var r=await existingChild.ExecuteReaderAsync(ct);
                if(await r.ReadAsync(ct))
                {
                    var child=r.GetGuid(0);var childState=r.GetString(1);await r.DisposeAsync();
                    if(childState is not ("cancelled" or "failed")) return new(false,$"Une tentative liée ({child:D}) existe déjà ; actualisez Activité avant de relancer.");
                    await using var unlink=Sql.Command("UPDATE executions SET retry_of=NULL WHERE id=@child",c,tx);
                    unlink.Parameters.AddWithValue("child",child);await unlink.ExecuteNonQueryAsync(ct);
                }
            }

            await using(var path=Sql.Command("SELECT relative_path FROM library_media WHERE library_id=@library AND upper(relative_path)=@media LIMIT 1",c,tx))
            {path.Parameters.AddWithValue("library",library);path.Parameters.AddWithValue("media",mediaKey);
                if(await path.ExecuteScalarAsync(ct) is not string source)return new(false,
                "Le fichier source n’est plus présent dans l’inventaire de la bibliothèque.");sourcePath=source;}

            if(oldOwner==identity.PeerId || rootState is "cancelled" or "failed")
            {
                alreadyNeutralized=true;neutralizedAt=clock.GetUtcNow()-TaskCoordinationProjection.RecoveryGrace;
            }
            else
            {
                await using(var existing=Sql.Command("SELECT author_peer_id,recorded_at FROM task_neutralizations WHERE peer_id=@peer AND released_at IS NULL",c,tx))
                {
                    existing.Parameters.AddWithValue("peer",oldOwner);await using var r=await existing.ExecuteReaderAsync(ct);
                    if(await r.ReadAsync(ct))
                    {
                        if(r.GetGuid(0)!=identity.PeerId) return new(false,"Ce serveur est déjà neutralisé par une autre décision ; faites converger les journaux avant de continuer.");
                        alreadyNeutralized=true;neutralizedAt=r.GetFieldValue<DateTimeOffset>(1);
                    }
                }
                if(!alreadyNeutralized)
                {
                    Guid cluster;
                    await using(var admitted=Sql.Command("SELECT cluster_id FROM cluster_peer_admissions WHERE peer_id=@peer AND revoked_at IS NULL",c,tx))
                    {admitted.Parameters.AddWithValue("peer",oldOwner);if(await admitted.ScalarGuidAsync(ct) is not {} clusterId) return new(false,
                        "Le serveur propriétaire n’est plus admis dans ce cluster.");cluster=clusterId;}
                    neutralizedAt=clock.GetUtcNow();
                    await AppendAsync(new(2,Guid.NewGuid(),cluster,identity.PeerId,"neutralize",Guid.Empty,0,Guid.Empty,SubjectPeerId:oldOwner,
                        RecordedAt:neutralizedAt,Reason:request.Reason.Trim()),c,tx,ct);
                }
            }

            var next=Guid.NewGuid();var detail="Nouvelle exécution créée depuis la source après interruption.";
            var availableAt=neutralizedAt!.Value+TaskCoordinationProjection.RecoveryGrace;
            await using(var create=Sql.Command("INSERT INTO executions(id,media,library_name,flow_name,flow_version,flow_definition,state,attempt,retry_of,detail) SELECT @next,@media,library_name,flow_name,flow_version,flow_definition,'queued',@attempt+1,id,@detail FROM executions WHERE id=@id AND state='cancelled' AND started_at IS NULL ON CONFLICT(retry_of) DO NOTHING RETURNING id",
                c,tx))
            {
                create.Parameters.AddWithValue("id",id);create.Parameters.AddWithValue("next",next);create.Parameters.AddWithValue("media",sourcePath);
                    create.Parameters.AddWithValue("attempt",attempt);create.Parameters.AddWithValue("detail",detail);
                if(await create.ScalarGuidAsync(ct) is not {} created)return new(false,"La tentative source n’a pas pu être créée ; actualisez Activité et réconciliez le cluster.");next=created;
            }
            await using(var work=Sql.Command("INSERT INTO execution_work(execution_id,library_id,media_path,media_key,available_at) VALUES(@next,@library,@path,@media,@available)",c,tx))
            {work.Parameters.AddWithValue("next",next);work.Parameters.AddWithValue("library",library);work.Parameters.AddWithValue("path",sourcePath);
                work.Parameters.AddWithValue("media",mediaKey);work.Parameters.AddWithValue("available",availableAt);await work.ExecuteNonQueryAsync(ct);}
            await using(var evt=Sql.Command("INSERT INTO execution_events(id,execution_id,revision,state,detail) VALUES(@event,@next,0,'queued',@detail)",c,tx))
            {evt.Parameters.AddWithValue("event",Guid.NewGuid());evt.Parameters.AddWithValue("next",next);evt.Parameters.AddWithValue("detail",detail);await evt.ExecuteNonQueryAsync(ct);}
            await ownership.CreateAsync([next],c,tx,ct);
            await TaskCoordinationProjection.DeduplicateAsync(c,tx,ct);
            await tx.CommitAsync(ct);
        }

        await projection.ProjectAsync(ct);
        var wait=Math.Max(0,(int)Math.Ceiling((neutralizedAt!.Value+TaskCoordinationProjection.RecoveryGrace-clock.GetUtcNow()).TotalSeconds));
        return new(true,wait==0?"Nouvelle tentative créée depuis la source et mise en file.":$"Nouvelle tentative créée depuis la source ; mise en file après le délai de grâce restant ({wait} s).");
    }
    public async Task<ClusterTaskResult> RecoverAsync(Guid id,CancellationToken ct)
    {
        if(!await TwoServerRecoveryAsync(ct)) return new(false,"La reprise à trois serveurs exige un témoin ou une décision majoritaire dédiée.");
        await ownership.ProjectAsync(ct);
        await projection.ProjectAsync(ct);
        await using(var pending=db.CreateCommand($"SELECT {TaskOwnershipRepository.BlockingPendingSql}")) if(await pending.ScalarBoolAsync(ct)) return new(false,
            "Rattrapage des projections requis avant reprise.");
        ClusterTaskEvent? saved=null;Guid neutralization;Guid oldOwner;DateTimeOffset at;
        await using(var c=await db.OpenConnectionAsync(ct))
        await using(var tx=await c.BeginTransactionAsync(ct))
        {
            await using var read=Sql.Command("SELECT p.body,n.event_id,o.owner_peer_id,n.recorded_at FROM task_ownership o JOIN task_neutralizations n ON n.peer_id=o.owner_peer_id AND n.released_at IS NULL AND n.author_peer_id=@local LEFT JOIN task_checkpoints p ON p.execution_id=o.execution_id AND p.epoch=o.epoch LEFT JOIN executions e ON e.id=o.execution_id WHERE (e.id IS NULL OR e.state IN ('queued','running')) AND o.execution_id=@id AND o.owner_peer_id<>@local AND o.state='active'",
                c,tx);read.Parameters.AddWithValue("id",id);read.Parameters.AddWithValue("local",identity.PeerId);
                await using var r=await read.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct)) return new(false,
                "Propriétaire neutralisé et checkpoint répliqué requis.");var body=r.IsDBNull(0)?null:r.GetString(0);neutralization=r.GetGuid(1);
                oldOwner=r.GetGuid(2);at=r.GetFieldValue<DateTimeOffset>(3);await r.DisposeAsync();
            if(body is not null)
            {
                var(value,_)=await checkpoints.VerifyAsync(body,c,tx,ct);if(value is null) return new(false,"Checkpoint signé indisponible.");saved=value;
            }
        }
        if(clock.GetUtcNow()-at<TaskCoordinationProjection.RecoveryGrace) return new(false,"Délai de grâce non écoulé (135 secondes).");
        if(saved is null)
            return await RecoverFromSourceWithoutCheckpointAsync(id,oldOwner,neutralization,ct);
        var snapshot=checkpoints.Decode(saved);
        await using(var newer=db.CreateCommand("SELECT EXISTS(SELECT 1 FROM execution_steps WHERE execution_id=@id AND completed_revision>@revision)")) {newer.Parameters.AddWithValue("id",
            id);newer.Parameters.AddWithValue("revision",snapshot.Revision);if(await newer.ScalarBoolAsync(ct)) return new(false,
            "Un checkpoint plus récent doit être reçu avant la reprise.");}
        if(snapshot.Flow.Nodes.Any(n=>!RuntimeStepEffects.IsReplayable(n.Component.PluginId,n.Component.ComponentId))) return new(false,
            "Reprise refusée : ce flow peut avoir un effet externe incertain.");
        var source=await EligibleSourceAsync(snapshot,ct);if(source is null||!await checkpoints.HasFilesAsync(snapshot,ct)) return new(false,
            "Reprise refusée : capacités, accès direct ou artefacts vérifiés manquants.");
        var transfer=Guid.NewGuid();var proposal=saved with {TransferId=transfer,TargetPeerId=identity.PeerId};
        await using(var c=await db.OpenConnectionAsync(ct))
        await using(var tx=await c.BeginTransactionAsync(ct))
        {
            await using(var abandoned=Sql.Command("UPDATE task_transfers SET state='aborted' WHERE execution_id=@id AND source_peer_id=@peer AND state='received'",
                c,tx)) {abandoned.Parameters.AddWithValue("id",id);abandoned.Parameters.AddWithValue("peer",oldOwner);await abandoned.ExecuteNonQueryAsync(ct);}
            await InsertTransferAsync(proposal,checkpoints.Sign(proposal with {AuthorPeerId=identity.PeerId}),"received",source,c,tx,ct);
            // The cached original proof is retained; the recovery decision is signed by the new owner.
            await using(var update=Sql.Command("UPDATE task_transfers SET snapshot=@body WHERE id=@id",c,tx)) {update.Parameters.AddWithValue("body",
                checkpoints.Sign(proposal with {AuthorPeerId=identity.PeerId}));update.Parameters.AddWithValue("id",transfer);
                await update.ExecuteNonQueryAsync(ct);}
            await AppendAsync(new(2,Guid.NewGuid(),saved.ClusterId,identity.PeerId,"recover",id,saved.Epoch+1,saved.PreviousEventId,identity.PeerId,transfer,
                RecordedAt:clock.GetUtcNow(),NeutralizationId:neutralization),c,tx,ct);await tx.CommitAsync(ct);
        }
        await projection.ProjectAsync(ct);var owner=await ownership.GetAsync(id,ct);return new(owner?.OwnerPeerId==identity.PeerId,
            "Reprise manuelle journalisée après neutralisation ; ancien propriétaire exclu.",transfer);
    }

    private async Task<ClusterTaskResult> RecoverFromSourceWithoutCheckpointAsync(Guid id,Guid oldOwner,Guid neutralization,CancellationToken ct)
    {
        ClusterTaskSnapshot snapshot;Guid cluster;Guid previousEvent;long epoch;
        await using(var c=await db.OpenConnectionAsync(ct))
        await using(var tx=await c.BeginTransactionAsync(ct))
        {
            await using var read=Sql.Command("""
              SELECT o.cluster_id,o.last_event_id,o.epoch,o.library_id,o.media_key,e.media,e.library_name,e.flow_definition,e.attempt,e.queued_at
              FROM task_ownership o JOIN executions e ON e.id=o.execution_id JOIN libraries l ON l.id=o.library_id AND l.is_active AND NOT l.is_deleted
              LEFT JOIN execution_work w ON w.execution_id=e.id
              WHERE o.execution_id=@id AND o.owner_peer_id=@peer AND o.state='active' AND e.state IN ('queued','running')
                AND w.published_path IS NULL AND NOT COALESCE(w.publication_requested,FALSE)
                AND NOT EXISTS(SELECT 1 FROM execution_steps s WHERE s.execution_id=e.id AND NOT s.idempotent)
                AND NOT EXISTS(SELECT 1 FROM task_checkpoints p WHERE p.execution_id=o.execution_id AND p.epoch=o.epoch)
              """,c,tx);
            read.Parameters.AddWithValue("id",id);read.Parameters.AddWithValue("peer",oldOwner);
            await using var r=await read.ExecuteReaderAsync(ct);
            if(!await r.ReadAsync(ct)) return new(false,"Propriétaire neutralisé et checkpoint répliqué requis.");
            cluster=r.GetGuid(0);previousEvent=r.GetGuid(1);epoch=r.GetInt64(2);
            var flow=JsonSerializer.Deserialize<Encoding.Contracts.Flows.FlowDefinition>(r.GetString(7),TaskCheckpointRepository.Json)!;
            snapshot=new(id,r.GetGuid(3),r.GetString(5),r.GetString(4),r.GetString(6),flow,r.GetInt32(8),0,r.GetFieldValue<DateTimeOffset>(9),1,null,null,[],[],
                new Dictionary<string,string>(),new Dictionary<string,IReadOnlyDictionary<string,JsonElement>>(),"beside");
        }
        if(snapshot.Flow.Nodes.Any(n=>!RuntimeStepEffects.IsReplayable(n.Component.PluginId,n.Component.ComponentId)))
            return new(false,"Reprise refusée : ce flow peut avoir un effet externe incertain.");
        var source=await EligibleSourceAsync(snapshot,ct);
        if(source is null) return new(false,"Reprise refusée : capacités, accès direct ou artefacts vérifiés manquants.");
        var transfer=Guid.NewGuid();
        var proposal= checkpoints.ProtectSnapshot(cluster,id,epoch,previousEvent,snapshot) with {TransferId=transfer,TargetPeerId=identity.PeerId};
        var signed=checkpoints.Sign(proposal);
        await using(var c=await db.OpenConnectionAsync(ct))
        await using(var tx=await c.BeginTransactionAsync(ct))
        {
            await using(var abandoned=Sql.Command("UPDATE task_transfers SET state='aborted' WHERE execution_id=@id AND source_peer_id=@peer AND state='received'",
                c,tx)) {abandoned.Parameters.AddWithValue("id",id);abandoned.Parameters.AddWithValue("peer",oldOwner);await abandoned.ExecuteNonQueryAsync(ct);}
            await InsertTransferAsync(proposal,signed,"received",source,c,tx,ct);
            await AppendAsync(new(2,Guid.NewGuid(),cluster,identity.PeerId,"recover",id,epoch+1,previousEvent,identity.PeerId,transfer,
                RecordedAt:clock.GetUtcNow(),NeutralizationId:neutralization),c,tx,ct);
            await tx.CommitAsync(ct);
        }
        await projection.ProjectAsync(ct);
        var owner=await ownership.GetAsync(id,ct);
        return new(owner?.OwnerPeerId==identity.PeerId,"Reprise depuis la source journalisée après neutralisation ; ancien propriétaire exclu.",transfer);
    }
}
