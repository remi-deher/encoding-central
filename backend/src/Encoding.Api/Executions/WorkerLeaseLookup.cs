using System.Data.Common;
using Encoding.Api.Infrastructure.Data;

namespace Encoding.Api.Executions;

/// <summary>Baux d'exécution actuellement tenus par un worker (identifiant → époque), sans engager la base à chaque trame.</summary>
public interface IWorkerLeaseLookup
{
    Task<IReadOnlyDictionary<Guid, long>> ActiveAsync(Guid workerId, CancellationToken ct);
}

/// <summary>Lecture unique de tous les baux valides d'un worker ; la même règle que <c>FenceAsync</c> (bail non expiré, exécution en cours).</summary>
public sealed class SqlWorkerLeaseLookup(DbDataSource db) : IWorkerLeaseLookup
{
    public async Task<IReadOnlyDictionary<Guid, long>> ActiveAsync(Guid workerId, CancellationToken ct)
    {
        await using var command = db.CreateCommand("""
            SELECT w.execution_id, w.lease_epoch FROM execution_work w JOIN executions e ON e.id=w.execution_id
            WHERE w.worker_id=@worker AND w.lease_until>(strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z') AND e.state IN ('running','cancelling')
            """);
        command.Parameters.AddWithValue("worker", workerId);
        var leases = new Dictionary<Guid, long>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetValue(0) switch { Guid guid => guid, string text when Guid.TryParse(text, out var parsed) => parsed, _ => Guid.Empty };
            if (id != Guid.Empty) leases[id] = reader.GetInt64(1);
        }
        return leases;
    }
}
