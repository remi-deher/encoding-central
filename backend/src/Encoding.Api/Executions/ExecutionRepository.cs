using Encoding.Api.Infrastructure.Data;
using System.Data.Common;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Api.Flows;
using System.Text.Json;
namespace Encoding.Api.Executions;

/// <summary>Persistent lifecycle journal. Does not assign work or publish media.</summary>
public sealed class ExecutionRepository(DbDataSource dataSource, FlowComponentRegistry? components = null,
    ExecutionStreamBroadcaster? broadcaster = null,TaskOwnershipRepository? ownership=null,ClusterTaskCoordinator? coordinator=null)
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    // Le bail vit dans la base du propriétaire : sans preuve locale et non expirée, l'état répliqué ne suffit pas.
    private static string ActivelyProcessingSql(string executionAlias) =>
        $"({executionAlias}.state='running' AND w.worker_node_id IS NOT NULL AND w.lease_until>clock_timestamp() AND {TaskOwnershipRepository.ActiveSql(executionAlias + ".id")})";

    private const string StateFilterPredicate = "e.state=@state";
    private const string LibraryFilterPredicate = "COALESCE(w.library_id,o.library_id)=@libraryId";
    private const string NodeFilterPredicate = "e.node_id=@nodeId";
    private const string SinceFilterPredicate = "e.updated_at>=@since";
    private const string AfterRevisionFilterSql = " AND revision>@afterRevision";
    private const string LimitFilterSql = " LIMIT @limit";

    private static readonly string Columns = $"""
 e.id,e.media,e.library_name,e.flow_name,e.flow_version,e.state,e.node_id,n.name,
 e.progress,e.detail,e.attempt,e.revision,e.queued_at,e.started_at,e.finished_at,e.updated_at,w.before_metadata::text,w.after_metadata::text,w.published_path
 ,(SELECT checkpoint::text FROM execution_steps s WHERE s.execution_id=e.id AND s.completed_at IS NOT NULL ORDER BY s.completed_revision DESC LIMIT 1),COALESCE(w.library_id,o.library_id),o.owner_peer_id,o.epoch,o.state
 ,(SELECT s.node_id FROM execution_steps s WHERE s.execution_id=e.id AND s.completed_at IS NULL ORDER BY s.started_at DESC LIMIT 1)
 ,CASE WHEN e.state NOT IN ('running','cancelling') THEN NULL
       WHEN w.publication_requested AND w.published_path IS NULL THEN 'publication'
       WHEN w.source_preparation_requested AND w.source_sha IS NULL THEN 'source_transfer'
       WHEN w.handoff_pending THEN 'waiting_handoff' ELSE NULL END
 ,CASE WHEN {ActivelyProcessingSql("e")} THEN TRUE ELSE FALSE END
 """;
    private static ExecutionResponse Read(DbDataReader r) => new(
    r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetString(5),
    r.IsDBNull(6) ? null : r.GetGuid(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetDecimal(8),
    r.GetString(9), r.GetInt32(10), r.GetInt32(11), r.GetFieldValue<DateTimeOffset>(12),
    r.IsDBNull(13) ? null : r.GetFieldValue<DateTimeOffset>(13), r.IsDBNull(14) ? null : r.GetFieldValue<DateTimeOffset>(14),
        r.GetFieldValue<DateTimeOffset>(15),ReadMetadata(r,16),ReadMetadata(r,17),r.IsDBNull(18)?null:r.GetString(18),ReadVideoEncodings(r,19),
            r.IsDBNull(20)?null:r.GetGuid(20),r.IsDBNull(21)?null:new TaskOwnershipStatus(r.GetGuid(0),r.GetGuid(21),r.GetInt64(22),r.GetString(23)),
        ReadFailure(r), r.IsDBNull(25) ? null : r.GetString(25), r.GetBoolean(26));
    private static ExecutionFailureInfo? ReadFailure(DbDataReader reader)
    {
        if (reader.GetString(5) != "failed") return null;
        var prefix = reader.GetString(9).Split(':', 2)[0].Trim();
        var code = prefix is "permission_denied" or "media_tool_failed" or "operation_timeout" or "execution_infrastructure_failed"
            or "integration_infrastructure_failed" or "plugin_configuration_missing" or "publication_failed" or "source_transfer_failed" or "cluster_refused" or "placement_impossible" ? prefix : "execution_failed";
        return new(code, reader.IsDBNull(24) ? null : reader.GetString(24), reader.IsDBNull(18) ? null : true);
    }

    private static System.Text.Json.JsonElement? ReadMetadata(DbDataReader reader,
        int index) => reader.IsDBNull(index)?null:System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(reader.GetString(index));
    private static IReadOnlyList<ResolvedVideoEncoding> ReadVideoEncodings(DbDataReader reader,int index)
    {
        if(reader.IsDBNull(index)) return [];
        try
        {
            var checkpoint=JsonSerializer.Deserialize<RuntimeCheckpoint>(reader.GetString(index),new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return checkpoint?.Context?.VideoEncodings??[];
        }
        catch(JsonException) { return []; }
    }
    public Task<ExecutionOverviewResponse> OverviewAsync(CancellationToken ct) =>
        OverviewAsync(new ExecutionFilterQuery(), ct);

    public async Task<ExecutionOverviewResponse> OverviewAsync(ExecutionFilterQuery query, CancellationToken ct)
    {
        var limit = Math.Clamp(query.Limit <= 0 ? 200 : query.Limit, 1, 500);
        var offset = Math.Max(0, query.Offset);
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(System.Data.IsolationLevel.Snapshot, ct);
        var counts = new Dictionary<string, int>();
        await using (var cmd = Sql.Command("SELECT CASE WHEN e.state='queued' AND o.state='quarantined' THEN 'quarantined' ELSE e.state END,count(*)::integer FROM executions e LEFT JOIN task_ownership o ON o.execution_id=e.id GROUP BY 1",
            c, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct)) { while (await r.ReadAsync(ct)) counts[r.GetString(0)] = r.GetInt32(1); }
        int activeCount;
        await using (var cmd = Sql.Command($"SELECT count(*)::integer FROM executions e JOIN execution_work w ON w.execution_id=e.id WHERE {ActivelyProcessingSql("e")}", c, tx))
            activeCount = await cmd.ScalarInt32Async(ct) ?? 0;

        var predicates = new List<string>();
        void BindFilters(DbCommand cmd)
        {
            if (!string.IsNullOrWhiteSpace(query.State))
                cmd.Parameters.AddWithValue("state", query.State.Trim().ToLowerInvariant());
            if (query.LibraryId is { } libraryId)
                cmd.Parameters.AddWithValue("libraryId", libraryId);
            if (query.NodeId is { } nodeId)
                cmd.Parameters.AddWithValue("nodeId", nodeId);
            if (query.Since is { } since)
                cmd.Parameters.AddWithValue("since", since);
        }
        // Allow-listed predicates only — values bind via @params, never concatenate query text from the client.
        if (!string.IsNullOrWhiteSpace(query.State)) predicates.Add(StateFilterPredicate);
        if (query.LibraryId is not null) predicates.Add(LibraryFilterPredicate);
        if (query.NodeId is not null) predicates.Add(NodeFilterPredicate);
        if (query.Since is not null) predicates.Add(SinceFilterPredicate);
        var whereSql = predicates.Count > 0 ? " WHERE " + string.Join(" AND ", predicates) : string.Empty;

        int totalCount;
        if (predicates.Count == 0)
        {
            totalCount = counts.Values.Sum();
        }
        else
        {
            await using var countCmd = Sql.Command($"SELECT count(*)::integer FROM executions e LEFT JOIN execution_work w ON w.execution_id=e.id LEFT JOIN task_ownership o ON o.execution_id=e.id{whereSql}",
                c, tx);
            BindFilters(countCmd);
            totalCount = await countCmd.ScalarInt32Async(ct) ?? 0;
        }

        var list = new List<ExecutionResponse>();
        await using (var cmd = Sql.Command($"SELECT {Columns} FROM executions e LEFT JOIN cluster_nodes n ON n.id=e.node_id LEFT JOIN execution_work w ON w.execution_id=e.id LEFT JOIN task_ownership o ON o.execution_id=e.id{whereSql} ORDER BY CASE WHEN {ActivelyProcessingSql("e")} THEN 0 ELSE 1 END,CASE WHEN e.state IN ('queued','running','cancelling') THEN 0 ELSE 1 END,e.updated_at DESC,e.id LIMIT @limit OFFSET @offset",
            c, tx))
        {
            BindFilters(cmd);
            cmd.Parameters.AddWithValue("limit", limit);
            cmd.Parameters.AddWithValue("offset", offset);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) list.Add(Read(r));
        }

        var activity = new List<ExecutionActivityResponse>();
        await using (var cmd = Sql.Command("SELECT v.id,v.execution_id,e.media,v.state,v.detail,v.created_at FROM execution_events v JOIN executions e ON e.id=v.execution_id ORDER BY v.created_at DESC,v.id LIMIT 20",
            c, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct)) { while (await r.ReadAsync(ct)) activity.Add(new(r.GetGuid(0), r.GetGuid(1), r.GetString(2),
            r.GetString(3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5))); }
        await tx.CommitAsync(ct);
        int Count(string s) => counts.GetValueOrDefault(s);
        return new(list, Count("queued"), activeCount, Count("failed"), Count("cancelled"), Count("succeeded"), DateTimeOffset.UtcNow, activity,
            Count("deferred"), totalCount, limit, offset, Count("quarantined"));
    }

    public async Task<ExecutionResponse?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = Sql.Command($"SELECT {Columns} FROM executions e LEFT JOIN cluster_nodes n ON n.id=e.node_id LEFT JOIN execution_work w ON w.execution_id=e.id LEFT JOIN task_ownership o ON o.execution_id=e.id WHERE e.id=@id",
            c);
        cmd.Parameters.AddWithValue("id", id); await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public Task<IReadOnlyList<ExecutionEventResponse>> EventsAsync(Guid id, CancellationToken ct) =>
        EventsAsync(id, null, null, ct);

    public async Task<IReadOnlyList<ExecutionEventResponse>> EventsAsync(Guid id, int? afterRevision, int? limit, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var filterSql = afterRevision is not null ? AfterRevisionFilterSql : string.Empty;
        var limitSql = limit is not null ? LimitFilterSql : string.Empty;
        await using var cmd = Sql.Command($"SELECT id,revision,state,detail,created_at FROM execution_events WHERE execution_id=@id{filterSql} ORDER BY revision{limitSql}", c);
        cmd.Parameters.AddWithValue("id", id);
        if (afterRevision is { } rev) cmd.Parameters.AddWithValue("afterRevision", rev);
        if (limit is { } max) cmd.Parameters.AddWithValue("limit", Math.Clamp(max, 1, 1000));
        await using var r = await cmd.ExecuteReaderAsync(ct); var list = new List<ExecutionEventResponse>();
        while (await r.ReadAsync(ct)) list.Add(new(r.GetGuid(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4)));
        return list;
    }
    public async Task<FlowDefinition?> FlowAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT flow_definition::text FROM executions WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.IsDBNull(0)) return null;
        return JsonSerializer.Deserialize<FlowDefinition>(reader.GetString(0), Json);
    }

    public async Task<IReadOnlyList<ExecutionPipelineStepResponse>> PipelineAsync(Guid id,CancellationToken ct)
    {
        FlowDefinition flow;
        string executionState;
        await using (var execution=dataSource.CreateCommand("SELECT flow_definition::text,state FROM executions WHERE id=@id"))
        {
            execution.Parameters.AddWithValue("id",id);
            await using var reader=await execution.ExecuteReaderAsync(ct);
            if(!await reader.ReadAsync(ct)) return [];
            flow=JsonSerializer.Deserialize<FlowDefinition>(reader.GetString(0),Json)!;
            executionState=reader.GetString(1);
        }
        // A partial replicated flow must not make durable step history unreadable.
        // Reconciliation can expose malformed or partially projected historical definitions. Keep the
        // diagnostic endpoint readable even if duplicate node IDs are present: the first serialized
        // occurrence is the deterministic descriptor for that ID, and the durable step row remains authoritative.
        var flowNodes=(flow?.Nodes ?? []).GroupBy(node=>node.Id,StringComparer.Ordinal)
            .ToDictionary(group=>group.Key,group=>group.First(),StringComparer.Ordinal);
        await using var cmd=dataSource.CreateCommand("""
          SELECT s.node_id,s.invocation_id,s.started_at,s.completed_at,s.checkpoint::text,s.command_logs::text
          FROM execution_steps s WHERE s.execution_id=@id ORDER BY s.started_at,s.node_id
          """);
        cmd.Parameters.AddWithValue("id",id); await using var r=await cmd.ExecuteReaderAsync(ct);
        var result=new List<ExecutionPipelineStepResponse>();
        while(await r.ReadAsync(ct))
        {
            var nodeId=r.GetString(0);
            var node=flowNodes.GetValueOrDefault(nodeId);
            var descriptor=node is null?null:components?.Resolve(node.Component);
            DateTimeOffset? completedAt=r.IsDBNull(3)?null:r.GetFieldValue<DateTimeOffset>(3);
            var checkpoint=r.IsDBNull(4)?null:JsonSerializer.Deserialize<RuntimeCheckpoint>(r.GetString(4),Json);
            var outputId=checkpoint?.OutputId;
            var outputLabel=outputId is null?null:descriptor?.Outputs.FirstOrDefault(output=>output.Id==outputId)?.Label??outputId;
            var status=completedAt is not null?"completed":executionState switch
            {
                "failed"=>"failed",
                "cancelled"=>"cancelled",
                "cancelling"=>"cancelling",
                _=>"running"
            };
            var logsReadable=RuntimeCommandLogCodec.TryDeserialize(r.GetString(5),out var logs);
            RuntimeStepDiagnostic? diagnostic = node?.Component.ComponentId == MediaIntegrityPolicy.Component
                ? ReadIntegrityDiagnostic(checkpoint)
                : null;
            if (!logsReadable)
                diagnostic ??= new("command_log_unreadable", "L’audit des commandes est illisible et n’a pas été modifié.");
            result.Add(new(nodeId,node?.Name??descriptor?.Name??nodeId,r.GetGuid(1),descriptor?.Kind??"unknown",status,
                r.GetFieldValue<DateTimeOffset>(2),completedAt,outputId,outputLabel,checkpoint?.NextNodeId,logs,diagnostic));
        }
        return result;
    }
    internal static RuntimeStepDiagnostic? ReadIntegrityDiagnostic(RuntimeCheckpoint? checkpoint)
    {
        if (checkpoint?.Context?.Variables is not { } variables
            || !variables.TryGetValue(MediaIntegrityPolicy.ReceiptVariable, out var report) || report.ValueKind != JsonValueKind.Object
            || !report.TryGetProperty("reason", out var reason) || reason.ValueKind != JsonValueKind.String
            || MediaIntegrityPolicy.DescribeFailure(reason.GetString()) is not { } message) return null;
        return new(reason.GetString()!, message);
    }

    internal static async Task<bool> CreateInTransactionAsync(CreateExecutionRequest request,DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        await using var cmd = Sql.Command("""
   INSERT INTO executions(id,media,library_name,flow_name,flow_version,flow_definition,state,attempt,detail)
   VALUES(@id,@media,@library,@flow,@version,@definition,'queued',1,'En attente du moteur d’exécution.') ON CONFLICT(id) DO NOTHING
   """, c, tx);
        cmd.Parameters.AddWithValue("id", request.Id); cmd.Parameters.AddWithValue("media", request.Media);
        cmd.Parameters.AddWithValue("library", request.Library); cmd.Parameters.AddWithValue("flow", request.Flow);
        cmd.Parameters.AddWithValue("version", request.FlowVersion); cmd.Parameters.AddWithValue("definition", SqlKind.Json, request.FlowDefinition.GetRawText());
        var inserted = await cmd.ExecuteNonQueryAsync(ct) > 0;
        if (inserted) await AddEvent(c, tx, request.Id, Guid.NewGuid(), 0, "queued", "En attente du moteur d’exécution.", ct);
        return inserted;
    }
    private async Task<bool> IsPeerNeutralizedAsync(Guid peer, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT EXISTS(SELECT 1 FROM task_neutralizations WHERE peer_id=@peer AND released_at IS NULL)");
        cmd.Parameters.AddWithValue("peer", peer);
        return await cmd.ScalarBoolAsync(ct);
    }

    public async Task<bool> CancelAsync(Guid id, CancellationToken ct, int? expectedRevision = null)
    {
        if(coordinator is not null && ownership is not null && await ownership.GetAsync(id,ct) is {} owner && !ownership.IsLocalPeer(owner.OwnerPeerId))
        {
            var result = await coordinator.ControlAsync(owner.OwnerPeerId,"cancel",id,expectedRevision,ct);
            if(result.Success) return true;
            if(!await IsPeerNeutralizedAsync(owner.OwnerPeerId,ct)) return false;
        }
        await using var c = await dataSource.OpenConnectionAsync(ct); await using var tx = await c.BeginTransactionAsync(ct);
        await using var cmd = Sql.Command($"""
   UPDATE executions SET state=CASE WHEN state='queued' THEN 'cancelled' ELSE 'cancelling' END,
   detail=CASE WHEN state='queued' THEN 'Annulée avant démarrage.' ELSE 'Annulation demandée ; attente de confirmation du worker.' END,
   finished_at=CASE WHEN state='queued' THEN clock_timestamp() ELSE NULL END,
   updated_at=clock_timestamp(),revision=revision+1 WHERE id=@id AND state IN ('queued','running')
   AND (@expectedRevision IS NULL OR revision=@expectedRevision)
   AND ({TaskOwnershipRepository.ActiveSql("@id",control:true)}
     OR (NOT EXISTS(SELECT 1 FROM task_ownership WHERE execution_id=@id) AND NOT EXISTS(SELECT 1 FROM execution_work WHERE execution_id=@id))
     OR EXISTS(SELECT 1 FROM task_ownership own JOIN task_neutralizations stopped ON stopped.peer_id=own.owner_peer_id WHERE own.execution_id=@id AND stopped.released_at IS NULL))
   RETURNING revision,state,detail
   """, c, tx); cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("expectedRevision", (object?)expectedRevision ?? DBNull.Value);
        int revision; string state, detail;
        await using (var r = await cmd.ExecuteReaderAsync(ct)) { if (!await r.ReadAsync(ct)) return false; revision = r.GetInt32(0); state = r.GetString(1); detail = r.GetString(2); }
        await AddEvent(c, tx, id, Guid.NewGuid(), revision, state, detail, ct); await tx.CommitAsync(ct);
        broadcaster?.Publish(id);
        return true;
    }
    public async Task<Guid?> RetryAsync(Guid id, CancellationToken ct)
    {
        if(coordinator is not null && ownership is not null && await ownership.GetAsync(id,ct) is {} owner && !ownership.IsLocalPeer(owner.OwnerPeerId))
        {
            var result=await coordinator.ControlAsync(owner.OwnerPeerId,"retry",id,null,ct);
            if(result.Success&&Guid.TryParse(result.Payload,out var remoteCreated)) return remoteCreated;
            if(!await IsPeerNeutralizedAsync(owner.OwnerPeerId,ct)) return null;
        }
        await using var c = await dataSource.OpenConnectionAsync(ct); await using var tx = await c.BeginTransactionAsync(ct);
        var created=await RetryInTransactionAsync(id,null,null,"Nouvelle tentative demandée.",c,tx,ct,ownership);
        if(created is null) return null;
        await tx.CommitAsync(ct);
        broadcaster?.Publish(created);
        return created;
    }

    /// <summary>Creates a fresh attempt from the original source for a cancelled execution.</summary>
    public async Task<Guid?> ResumeAsync(Guid id, CancellationToken ct)
    {
        if(coordinator is not null && ownership is not null && await ownership.GetAsync(id,ct) is {} owner && !ownership.IsLocalPeer(owner.OwnerPeerId))
        {
            var result=await coordinator.ControlAsync(owner.OwnerPeerId,"resume",id,null,ct);
            if(result.Success&&Guid.TryParse(result.Payload,out var remoteCreated)) return remoteCreated;
            if(!await IsPeerNeutralizedAsync(owner.OwnerPeerId,ct)) return null;
        }
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var next = Guid.NewGuid();
        const string detail = "Nouvelle exécution créée depuis la source après interruption.";
        await using (var create = Sql.Command("""
            INSERT INTO executions(id,media,library_name,flow_name,flow_version,flow_definition,state,attempt,retry_of,detail)
            SELECT @next,e.media,e.library_name,e.flow_name,e.flow_version,e.flow_definition,'queued',e.attempt+1,e.id,@detail
            FROM executions e
            LEFT JOIN execution_work source ON source.execution_id=e.id
            LEFT JOIN task_ownership own_source ON own_source.execution_id=e.id
            WHERE e.id=@id AND e.state='cancelled'
              AND (source.library_id IS NOT NULL OR own_source.library_id IS NOT NULL)
              AND NOT EXISTS(SELECT 1 FROM task_ownership own WHERE own.execution_id=e.id AND own.state='quarantined')
              AND NOT EXISTS(SELECT 1 FROM task_quarantines quarantine WHERE quarantine.execution_id=e.id)
              AND NOT EXISTS(
                SELECT 1 FROM execution_work sibling
                JOIN executions active ON active.id=sibling.execution_id
                WHERE sibling.library_id=COALESCE(source.library_id,own_source.library_id)
                  AND sibling.media_key=COALESCE(source.media_key,own_source.media_key)
                  AND sibling.execution_id<>e.id AND active.state IN ('queued','running','cancelling'))
              AND NOT EXISTS(
                SELECT 1 FROM task_ownership sibling
                JOIN executions active ON active.id=sibling.execution_id
                WHERE sibling.library_id=COALESCE(source.library_id,own_source.library_id)
                  AND sibling.media_key=COALESCE(source.media_key,own_source.media_key)
                  AND sibling.execution_id<>e.id AND active.state IN ('queued','running','cancelling'))
            ON CONFLICT(retry_of) DO NOTHING RETURNING id
            """, c, tx))
        {
            create.Parameters.AddWithValue("id", id);
            create.Parameters.AddWithValue("next", next);
            create.Parameters.AddWithValue("detail", detail);
            if (await create.ScalarGuidAsync(ct) is not { } created) return null;
            next = created;
        }
        await AddEvent(c, tx, next, Guid.NewGuid(), 0, "queued", detail, ct);
        await using (var work = Sql.Command("""
            INSERT INTO execution_work(execution_id,library_id,media_path,media_key,available_at,placement_plan)
            SELECT @next,COALESCE(w.library_id,o.library_id),COALESCE(w.media_path,e.media),COALESCE(w.media_key,o.media_key,upper(e.media)),clock_timestamp(),NULL
            FROM executions e LEFT JOIN execution_work w ON w.execution_id=e.id LEFT JOIN task_ownership o ON o.execution_id=e.id
            WHERE e.id=@id AND COALESCE(w.library_id,o.library_id) IS NOT NULL
            """, c, tx))
        {
            work.Parameters.AddWithValue("id", id);
            work.Parameters.AddWithValue("next", next);
            if (await work.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("L’exécution d’origine ne possède plus de source à reprendre.");
        }
        if(ownership is not null) await ownership.CreateAsync([next],c,tx,ct);
        await tx.CommitAsync(ct);
        broadcaster?.Publish(next);
        return next;
    }

    internal static async Task<Guid?> RetryInTransactionAsync(Guid id,RuntimeHandoffSelector? selector,Guid? previousWorker,string detail,
        DbConnection c,DbTransaction tx,CancellationToken ct,TaskOwnershipRepository? ownership=null)
    {
        var next=Guid.NewGuid();
        await using(var cmd=Sql.Command("""
          INSERT INTO executions(id,media,library_name,flow_name,flow_version,flow_definition,state,attempt,retry_of,detail)
          SELECT @next,media,library_name,flow_name,flow_version,flow_definition,'queued',attempt+1,id,@detail
          FROM executions WHERE id=@id AND state='failed' ON CONFLICT(retry_of) DO NOTHING RETURNING id
          """,c,tx))
        {
            cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("next",next);cmd.Parameters.AddWithValue("detail",detail);
            if(await cmd.ScalarGuidAsync(ct) is not {} created) return null;
            next=created;
        }
        await AddEvent(c,tx,next,Guid.NewGuid(),0,"queued",detail,ct);
        await using(var work=Sql.Command("""
          INSERT INTO execution_work(execution_id,library_id,media_path,media_key,handoff_selector,previous_worker_node_id,placement_plan)
          SELECT @next,COALESCE(w.library_id,o.library_id),COALESCE(w.media_path,e.media),COALESCE(w.media_key,o.media_key,upper(e.media)),@selector,@previous,NULL
          FROM executions e LEFT JOIN execution_work w ON w.execution_id=e.id LEFT JOIN task_ownership o ON o.execution_id=e.id
          WHERE e.id=@id AND COALESCE(w.library_id,o.library_id) IS NOT NULL
          """,c,tx))
        {
            work.Parameters.AddWithValue("next",next);work.Parameters.AddWithValue("id",id);
            work.Parameters.AddWithValue("selector",SqlKind.Json,selector is null?DBNull.Value:JsonSerializer.Serialize(selector,Json));
            work.Parameters.AddWithValue("previous",SqlKind.Uuid,(object?)previousWorker??DBNull.Value);
            if(await work.ExecuteNonQueryAsync(ct)==0) throw new InvalidOperationException("L’exécution d’origine ne possède pas de contexte de média à relancer.");
        }
        if(ownership is not null) await ownership.CreateAsync([next],c,tx,ct);
        return next;
    }
    // Called by the future orchestrator after its lease and output validation checks.
    public async Task<bool> RecordAsync(Guid id, RecordExecutionProgress update, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct); await using var tx = await c.BeginTransactionAsync(ct);
        var recorded=await RecordInTransactionAsync(id,update,c,tx,ct);
        if(recorded) { await tx.CommitAsync(ct); broadcaster?.Publish(id); }
        return recorded;
    }
    internal static async Task<bool> RecordInTransactionAsync(Guid id, RecordExecutionProgress update, DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        if (update.EventId == Guid.Empty || update.ExpectedRevision < 0 || update.Progress is < 0 or > 100 || update.Detail.Length > 4000 || !AllowedStates.Contains(update.State)) return false;
        await using (var rowLock = Sql.Command("SELECT id FROM executions WHERE id=@id FOR UPDATE", c, tx))
        { rowLock.Parameters.AddWithValue("id", id); if (await rowLock.ExecuteScalarAsync(ct) is null) return false; }
        await using (var duplicate = Sql.Command("SELECT EXISTS(SELECT 1 FROM execution_events WHERE id=@event AND execution_id=@id)", c, tx))
        { duplicate.Parameters.AddWithValue("event", update.EventId); duplicate.Parameters.AddWithValue("id", id); if ((await duplicate.ScalarBoolAsync(ct))) return true; }
        await using var cmd = Sql.Command($"""
   UPDATE executions SET state=@state,node_id=COALESCE(node_id,@node),progress=@progress,detail=@detail,
   revision=revision+1,updated_at=clock_timestamp(),
   started_at=CASE WHEN @state='running' THEN COALESCE(started_at,clock_timestamp()) ELSE started_at END,
   finished_at=CASE WHEN @state IN ('succeeded','failed','cancelled','deferred') THEN clock_timestamp() ELSE finished_at END
   WHERE id=@id AND revision=@revision AND (node_id IS NULL OR node_id=@node)
   AND (NOT EXISTS(SELECT 1 FROM execution_work w WHERE w.execution_id=@id) OR {TaskOwnershipRepository.ActiveSql("@id")} OR (state='cancelling' AND @state IN ('cancelled','failed') AND {TaskOwnershipRepository.ActiveSql("@id",true)}))
   AND ((state='queued' AND @state IN ('running','failed','cancelled'))
    OR (state='running' AND @state IN ('running','succeeded','failed','cancelled','deferred'))
    OR (state='cancelling' AND @state IN ('cancelled','failed')))
   AND (@state<>'running' OR @node IS NOT NULL)
   RETURNING revision
   """, c, tx);
        cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("revision", update.ExpectedRevision);
        cmd.Parameters.AddWithValue("state", update.State); cmd.Parameters.AddWithValue("node", SqlKind.Uuid, (object?)update.NodeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("progress", SqlKind.Decimal, (object?)update.Progress ?? DBNull.Value); cmd.Parameters.AddWithValue("detail", update.Detail);
        if (await cmd.ScalarInt32Async(ct) is not {} revision) return false;
        await AddEvent(c, tx, id, update.EventId, revision, update.State, update.Detail, ct); return true;
    }
    private static readonly HashSet<string> AllowedStates = [..ExecutionStates.ProgressRecordable];
    internal static async Task AddEvent(DbConnection c, DbTransaction tx, Guid id, Guid eventId, int revision, string state, string detail, CancellationToken ct)
    {
        await using var cmd = Sql.Command("INSERT INTO execution_events(id,execution_id,revision,state,detail) VALUES(@event,@id,@revision,@state,@detail)", c, tx);
        cmd.Parameters.AddWithValue("event", eventId); cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("revision",
            revision); cmd.Parameters.AddWithValue("state", state); cmd.Parameters.AddWithValue("detail", detail); await cmd.ExecuteNonQueryAsync(ct);
    }
}
