using Encoding.Api.Infrastructure.Data;
using Encoding.Contracts.Flows;
using System.Text.Json;

namespace Encoding.Api.Flows;

public sealed partial class FlowRepository
{
    public async Task<IReadOnlyList<FlowLibraryUsage>> UsageAsync(Guid id, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("SELECT l.id,l.name,a.flow_version,l.is_active FROM library_flow_assignments a JOIN libraries l ON l.id=a.library_id WHERE a.flow_id=@id ORDER BY l.name");
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var results = new List<FlowLibraryUsage>();
        while (await reader.ReadAsync(ct)) results.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), reader.GetBoolean(3)));
        return results;
    }

    public async Task<IReadOnlyList<FlowDraftBackupSummary>> DraftBackupsAsync(Guid id, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT b.revision,b.status,b.checked_at FROM flow_draft_backups b
            JOIN flows f ON f.id=b.flow_id WHERE f.id=@id AND f.deleted_at IS NULL
            ORDER BY b.revision DESC LIMIT 200
            """);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var results = new List<FlowDraftBackupSummary>();
        while (await reader.ReadAsync(ct)) results.Add(new(reader.GetInt32(0), reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2)));
        return results;
    }

    public async Task<FlowDefinition?> DraftBackupAsync(Guid id, int revision, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT b.definition::text FROM flow_draft_backups b JOIN flows f ON f.id=b.flow_id
            WHERE f.id=@id AND f.deleted_at IS NULL AND b.revision=@revision
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("revision", revision);
        return await command.ExecuteScalarAsync(ct) is string json
            ? JsonSerializer.Deserialize<FlowDefinition>(json, JsonOptions) : null;
    }
}
