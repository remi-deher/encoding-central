using Encoding.Api.Infrastructure.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;

namespace Encoding.Api.Flows;

/// <summary>Replays the transactional outbox and periodically checks completed snapshots.</summary>
public sealed class FlowBackupService(DbDataSource dataSource, IConfiguration configuration, ILogger<FlowBackupService> logger) : BackgroundService
{
    private readonly string directory = Path.GetFullPath(configuration["FlowBackups:Directory"]
        ?? (configuration["Node:DataDirectory"] is { Length: > 0 } dataDir ? Path.Combine(dataDir, "flow-backups") : Path.Combine(AppContext.BaseDirectory, "flow-backups")));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {

                var processed = 0;
                var token = stoppingToken;
                while(processed++ < 50) { var worked=await ProcessOneAsync(false,token); worked|=await ProcessOneAsync(true,token); if(!worked) break; }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception,"Impossible de traiter la file de sauvegardes des flows."); }
            try { await Task.Delay(TimeSpan.FromSeconds(5),stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    public async Task<bool> ProcessOneAsync(bool draft,CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        Guid id; int version; string document;
        var select=draft ? """
            SELECT b.flow_id,b.revision,b.definition::text FROM flow_draft_backups b WHERE b.next_attempt_at<=clock_timestamp()
            ORDER BY b.next_attempt_at,b.flow_id,b.revision LIMIT 1 FOR UPDATE OF b SKIP LOCKED
            """ : """
            SELECT b.flow_id,b.version,v.definition::text FROM flow_backup_jobs b
            JOIN flow_versions v ON v.flow_id=b.flow_id AND v.version=b.version
            WHERE b.next_attempt_at <= clock_timestamp() ORDER BY b.next_attempt_at,b.flow_id,b.version
            LIMIT 1 FOR UPDATE OF b SKIP LOCKED
            """;
        // Read-only admission hint. Reread under the transaction before claiming the job.
        await using (var available = Sql.Command(select.Replace(" FOR UPDATE OF b SKIP LOCKED", "", StringComparison.Ordinal), connection))
        await using (var reader = await available.ExecuteReaderAsync(ct))
            if (!await reader.ReadAsync(ct)) return false;
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var command = Sql.Command(select,connection,tx))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return false;
            id=reader.GetGuid(0); version=reader.GetInt32(1); document=reader.GetString(2);
        }
        var status="ready";
        try {
            if(draft) { using var json=JsonDocument.Parse(document); document=JsonSerializer.Serialize(new {kind="draft",draftRevision=version,
                definition=json.RootElement},FlowRepository.JsonOptions); }
            await WriteSnapshotAsync(id,version,document,ct,draft);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { status="failed"; logger.LogWarning(exception,"Sauvegarde impossible pour le flow {FlowId}, version {Version}.",id,version); }
        var table=draft?"flow_draft_backups":"flow_backup_jobs"; var number=draft?"revision":"version";
        await using var update=Sql.Command($"UPDATE {table} SET status=@status,checked_at=clock_timestamp(),next_attempt_at=clock_timestamp()+@delay WHERE flow_id=@id AND {number}=@version",
            connection,tx);
        update.Parameters.AddWithValue("id",id); update.Parameters.AddWithValue("version",version);
        update.Parameters.AddWithValue("status",status); update.Parameters.AddWithValue("delay",status=="ready" ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(30));
        await update.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct); return true;
    }

    private async Task WriteSnapshotAsync(Guid id,int version,string document,CancellationToken ct,bool draft=false)
    {
        Directory.CreateDirectory(directory);
        var target=Path.Combine(directory,draft?$"{id:D}.draft.r{version}.json":$"{id:D}.v{version}.json");
        var bytes=System.Text.Encoding.UTF8.GetBytes(document);
        async Task VerifyAsync()
        {
            var existing=await File.ReadAllBytesAsync(target,ct);
            if (!SHA256.HashData(existing).AsSpan().SequenceEqual(SHA256.HashData(bytes)))
                throw new IOException("L’instantané existant ne correspond pas à la version en base. Il est conservé pour diagnostic.");
            Encoding.Core.AtomicFilePublication.Confirm(target);
        }
        if (File.Exists(target)) { await VerifyAsync(); return; }
        var temporary=Path.Combine(directory,$".{id:D}.{version}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough | FileOptions.Asynchronous))
            { await stream.WriteAsync(bytes,ct); await stream.FlushAsync(ct); stream.Flush(true); }
            try { Encoding.Core.AtomicFilePublication.Publish(temporary,target); }
            catch (IOException) when (File.Exists(target)) { await VerifyAsync(); }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
