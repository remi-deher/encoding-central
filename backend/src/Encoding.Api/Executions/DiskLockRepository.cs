using System.Data.Common;
using Encoding.Api.Infrastructure.Data;
using Encoding.Contracts.Executions;

namespace Encoding.Api.Executions;

/// <summary>
/// Verrous de disque (architecture 0058) : une exécution à la fois par disque déclaré, bail de 60 secondes renouvelé avec celui de l'exécution
/// (<see cref="RuntimeRepository.RenewAsync"/>), libéré à la fin, à l'attente, au relais ou sur demande. Une exécution perdue laisse expirer son bail.
/// </summary>
public sealed class DiskLockRepository(DbDataSource dataSource)
{
    /// <summary>Prend le verrou, ou confirme que l'exécution le tient déjà. Nul si le bail ou l'époque de l'exécution ne sont plus valides.</summary>
    public async Task<DiskLockResponse?> TryAcquireAsync(Guid execution, Guid worker, DiskLockRequest request, CancellationToken ct)
    {
        var disk = DiskLockKey.Normalize(request.Disk);
        await using var c = await dataSource.OpenConnectionAsync(ct); await using var tx = await c.BeginTransactionAsync(ct);
        if (!await RuntimeRepository.FenceAsync(execution, worker, request.Epoch, c, tx, ct)) return null;
        await using (var take = Sql.Command("""
          INSERT INTO disk_locks(disk_key,execution_id,lease_epoch,lease_until) VALUES(@disk,@id,@epoch,clock_timestamp()+interval '180 seconds')
          ON CONFLICT(disk_key) DO UPDATE SET execution_id=@id,lease_epoch=@epoch,lease_until=clock_timestamp()+interval '180 seconds',
            acquired_at=CASE WHEN disk_locks.execution_id=@id THEN disk_locks.acquired_at ELSE clock_timestamp() END
          WHERE disk_locks.execution_id=@id OR disk_locks.lease_until<=clock_timestamp() RETURNING execution_id
          """, c, tx))
        {
            take.Parameters.AddWithValue("disk", disk); take.Parameters.AddWithValue("id", execution); take.Parameters.AddWithValue("epoch", request.Epoch);
            if (await take.ExecuteScalarAsync(ct) is not null) { await tx.CommitAsync(ct); return new(true, disk); }
        }
        await using var holder = Sql.Command("SELECT execution_id FROM disk_locks WHERE disk_key=@disk", c, tx);
        holder.Parameters.AddWithValue("disk", disk);
        var held = await holder.ScalarGuidAsync(ct);
        await tx.CommitAsync(ct);
        return new(false, disk, held);
    }

    /// <summary>Libère le verrou si l'exécution le tient ; sans effet sinon (idempotent). Nul si le bail ou l'époque ne sont plus valides : un worker périmé ne libère jamais le verrou d'un autre.</summary>
    public async Task<bool?> ReleaseAsync(Guid execution, Guid worker, DiskLockRequest request, CancellationToken ct)
    {
        var disk = DiskLockKey.Normalize(request.Disk);
        await using var c = await dataSource.OpenConnectionAsync(ct); await using var tx = await c.BeginTransactionAsync(ct);
        if (!await RuntimeRepository.FenceAsync(execution, worker, request.Epoch, c, tx, ct)) return null;
        await using var delete = Sql.Command("DELETE FROM disk_locks WHERE disk_key=@disk AND execution_id=@id", c, tx);
        delete.Parameters.AddWithValue("disk", disk); delete.Parameters.AddWithValue("id", execution);
        var released = await delete.ExecuteNonQueryAsync(ct) > 0;
        await tx.CommitAsync(ct);
        return released;
    }
}
