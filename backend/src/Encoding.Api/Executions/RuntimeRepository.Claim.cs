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

public sealed partial class RuntimeRepository
{
    public async Task<RuntimeLease?> ClaimAsync(Guid worker,CancellationToken ct)
    {
        if(ownership is not null && !await ownership.PrepareClaimsAsync(ct)) return null;
        // Persist failures independently, so an inaccessible task cannot delay another eligible task.
        if (await HasUnavailableSourcesAsync(dataSource, presence.OfflineAfter, ct))
        {
            await using var recovery = await dataSource.OpenConnectionAsync(ct);
            await using var recoveryTransaction = await recovery.BeginTransactionAsync(ct);
            if (await FailUnavailableSourcesAsync(recovery, recoveryTransaction, presence.OfflineAfter, ct) > 0)
                await recoveryTransaction.CommitAsync(ct);
        }
        ClaimCursor? after = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await ClaimAvailableAsync(worker, after, ct);
            if (page.Lease is not null || page.Next is null) return page.Lease;
            // Each page releases its transaction/admission before scanning the next one,
            // so heartbeats and renewals can run while a heterogeneous queue is searched.
            after = page.Next;
        }
    }

    private const int CandidatePageSize = 100;
    private sealed record ClaimCursor(DateTimeOffset QueuedAt, Guid Id);
    private sealed record ClaimPage(RuntimeLease? Lease, ClaimCursor? Next = null);

    internal async Task<IReadOnlyList<NodePlacementCandidate>> LoadPlacementCandidatesAsync(
        Guid libraryId,
        DbConnection c,
        DbTransaction tx,
        CancellationToken ct,
        Guid? excludeExecutionId = null)
    {
        const string sql = """
          SELECT n.id, n.name, n.roles,
            COALESCE((SELECT p.max_concurrent_tasks FROM node_processing_settings p WHERE p.node_id=n.id), n.max_concurrent_tasks) AS max_tasks,
            (SELECT count(*) FROM execution_work aw JOIN executions ae ON ae.id=aw.execution_id WHERE aw.worker_node_id=n.id AND aw.execution_id<>@exclude_execution AND aw.lease_until>clock_timestamp() AND ae.state IN ('running','cancelling')) AS active_tasks,
            n.capabilities::text,
            n.inventory::text,
            n.last_seen_at >= clock_timestamp() - @offline AS is_online,
            n.accepts_work,
            a.mode, a.local_path, a.can_read, a.can_write,
            (SELECT p.schedule_timezone FROM node_processing_settings p WHERE p.node_id=n.id AND p.schedule_enabled) AS sched_tz,
            (SELECT p.schedule_slots FROM node_processing_settings p WHERE p.node_id=n.id AND p.schedule_enabled) AS sched_slots,
            n.labels::text,
            EXISTS(
              SELECT 1 FROM library_node_accesses da JOIN cluster_nodes dn ON dn.id=da.node_id
              WHERE da.library_id=@library AND da.mode='direct' AND da.is_enabled AND da.can_read
                AND da.local_path IS NOT NULL
                AND dn.deleted_at IS NULL AND dn.last_seen_at>clock_timestamp()-@offline
                AND dn.accepts_work AND 'worker'=ANY(dn.roles)
            ) AS has_valid_transfer_source
          FROM cluster_nodes n
          LEFT JOIN library_node_accesses a ON a.node_id=n.id AND a.library_id=@library AND a.is_enabled
          WHERE n.deleted_at IS NULL
          """;
        await using var cmd = Sql.Command(sql, c, tx);
        cmd.Parameters.AddWithValue("offline", presence.OfflineAfter);
        cmd.Parameters.AddWithValue("library", libraryId);
        cmd.Parameters.AddWithValue("exclude_execution", excludeExecutionId?.ToString("D") ?? string.Empty);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<NodePlacementCandidate>();
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetGuid(0);
            var name = reader.IsDBNull(1) ? null : reader.GetString(1);
            var roles = reader.GetStringArray(2);
            var isWorker = roles.Contains("worker", StringComparer.OrdinalIgnoreCase);
            var maxTasks = Convert.ToInt32(reader.GetValue(3));
            var activeTasks = Convert.ToInt32(reader.GetValue(4));
            var caps = JsonSerializer.Deserialize<NodeToolCapability[]>(reader.GetString(5), Json) ?? [];
            var inv = reader.IsDBNull(6) ? null : JsonSerializer.Deserialize<NodeInventory>(reader.GetString(6), Json);
            var isOnline = reader.GetBoolean(7);
            var acceptsWork = reader.GetBoolean(8);
            var mode = reader.IsDBNull(9) ? null : reader.GetString(9);
            var localPath = reader.IsDBNull(10) ? null : reader.GetString(10);
            var canRead = !reader.IsDBNull(11) && reader.GetBoolean(11);
            var canWrite = !reader.IsDBNull(12) && reader.GetBoolean(12);
            var schedTz = reader.IsDBNull(13) ? null : reader.GetString(13);
            var schedSlots = reader.IsDBNull(14) ? null : reader.GetString(14);
            var labelsJson = reader.IsDBNull(15) ? null : reader.GetString(15);
            var hasValidTransferSource = reader.GetBoolean(16);

            LibraryNodeAccessInfo? access = null;
            if (mode is not null)
            {
                access = new LibraryNodeAccessInfo(
                    libraryId,
                    mode,
                    true,
                    canRead,
                    canWrite,
                    mode == "direct" ? localPath : null,
                    null);
            }

            var identity = await nodeGroups.GetNodeIdentityAsync(id, ct);

            list.Add(new NodePlacementCandidate(
                NodeId: id,
                Name: name,
                IsWorker: isWorker,
                IsOnline: isOnline && acceptsWork,
                Capabilities: caps,
                Inventory: inv,
                Identity: identity,
                Access: access,
                HasAvailableCapacity: maxTasks>activeTasks));
        }
        return list;
    }

    private async Task<ClaimPage> ClaimAvailableAsync(Guid worker, ClaimCursor? after, CancellationToken ct)
    {
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        IReadOnlyList<NodeToolCapability> capabilities; int capacity; NodeInventory? inventory;
        await using(var node=Sql.Command("SELECT capabilities::text,COALESCE((SELECT p.max_concurrent_tasks FROM node_processing_settings p WHERE p.node_id=cluster_nodes.id),max_concurrent_tasks),(SELECT p.schedule_timezone FROM node_processing_settings p WHERE p.node_id=cluster_nodes.id AND p.schedule_enabled),(SELECT p.schedule_slots FROM node_processing_settings p WHERE p.node_id=cluster_nodes.id AND p.schedule_enabled),inventory::text FROM cluster_nodes WHERE id=@node AND deleted_at IS NULL AND accepts_work AND 'worker'=ANY(roles) AND last_seen_at>clock_timestamp()-@offline FOR UPDATE",
            c,tx))
        {
            node.Parameters.AddWithValue("offline",presence.OfflineAfter); node.Parameters.AddWithValue("node",worker); await using var r=await node.ExecuteReaderAsync(ct);
            if(!await r.ReadAsync(ct)) return new(null);
            capabilities=JsonSerializer.Deserialize<NodeToolCapability[]>(r.GetString(0),Json)??[]; capacity=r.GetInt32(1);
                inventory=r.IsDBNull(4)?null:JsonSerializer.Deserialize<NodeInventory>(r.GetString(4),Json);
            // Le calendrier ne bloque que les nouveaux démarrages : les baux actifs ne sont ni révoqués ni interrompus.
            if(!r.IsDBNull(2) && !r.IsDBNull(3) && !new NodeSchedule(true,r.GetString(2),r.GetString(3)).IsOpen(DateTimeOffset.UtcNow)) return new(null);
        }
        await using(var active=Sql.Command("SELECT count(*) FROM execution_work w JOIN executions e ON e.id=w.execution_id WHERE w.worker_node_id=@worker AND w.lease_until>clock_timestamp() AND e.state IN ('running','cancelling')",
            c,tx))
        { active.Parameters.AddWithValue("worker",worker); if((long)(await active.ExecuteScalarAsync(ct))!>=capacity) return new(null); }
        var nodeIdentity=await nodeGroups.GetNodeIdentityAsync(worker,ct);
        if(nodeIdentity is null) return new(null);
        var candidates=new List<(Guid Id,Guid Library,string Path,string Key,FlowDefinition Flow,int Attempt,int Revision,long Epoch,
            RuntimeCheckpoint? Checkpoint,RuntimeHandoffSelector? Selector,Guid? PreviousWorker,Guid? Source,byte[]? Settings,RuntimeWaitProbe? Probe,
                DateTimeOffset QueuedAt,FlowPlacementPlan? PlacementPlan,JsonElement? BeforeMetadata)>();
        var cursorPredicate = after is null ? "" : "AND (e.queued_at,e.id) > (@after_time,@after_id)";
        await using(var read=Sql.Command($"""
          SELECT e.id,w.library_id,w.media_path,w.media_key,e.flow_definition::text,e.attempt,e.revision,w.lease_epoch,
            (SELECT checkpoint::text FROM execution_steps WHERE execution_id=e.id AND completed_at IS NOT NULL ORDER BY completed_revision DESC LIMIT 1),
            w.handoff_selector::text,w.previous_worker_node_id,w.source_node_id,w.plugin_configuration_snapshot,
            CASE WHEN w.wait_probe_due THEN (SELECT x.spec::text FROM execution_waits x WHERE x.execution_id=e.id AND x.state='waiting') END,e.queued_at,w.placement_plan::text,w.before_metadata::text
          FROM execution_work w JOIN executions e ON e.id=w.execution_id JOIN libraries l ON l.id=w.library_id
          WHERE (e.state='queued' OR e.state='running' AND w.worker_node_id IS NULL)
          AND {TaskOwnershipRepository.ActiveSql("e.id")}
          AND COALESCE(w.handoff_selector->>'mode','')<>'server'
          AND l.is_active AND w.available_at<=clock_timestamp()
          AND w.lease_until IS NULL
          {cursorPredicate}
          AND NOT EXISTS(SELECT 1 FROM library_processing_settings p WHERE p.library_id=l.id AND p.max_concurrent_tasks IS NOT NULL
            AND p.max_concurrent_tasks<=(SELECT count(*) FROM execution_work aw JOIN executions ae ON ae.id=aw.execution_id WHERE aw.library_id=l.id AND aw.lease_until>clock_timestamp() AND ae.state IN ('running','cancelling')))
          AND NOT EXISTS(SELECT 1 FROM library_node_processing_limits p WHERE p.library_id=l.id AND p.node_id=@worker
            AND p.max_concurrent_tasks<=(SELECT count(*) FROM execution_work aw JOIN executions ae ON ae.id=aw.execution_id WHERE aw.library_id=l.id AND aw.worker_node_id=@worker AND aw.lease_until>clock_timestamp() AND ae.state IN ('running','cancelling')))
          ORDER BY e.queued_at,e.id LIMIT @page_size
          FOR UPDATE OF e,w SKIP LOCKED
          """,c,tx))
        {
            read.Parameters.AddWithValue("worker",worker);
            if (after is not null)
            {
                read.Parameters.AddWithValue("after_time", after.QueuedAt);
                read.Parameters.AddWithValue("after_id", after.Id);
            }
            read.Parameters.AddWithValue("page_size", CandidatePageSize);
            await using var r=await read.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) candidates.Add((r.GetGuid(0),r.GetGuid(1),r.GetString(2),r.GetString(3),
                JsonSerializer.Deserialize<FlowDefinition>(r.GetString(4),Json)!,r.GetInt32(5),r.GetInt32(6),r.GetInt64(7),
                r.IsDBNull(8)?null:JsonSerializer.Deserialize<RuntimeCheckpoint>(r.GetString(8),Json),
                r.IsDBNull(9)?null:JsonSerializer.Deserialize<RuntimeHandoffSelector>(r.GetString(9),Json),
                r.IsDBNull(10)?null:r.GetGuid(10),r.IsDBNull(11)?null:r.GetGuid(11),r.IsDBNull(12)?null:(byte[])r[12],
                r.IsDBNull(13)?null:ExecutionWaitRepository.ProbeOf(JsonSerializer.Deserialize<RuntimeWait>(r.GetString(13),Json)!),
                    r.GetFieldValue<DateTimeOffset>(14),r.IsDBNull(15)?null:JsonSerializer.Deserialize<FlowPlacementPlan>(r.GetString(15),Json),
                    r.IsDBNull(16)?null:JsonSerializer.Deserialize<JsonElement>(r.GetString(16),Json)));
        }
        var placementCandidatesByLibrary=new Dictionary<Guid,IReadOnlyList<NodePlacementCandidate>>();
        var placementPlans=new Dictionary<(Guid Library,Guid Flow,int Version,string Metadata),FlowPlacementPlan>();
        foreach(var item in candidates)
        {
            FlowPlacementPlan? plan = item.PlacementPlan;
            var placementIsPersisted = item.PlacementPlan is not null;
            // Legacy and not-yet-scanned executions may have no durable plan. Compute a read-only admission
            // hint here; persistence is intentionally deferred until source metadata is reported.
            if(item.Probe is null && plan is null)
            {
                var metadataKey=item.BeforeMetadata?.GetRawText()??string.Empty;
                var key=(item.Library,item.Flow.Id,item.Flow.Version,metadataKey);
                if(!placementPlans.TryGetValue(key,out plan))
                {
                    if(!placementCandidatesByLibrary.TryGetValue(item.Library,out var nodeCandidates))
                        placementCandidatesByLibrary[item.Library]=nodeCandidates=await LoadPlacementCandidatesAsync(item.Library,c,tx,ct);
                    plan=planner.Plan(item.Flow,item.BeforeMetadata,nodeCandidates,DateTimeOffset.UtcNow);
                    if(plan.Strategy==FlowPlacementPlan.StrategyImpossible)
                        logger?.LogWarning("Placement impossible pour l’exécution {ExecutionId} : {Reason}",item.Id,plan.Reason);
                    placementPlans[key]=plan;
                }
            }
            if(item.Probe is {} wanted)
            {
                if(!eligibility.CanProbe(item.Flow,capabilities,wanted.BlockNodeId,item.Checkpoint is not null)) continue;
            }
            else if(plan is not null && (placementIsPersisted || item.Checkpoint is null))
            {
                if(plan.Strategy == FlowPlacementPlan.StrategyImpossible)
                {
                    continue;
                }

                var directUnifiedStayOnWorker = plan.Strategy == FlowPlacementPlan.StrategyDirectUnified
                    && item.Selector?.Mode == "eligible"
                    && plan.AssignedNodeId == worker;

                if(!directUnifiedStayOnWorker && item.Selector is {} selector && (!item.PreviousWorker.HasValue || !selector.Accepts(worker,item.PreviousWorker.Value,nodeIdentity)))
                    continue;

                if(item.Selector is null)
                {
                    if(plan.Strategy is FlowPlacementPlan.StrategyDirectUnified or FlowPlacementPlan.StrategyTransferWorker)
                    {
                        if(plan.AssignedNodeId.HasValue && plan.AssignedNodeId.Value != worker) continue;
                    }
                    else if(plan.Strategy is FlowPlacementPlan.StrategyTranscodeRelay or FlowPlacementPlan.StrategySegmented)
                    {
                        if(item.Checkpoint is null)
                        {
                            var entry=item.Flow.Nodes.Single(node=>components.Resolve(node.Component)?.Kind=="input").Id;
                            var initialTarget=plan.NodeAssignments?.TryGetValue(entry,out var assignedEntry)==true?assignedEntry:plan.AssignedNodeId;
                            if(initialTarget.HasValue && initialTarget.Value!=worker) continue;
                        }
                        else
                        {
                            var target = item.Checkpoint.NextNodeId is { } next && plan.NodeAssignments?.TryGetValue(next, out var assigned)==true
                                ? assigned : (Guid?)null;
                            if(target.HasValue && target.Value != worker) continue;
                        }
                    }
                }
            }
            else if(item.Probe is null && item.Selector is {} selector
                && (!item.PreviousWorker.HasValue || !selector.Accepts(worker,item.PreviousWorker.Value,nodeIdentity))) continue;
            var planValidatesWorker = (placementIsPersisted || item.Checkpoint is null) && plan is not null
                && (plan.Strategy == FlowPlacementPlan.StrategyDirectUnified && plan.AssignedNodeId == worker
                    || plan.Strategy == FlowPlacementPlan.StrategyTransferWorker && plan.AssignedNodeId == worker);

            if(item.Probe is null && !planValidatesWorker
                && !eligibility.CanRun(item.Flow,capabilities,worker,item.Checkpoint?.NextNodeId,item.Checkpoint is not null,item.Checkpoint?.Context,nodeIdentity,inventory,
                placementIsPersisted || item.Checkpoint is null ? plan?.NodeAssignments : null)) continue;
            var sourceEditResume=false;
            await using(var waiting=Sql.Command("SELECT EXISTS(SELECT 1 FROM runtime_source_write_waits WHERE execution_id=@id)",c,tx))
            {
                waiting.Parameters.AddWithValue("id",item.Id);
                sourceEditResume=await waiting.ScalarBoolAsync(ct);
                if(sourceEditResume && sourceWrites is not null && await sourceWrites.CheckAsync(c,tx,ct) is not null) continue;
            }
            if(sourceWrites is not null && item.Flow.Nodes.Any(n=>n.Component.ComponentId=="mkv-edit-in-place")
                && !capabilities.Any(cap=>cap.ToolId==SourceWriteProtocol.Capability) && await sourceWrites.IsClusteredAsync(c,tx,ct)) continue;
            // Library row serializes claims from all workers before counting active leases.
            await using(var limits=Sql.Command("""
              SELECT p.max_concurrent_tasks,(SELECT max_concurrent_tasks FROM library_node_processing_limits WHERE library_id=l.id AND node_id=@worker)
              FROM libraries l LEFT JOIN library_processing_settings p ON p.library_id=l.id
              WHERE l.id=@library AND l.is_active FOR UPDATE OF l SKIP LOCKED
              """,c,tx))
            {
                limits.Parameters.AddWithValue("library",item.Library); limits.Parameters.AddWithValue("worker",worker);
                await using var r=await limits.ExecuteReaderAsync(ct); if(!await r.ReadAsync(ct)) continue;
                int? global=r.IsDBNull(0)?null:r.GetInt32(0); int? local=r.IsDBNull(1)?null:r.GetInt32(1); await r.DisposeAsync();
                await using var count=Sql.Command("""
                  SELECT count(*)::integer,count(*) FILTER(WHERE w.worker_node_id=@worker)::integer
                  FROM execution_work w JOIN executions e ON e.id=w.execution_id
                  WHERE w.library_id=@library AND w.lease_until>clock_timestamp() AND e.state IN ('running','cancelling')
                  """,c,tx);
                count.Parameters.AddWithValue("library",item.Library); count.Parameters.AddWithValue("worker",worker);
                await using var counts=await count.ExecuteReaderAsync(ct); await counts.ReadAsync(ct);
                if(global is {} maximum && counts.GetInt32(0)>=maximum || local is {} maximumLocal && counts.GetInt32(1)>=maximumLocal) continue;
            }
            Guid source=item.Source??worker; string? root=null;
            var needsWrite=item.Flow.Nodes.Any(n=>components.Resolve(n.Component)!.RequiresWriteAccess);
            bool replacementAccess = false;
            if (needsWrite)
            await using (var policy = Sql.Command("SELECT w.media_policy<>'beside' AND EXISTS(SELECT 1 FROM library_replacement_policies p WHERE p.library_id=w.library_id AND p.enabled) FROM execution_work w WHERE w.execution_id=@id",
                c, tx))
            {
                policy.Parameters.AddWithValue("id", item.Id);
                replacementAccess = await policy.ScalarBoolAsync(ct);
            }
            var needsDirect=sourceEditResume || eligibility.RequiresDirectAccess(item.Flow,item.Checkpoint?.NextNodeId,item.Checkpoint is not null);
            // ADR 0024 : par défaut le worker à accès direct fait le travail. Un worker par transfert ne le prend que si aucun worker
            // direct en ligne n'en est capable (capacités évaluées comme pour une réclamation) ; une passation ou un choix de nœud
            // explicite garde la décision du flow.
            var prefersDirect=eligibility.PrefersDirectAccess(item.Flow,item.Checkpoint?.NextNodeId,item.Checkpoint is not null)
                || item.Selector is null && !eligibility.ChoosesPlacementExplicitly(item.Flow,item.Checkpoint?.NextNodeId,item.Checkpoint is not null);
            var deferToDirect=false;
            if(prefersDirect && !needsDirect && item.Probe is null && item.Checkpoint is null)
                deferToDirect=await CapableDirectWorkerExistsAsync(item.Library,needsWrite,item,worker,c,tx,ct);
            if(item.Probe is null)
            {
                await using(var access=Sql.Command("""
                  SELECT COALESCE(a.transfer_source_node_id,a.node_id),CASE WHEN a.mode='direct' THEN a.local_path END
                  FROM library_node_accesses a JOIN cluster_nodes n ON n.id=a.node_id
                  LEFT JOIN library_node_accesses s ON s.library_id=a.library_id AND s.node_id=a.transfer_source_node_id
                  LEFT JOIN cluster_nodes sn ON sn.id=s.node_id
                  WHERE a.library_id=@library AND a.node_id=@worker AND a.is_enabled AND a.can_read
                  AND ((a.mode='direct' AND a.local_path IS NOT NULL AND (NOT @write OR a.can_write))
                    OR (NOT @direct AND NOT @defer AND a.mode='transfer' AND s.mode='direct' AND s.is_enabled AND s.can_read AND (NOT @write OR s.can_write)
                        AND sn.deleted_at IS NULL AND sn.last_seen_at>clock_timestamp()-@offline))
                  """,c,tx))
                {
                    access.Parameters.AddWithValue("offline",presence.OfflineAfter); access.Parameters.AddWithValue("library",item.Library);
                        access.Parameters.AddWithValue("worker",worker); access.Parameters.AddWithValue("write",needsWrite);
                    access.Parameters.AddWithValue("direct",needsDirect); access.Parameters.AddWithValue("defer",deferToDirect);
                    await using var r=await access.ExecuteReaderAsync(ct); if(!await r.ReadAsync(ct)) continue;
                    source=r.GetGuid(0); root=r.IsDBNull(1)?null:r.GetString(1);
                }
                // An explicit relay keeps the original library-access node pinned. A fresh assignment after a wait
                // or a flow retry uses the receiving worker's own library access.
                if(item.Source is {} originalSource && item.Selector is not null)
                {
                    // A relay does not move ownership of the library or reinterpret the source path.
                    await using var originalAccess=Sql.Command("""
                      SELECT EXISTS(SELECT 1 FROM library_node_accesses a JOIN cluster_nodes n ON n.id=a.node_id
                        WHERE a.library_id=@library AND a.node_id=@source AND a.mode='direct' AND a.is_enabled
                        AND a.can_read AND (NOT @write OR a.can_write) AND a.local_path IS NOT NULL
                        AND n.deleted_at IS NULL AND n.last_seen_at>clock_timestamp()-@offline)
                      """,c,tx);
                    originalAccess.Parameters.AddWithValue("library",item.Library); originalAccess.Parameters.AddWithValue("source",originalSource);
                    originalAccess.Parameters.AddWithValue("write",needsWrite); originalAccess.Parameters.AddWithValue("offline",presence.OfflineAfter);
                    if(await originalAccess.ScalarBoolAsync(ct)) { source=originalSource; if(source!=worker) root=null; }
                    // Après la perte d'un worker, l'accès d'origine peut être le worker perdu lui-même (accès direct sur chaque worker) :
                    // la reprise utilise alors l'accès propre du worker qui la reçoit, déjà résolu ci-dessus.
                    else if(!item.Selector.IsResume) continue;
                }
            }
            var pluginSettings=item.Settings is {} savedSettings?pluginConfigurations.ReadSnapshot(savedSettings)
                :await pluginConfigurations.ResolveEffectiveAsync(item.Flow.Id,item.Flow.Nodes.Select(node=>node.Component.PluginId).Where(id=>id!="base"),c,tx,ct);
            if (PluginConfigurationPrerequisites.FindMissing(item.Flow, components.Plugins, pluginSettings).FirstOrDefault() is { } missing)
            {
                await ExecutionRepository.RecordInTransactionAsync(item.Id,
                    new(Guid.NewGuid(), item.Revision, "failed", worker, null,
                        $"{missing.Code} : bloc {missing.NodeId}, plugin {missing.PluginId}, paramètre requis {missing.FieldId}."), c, tx, ct);
                await ReleaseAsync(item.Id, c, tx, ct);
                await tx.CommitAsync(ct);
                return new(null);
            }
            pluginSettings=components.ForWorker(pluginSettings);
            var segmentEntry=item.Checkpoint?.NextNodeId??(item.Checkpoint is null?item.Flow.Nodes.Single(node=>components.Resolve(node.Component)?.Kind=="input").Id:null);
            if(segmentEntry is not null && item.Probe is null)
            {
                var returnsToSelf=false;
                foreach(var relay in RuntimeHandoffPolicy.Segment(item.Flow,segmentEntry).Where(RuntimeHandoffPolicy.IsHandoff))
                {
                    RuntimeHandoffSelector destination;
                    try { destination=RuntimeHandoffPolicy.Resolve(relay,pluginSettings.GetValueOrDefault("nodes")); }
                    catch(ArgumentException)
                    {
                        await using var actor=Sql.Command("UPDATE executions SET node_id=@worker WHERE id=@id",c,tx);
                        actor.Parameters.AddWithValue("id",item.Id); actor.Parameters.AddWithValue("worker",worker); await actor.ExecuteNonQueryAsync(ct);
                        await ExecutionRepository.RecordInTransactionAsync(item.Id,new(Guid.NewGuid(),item.Revision,"failed",worker,null,
                            "La configuration du relais ne désigne pas un worker valide."),c,tx,ct);
                        await ReleaseAsync(item.Id,c,tx,ct); await tx.CommitAsync(ct); return new(null);
                    }
                    if(destination.Mode=="node" && destination.TargetNodeId==worker) returnsToSelf=true;
                    if(destination.Mode=="server" && !capabilities.Any(c=>c.ToolId=="plugin/nodes" && Version.TryParse(c.Version,out var v) && v>=new Version(1,8,0))) returnsToSelf=true;
                }
                if(returnsToSelf) continue;
            }
            var epoch=checked(item.Epoch+1);
            await using(var reserve=Sql.Command("""
              INSERT INTO media_reservations(library_id,media_key,execution_id,lease_epoch,lease_until)
              VALUES(@library,@key,@id,@epoch,clock_timestamp()+interval '180 seconds')
              ON CONFLICT(library_id,media_key) DO UPDATE SET execution_id=@id,lease_epoch=@epoch,lease_until=clock_timestamp()+interval '180 seconds'
              WHERE media_reservations.execution_id=@id OR media_reservations.lease_until<=clock_timestamp() RETURNING execution_id
              """,c,tx))
            {
                reserve.Parameters.AddWithValue("library",item.Library); reserve.Parameters.AddWithValue("key",item.Key);
                reserve.Parameters.AddWithValue("id",item.Id); reserve.Parameters.AddWithValue("epoch",epoch);
                if(await reserve.ExecuteScalarAsync(ct) is null) continue;
            }
            if(sourceEditResume)
            {
                await using var clear=Sql.Command("DELETE FROM runtime_source_write_waits WHERE execution_id=@id",c,tx);
                clear.Parameters.AddWithValue("id",item.Id);await clear.ExecuteNonQueryAsync(ct);
            }
            await using(var set=Sql.Command("""
              UPDATE execution_work SET worker_node_id=@worker,source_node_id=@source,lease_epoch=@epoch,handoff_pending=false,
                 plugin_configuration_snapshot=COALESCE(plugin_configuration_snapshot,@settings),
                 lease_until=clock_timestamp()+interval '180 seconds' WHERE execution_id=@id;
              UPDATE executions SET node_id=@worker WHERE id=@id;
              """,c,tx))
            { set.Parameters.AddWithValue("worker",worker); set.Parameters.AddWithValue("source",source); set.Parameters.AddWithValue("epoch",epoch); set.Parameters.AddWithValue("id",item.Id);
                set.Parameters.AddWithValue("settings",SqlKind.Bytes,pluginConfigurations.ProtectSnapshot(pluginSettings)); await set.ExecuteNonQueryAsync(ct); }
            await using(var uncertain=Sql.Command("SELECT EXISTS(SELECT 1 FROM execution_steps WHERE execution_id=@id AND completed_at IS NULL AND NOT idempotent)",c,tx))
            {
                uncertain.Parameters.AddWithValue("id",item.Id);
                if((await uncertain.ScalarBoolAsync(ct)))
                {
                    await ExecutionRepository.RecordInTransactionAsync(item.Id,new(Guid.NewGuid(),item.Revision,"failed",worker,null,
                        "Une étape externe a un effet incertain après interruption. Une relance explicite est nécessaire."),c,tx,ct);
                    await ReleaseAsync(item.Id,c,tx,ct); await tx.CommitAsync(ct); return new(null);
                }
            }
            if(!await ExecutionRepository.RecordInTransactionAsync(item.Id,new(Guid.NewGuid(),item.Revision,"running",worker,null,
                item.Probe is not null?"Vérification de la condition d'attente confiée au worker.":item.Selector is null?"Flow attribué au worker ; bail actif.":"Relais attribué au worker ; reprise depuis le checkpoint durable."),
                    c,tx,ct)) return new(null);
            RuntimeCheckpoint? checkpoint=null; string? sourceSha=null; DateTimeOffset expires;
            await using(var state=Sql.Command("SELECT source_sha,lease_until,(SELECT checkpoint::text FROM execution_steps WHERE execution_id=@id AND completed_at IS NOT NULL ORDER BY completed_revision DESC LIMIT 1) FROM execution_work WHERE execution_id=@id",
                c,tx))
            {
                state.Parameters.AddWithValue("id",item.Id); await using var r=await state.ExecuteReaderAsync(ct); await r.ReadAsync(ct);
                sourceSha=r.IsDBNull(0)?null:r.GetString(0); expires=r.GetFieldValue<DateTimeOffset>(1);
                if(!r.IsDBNull(2)) checkpoint=JsonSerializer.Deserialize<RuntimeCheckpoint>(r.GetString(2),Json);
            }
            var directRead=source==worker && root is not null && sourceSha is null && item.Probe is null
                && capabilities.Any(capability=>capability.ToolId==RuntimeSourceObservation.Capability);
            await using(var prepare=Sql.Command("UPDATE execution_work SET source_preparation_requested=@requested WHERE execution_id=@id",c,tx))
            {
                prepare.Parameters.AddWithValue("id",item.Id); prepare.Parameters.AddWithValue("requested",!directRead);
                await prepare.ExecuteNonQueryAsync(ct);
            }
            if(ownership is not null) await ownership.SaveCheckpointAsync(item.Id,c,tx,ct);
            await tx.CommitAsync(ct); return new(new(item.Id,item.Attempt,item.Revision+1,epoch,expires,source,root,item.Path,sourceSha,item.Flow,checkpoint,
                item.Flow.Nodes.Single(node=>components.Resolve(node.Component)?.Kind=="input").Id,pluginSettings,item.Library,item.Probe,DirectRead:directRead,
                SourceWriteGuardsSupported:sourceWrites is not null,VerifyPublicationAccess:needsWrite,VerifyReplacementAccess:replacementAccess,
                PlacementPlan:plan));
        }
        return new(null, candidates.Count == CandidatePageSize
            ? new(candidates[^1].QueuedAt, candidates[^1].Id) : null);
    }
}
