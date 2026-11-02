using Encoding.Api.Infrastructure.Data;
using System.Data.Common;

using Encoding.Contracts.Libraries;
using Encoding.Contracts.Nodes;
namespace Encoding.Api.Libraries;

public sealed class LibraryProcessingRepository(DbDataSource dataSource)
{
    public async Task<LibraryProcessingSettings> GetAsync(Guid library,CancellationToken ct)
    {
        bool watch=true;int interval=60;int? maximum=null;int revision=0;
        await using(var cmd=dataSource.CreateCommand("SELECT watch_enabled,scan_interval_seconds,max_concurrent_tasks,revision FROM library_processing_settings WHERE library_id=@id"))
        {
            cmd.Parameters.AddWithValue("id",library);await using var r=await cmd.ExecuteReaderAsync(ct);
            if(await r.ReadAsync(ct)){watch=r.GetBoolean(0);interval=r.GetInt32(1);maximum=r.IsDBNull(2)?null:r.GetInt32(2);revision=r.GetInt32(3);}
        }
        var limits=new List<LibraryNodeProcessingLimit>();
        await using(var cmd=dataSource.CreateCommand("SELECT node_id,max_concurrent_tasks FROM library_node_processing_limits WHERE library_id=@id ORDER BY node_id"))
        {
            cmd.Parameters.AddWithValue("id",library);await using var r=await cmd.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))limits.Add(new(r.GetGuid(0),r.GetInt32(1)));
        }
        return new(watch,interval,maximum,limits,revision);
    }
    public async Task<bool> SaveAsync(Guid library,SaveLibraryProcessingSettings request,CancellationToken ct)
    {
        if(request.ScanIntervalSeconds is <15 or >86400 || request.MaxConcurrentTasks is <1 or >1000 || request.NodeLimits is null
            || request.NodeLimits.Count>1000 || request.NodeLimits.Any(limit=>limit is null || limit.MaxConcurrentTasks is <1 or >1000)
            || request.NodeLimits.Select(limit=>limit.NodeId).Distinct().Count()!=request.NodeLimits.Count || request.ExpectedRevision is <0 or int.MaxValue)
            throw new ArgumentException("Limites invalides : 1 à 1 000 traitements ; intervalle de 15 à 86 400 secondes.");
        await using var c=await dataSource.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        await using var libraryLock=Sql.Command("SELECT id FROM libraries WHERE id=@id FOR UPDATE",c,tx);
        libraryLock.Parameters.AddWithValue("id",library);if(await libraryLock.ExecuteScalarAsync(ct) is null)return false;
        await using var revision=Sql.Command("SELECT revision FROM library_processing_settings WHERE library_id=@id",c,tx);
        revision.Parameters.AddWithValue("id",library);if((await revision.ScalarInt32Async(ct) ?? 0)!=request.ExpectedRevision)return false;
        foreach(var limit in request.NodeLimits)
        {
            await using var eligible=Sql.Command("SELECT EXISTS(SELECT 1 FROM library_node_accesses a JOIN cluster_nodes n ON n.id=a.node_id WHERE a.library_id=@library AND a.node_id=@node AND a.mode<>'unsupported' AND n.deleted_at IS NULL AND 'worker'=ANY(n.roles))",
                c,tx);
            eligible.Parameters.AddWithValue("library",library);eligible.Parameters.AddWithValue("node",limit.NodeId);
            if(!(await eligible.ScalarBoolAsync(ct)))throw new ArgumentException("Les limites par nœud exigent un worker disposant d’un accès à cette bibliothèque.");
        }
        await using var save=Sql.Command("""
          INSERT INTO library_processing_settings(library_id,watch_enabled,scan_interval_seconds,max_concurrent_tasks,revision)
          VALUES(@id,@watch,@interval,@maximum,@revision)
          ON CONFLICT(library_id) DO UPDATE SET watch_enabled=@watch,scan_interval_seconds=@interval,max_concurrent_tasks=@maximum,revision=@revision;
          DELETE FROM library_node_processing_limits WHERE library_id=@id;
          """,c,tx);
        save.Parameters.AddWithValue("id",library);save.Parameters.AddWithValue("watch",request.WatchEnabled);save.Parameters.AddWithValue("interval",request.ScanIntervalSeconds);
        save.Parameters.AddWithValue("maximum",SqlKind.Integer,(object?)request.MaxConcurrentTasks??DBNull.Value);save.Parameters.AddWithValue("revision",request.ExpectedRevision+1);
        await save.ExecuteNonQueryAsync(ct);
        foreach(var limit in request.NodeLimits)
        {
            await using var insert=Sql.Command("INSERT INTO library_node_processing_limits(library_id,node_id,max_concurrent_tasks) VALUES(@library,@node,@maximum)",c,tx);
            insert.Parameters.AddWithValue("library",library);insert.Parameters.AddWithValue("node",limit.NodeId);insert.Parameters.AddWithValue("maximum",
                limit.MaxConcurrentTasks);await insert.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);return true;
    }
    public async Task<NodeProcessingSettings?> NodeAsync(Guid node,CancellationToken ct)
    {
        await using var cmd=dataSource.CreateCommand("SELECT p.max_concurrent_tasks,COALESCE(p.max_concurrent_tasks,n.max_concurrent_tasks),COALESCE(p.revision,0),COALESCE(p.schedule_enabled,false),COALESCE(p.schedule_timezone,'UTC'),COALESCE(p.schedule_slots,repeat('1',672)) FROM cluster_nodes n LEFT JOIN node_processing_settings p ON p.node_id=n.id WHERE n.id=@id AND n.deleted_at IS NULL");
        cmd.Parameters.AddWithValue("id",node);await using var r=await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)?new(r.IsDBNull(0)?null:r.GetInt32(0),r.GetInt32(1),r.GetInt32(2),new NodeSchedule(r.GetBoolean(3),r.GetString(4),r.GetString(5))):null;
    }
    public async Task<bool> SaveNodeAsync(Guid node,SaveNodeProcessingSettings request,CancellationToken ct)
    {
        if(request.MaxConcurrentTasks is <1 or >1000 || request.ExpectedRevision is <0 or int.MaxValue)throw new ArgumentException("La limite doit être comprise entre 1 et 1 000, ou laissée vide.");
        var schedule=request.Schedule??NodeSchedule.AlwaysOpen; if(schedule.Validate() is {} scheduleError)throw new ArgumentException(scheduleError);
        await using var c=await dataSource.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        await using var locked=Sql.Command("SELECT id FROM cluster_nodes WHERE id=@id AND deleted_at IS NULL FOR UPDATE",c,tx);
        locked.Parameters.AddWithValue("id",node);if(await locked.ExecuteScalarAsync(ct) is null)return false;
        await using var current=Sql.Command("SELECT revision FROM node_processing_settings WHERE node_id=@id",c,tx);current.Parameters.AddWithValue("id",node);
        if((await current.ScalarInt32Async(ct) ?? 0)!=request.ExpectedRevision)return false;
        await using var save=Sql.Command("INSERT INTO node_processing_settings(node_id,max_concurrent_tasks,revision,schedule_enabled,schedule_timezone,schedule_slots) VALUES(@id,@max,@revision,@enabled,@zone,@slots) ON CONFLICT(node_id) DO UPDATE SET max_concurrent_tasks=@max,revision=@revision,schedule_enabled=@enabled,schedule_timezone=@zone,schedule_slots=@slots",
            c,tx);
        save.Parameters.AddWithValue("id",node);save.Parameters.AddWithValue("max",SqlKind.Integer,(object?)request.MaxConcurrentTasks??DBNull.Value);
            save.Parameters.AddWithValue("revision",request.ExpectedRevision+1);save.Parameters.AddWithValue("enabled",schedule.Enabled);
            save.Parameters.AddWithValue("zone",schedule.TimeZone);save.Parameters.AddWithValue("slots",schedule.Slots);
        await save.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);return true;
    }
}
