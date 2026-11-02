using System.Data.Common;
using System.Text.Json;
using Encoding.Api.Infrastructure.Data;
using Encoding.Contracts.Executions;

namespace Encoding.Api.Executions;

/// <summary>Bounded command audit, keyed by invocation and command identity. Caller must hold the execution fence.</summary>
internal static class RuntimeCommandJournal
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static RuntimeCommandLog[] Merge(IEnumerable<RuntimeCommandLog> existing, IEnumerable<RuntimeCommandLog> incoming)
    {
        var merged = existing.ToDictionary(log => log.Id);
        foreach (var log in incoming)
            if (!merged.TryGetValue(log.Id, out var previous) || previous.FinishedAt is null)
                merged[log.Id] = log;
        return merged.Values.OrderBy(log => log.StartedAt).TakeLast(64).ToArray();
    }

    internal static async Task<bool> StoreAsync(Guid execution, IReadOnlyList<RuntimeStepCommand>? commands,
        DbConnection connection, DbTransaction transaction, CancellationToken ct, bool allowCompletedOperations = false)
    {
        if (commands is null || commands.Count == 0) return true;
        if (commands.Count > 64 || commands.Any(item => item.InvocationId == Guid.Empty || string.IsNullOrWhiteSpace(item.NodeId)
            || item.NodeId.Length > 128 || !RuntimeRepository.ValidCommandLog(item.Command))
            || allowCompletedOperations && commands.Any(item => item.Command.Kind != "operation")
            || commands.Select(item => item.Command.Id).Distinct().Count() != commands.Count
            || JsonSerializer.SerializeToUtf8Bytes(commands, Json).Length > 524288) return false;
        foreach (var group in commands.GroupBy(item => (item.NodeId, item.InvocationId)))
        {
            await using var read = Sql.Command("SELECT command_logs,completed_at FROM execution_steps WHERE execution_id=@id AND node_id=@node AND invocation_id=@invocation", connection, transaction);
            read.Parameters.AddWithValue("id", execution); read.Parameters.AddWithValue("node", group.Key.NodeId); read.Parameters.AddWithValue("invocation", group.Key.InvocationId);
            await using var reader = await read.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return false;
            if (!RuntimeCommandLogCodec.TryDeserialize(reader.GetString(0), out var existing)) return false;
            var completed = !reader.IsDBNull(1);
            await reader.DisposeAsync();
            if (completed && !allowCompletedOperations) continue; // A late heartbeat cannot rewrite a completed step.
            var logs = Merge(existing, group.Select(item => item.Command));
            var serialized = JsonSerializer.Serialize(logs, Json);
            if (System.Text.Encoding.UTF8.GetByteCount(serialized) > 524288) return false;
            await using var update = Sql.Command("UPDATE execution_steps SET command_logs=@logs WHERE execution_id=@id AND node_id=@node AND invocation_id=@invocation AND ((@completed AND completed_at IS NOT NULL) OR (NOT @completed AND completed_at IS NULL))",
                connection, transaction);
            update.Parameters.AddWithValue("id", execution); update.Parameters.AddWithValue("node",
                group.Key.NodeId); update.Parameters.AddWithValue("invocation", group.Key.InvocationId); update.Parameters.AddWithValue("logs", SqlKind.Json,
                serialized);
            update.Parameters.AddWithValue("completed", completed);
            if (await update.ExecuteNonQueryAsync(ct) != 1) return false;
        }
        return true;
    }
}
