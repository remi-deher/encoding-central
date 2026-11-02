using System.Data.Common;

namespace Encoding.Api.Libraries;
public sealed class LibraryMonitoringService(DbDataSource dataSource,LibraryWorkflowRepository workflow,ILogger<LibraryMonitoringService> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try
            {

                await using var cmd=dataSource.CreateCommand("""
                  INSERT INTO library_scans(id,library_id,media_type)
                  SELECT gen_random_uuid(),l.id,v.definition->>'mediaType'
                  FROM libraries l JOIN library_flow_assignments a ON a.library_id=l.id
                  JOIN flow_versions v ON v.flow_id=a.flow_id AND v.version=a.flow_version
                  LEFT JOIN library_processing_settings p ON p.library_id=l.id
                  WHERE l.is_active AND COALESCE(p.watch_enabled,true)
                  AND NOT EXISTS(SELECT 1 FROM library_scans s WHERE s.library_id=l.id AND s.created_at>clock_timestamp()-make_interval(secs=>COALESCE(p.scan_interval_seconds,60)))
                  ON CONFLICT DO NOTHING
                  """);
                await cmd.ExecuteNonQueryAsync(ct);
                await using var libraries=dataSource.CreateCommand("SELECT l.id FROM libraries l JOIN library_flow_assignments a ON a.library_id=l.id LEFT JOIN library_processing_settings p ON p.library_id=l.id WHERE l.is_active AND COALESCE(p.watch_enabled,true)");
                var ids=new List<Guid>();
                await using(var r=await libraries.ExecuteReaderAsync(ct)){while(await r.ReadAsync(ct))ids.Add(r.GetGuid(0));}
                foreach(var id in ids)await workflow.EnqueueAutomaticallyAsync(id,ct);
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception e){logger.LogError(e,"Surveillance des bibliothèques momentanément indisponible.");}
            try{await Task.Delay(TimeSpan.FromSeconds(5),ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
        }
    }
}
