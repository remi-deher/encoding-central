using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using Encoding.Api.Infrastructure.Data;

namespace Encoding.Api.Infrastructure;

public sealed partial class ClusterEntityReconciliation
{
    public static IReadOnlyList<string> CaptureTableNames => Tables.Select(table => table.Name)
        .Append(ProcessingReceiptTable.Name).ToArray();

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> CaptureTableColumns => Tables.Append(ProcessingReceiptTable)
        .ToDictionary(table => table.Name, table => (IReadOnlyList<string>)table.Columns
            .Select(column => table.Name == ProcessingReceiptTable.Name && column == "relative_path" ? "file_id" : column).ToArray(), StringComparer.Ordinal);

    private sealed record CaptureSelection(string Table, long Revision, bool FullScan, HashSet<string> Keys);

    private static async Task<Dictionary<string, CaptureSelection>> ReadCaptureSelectionsAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        var selections = new Dictionary<string, CaptureSelection>(StringComparer.Ordinal);
        await using (var tables = Sql.Command("SELECT entity_type,revision,captured_revision,full_scan_revision FROM cluster_sync_capture_tables WHERE revision>captured_revision",
            connection, transaction))
        await using (var rows = await tables.ExecuteReaderAsync(ct))
            while (await rows.ReadAsync(ct))
                selections.Add(rows.GetString(0), new(rows.GetString(0), rows.GetInt64(1), rows.GetInt64(2) == 0 || rows.GetInt64(3) > rows.GetInt64(2), new(StringComparer.Ordinal)));

        await using var keys = Sql.Command("""
            SELECT d.entity_type,d.entity_key FROM cluster_sync_dirty_entities d
            JOIN cluster_sync_capture_tables t ON t.entity_type=d.entity_type
            WHERE t.revision>t.captured_revision AND t.captured_revision>0 AND t.full_scan_revision<=t.captured_revision
            """, connection, transaction);
        await using var reader = await keys.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            // SQLite json_object and System.Text.Json escape non-ASCII differently. Entity-state
            // keys use MakeKey's serializer, including for composite keys containing media paths.
            var canonical = JsonNode.Parse(reader.GetString(1))!.ToJsonString(JsonOptions);
            selections[reader.GetString(0)].Keys.Add(canonical);
        }
        return selections;
    }

    private static string SelectedKeysPredicate(EntityTable table, string? receiptAlias = null)
    {
        var columns = table.KeyColumns.Select(column => receiptAlias is null ? column : (column == "relative_path" ? "m." : receiptAlias + ".") + column);
        var values = table.KeyColumns.Select(column => "json_extract(value,'$." + column + "')");
        return $"({string.Join(',', columns)}) IN(SELECT {string.Join(',', values)} FROM json_each(@capture_keys))";
    }

    private static void BindSelectedKeys(DbCommand command, CaptureSelection selection)
    {
        if (!selection.FullScan) command.Parameters.AddWithValue("capture_keys", JsonSerializer.Serialize(selection.Keys, JsonOptions));
    }

    private static async Task AcknowledgeCaptureAsync(DbConnection connection, DbTransaction transaction,
        IEnumerable<CaptureSelection> selections, CancellationToken ct)
    {
        foreach (var selection in selections)
        {
            await using var acknowledge = Sql.Command("""
                UPDATE cluster_sync_capture_tables SET captured_revision=@revision
                  WHERE entity_type=@type AND captured_revision<@revision;
                DELETE FROM cluster_sync_dirty_entities WHERE entity_type=@type AND revision<=@revision;
                """, connection, transaction);
            acknowledge.Parameters.AddWithValue("type", selection.Table);
            acknowledge.Parameters.AddWithValue("revision", selection.Revision);
            await acknowledge.ExecuteNonQueryAsync(ct);
        }
    }
}
