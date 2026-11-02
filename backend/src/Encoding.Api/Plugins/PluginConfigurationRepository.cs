using Encoding.Api.Infrastructure.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Contracts.Plugins;
using Microsoft.AspNetCore.DataProtection;

namespace Encoding.Api.Plugins;

public sealed class PluginConfigurationRepository(DbDataSource dataSource, IDataProtectionProvider protection)
{
    private readonly IDataProtector protector = protection.CreateProtector("Encoding.PluginConfiguration.v1");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PluginConfigurationResponse> GetGlobalAsync(PluginManifest plugin, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("SELECT revision,encrypted_values FROM plugin_configurations WHERE plugin_id=@id");
        command.Parameters.AddWithValue("id", plugin.Id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? ToResponse(plugin, reader.GetInt32(0), Unprotect((byte[])reader[1]))
            : ToResponse(plugin, 0, new Dictionary<string, JsonElement>());
    }

    /// <summary>Valeurs globales déchiffrées, secrets compris, pour un usage serveur uniquement (jamais renvoyées à un client).</summary>
    public Task<Dictionary<string, JsonElement>> ReadGlobalValuesAsync(PluginManifest plugin, CancellationToken ct) =>
        ReadAsync("SELECT encrypted_values FROM plugin_configurations WHERE plugin_id=@id", "id", plugin.Id, ct);

    public async Task<PluginConfigurationResponse?> GetFlowAsync(Guid flowId, PluginManifest plugin, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("SELECT revision,encrypted_values FROM flow_plugin_configurations WHERE flow_id=@flow AND plugin_id=@plugin");
        command.Parameters.AddWithValue("flow", flowId);
        command.Parameters.AddWithValue("plugin", plugin.Id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? ToResponse(plugin, reader.GetInt32(0), Unprotect((byte[])reader[1]))
            : ToResponse(plugin, 0, new Dictionary<string, JsonElement>());
    }

    public async Task<PluginConfigurationResponse?> SaveGlobalAsync(
        PluginManifest plugin, SavePluginConfigurationRequest request, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var current = await ReadAsync("SELECT encrypted_values FROM plugin_configurations WHERE plugin_id=@id FOR UPDATE", connection, transaction, "id", plugin.Id, ct);
        var values = Merge(plugin.ConfigurationSchema, current, request.Values, request.ClearSecrets, flowOverride: false);
        var validation = Validate(plugin.ConfigurationSchema, values, requireRequired: true);
        if (validation is not null) throw new PluginConfigurationValidationException(validation);
        ValidateNodeDefaults(plugin, values, requireTarget: true);
        ValidatePathMappings(values);

        await using var command = Sql.Command("""
            INSERT INTO plugin_configurations(plugin_id,revision,encrypted_values)
            VALUES(@id,1,@values)
            ON CONFLICT(plugin_id) DO UPDATE SET revision=plugin_configurations.revision+1,
              encrypted_values=EXCLUDED.encrypted_values,updated_at=clock_timestamp()
            RETURNING revision
            """, connection, transaction);
        command.Parameters.AddWithValue("id", plugin.Id);
        command.Parameters.AddWithValue("values", SqlKind.Bytes, Protect(values));
        var revision = await command.ScalarInt32Async(ct) ?? throw new InvalidOperationException("La configuration du plugin n’a pas été enregistrée.");
        await transaction.CommitAsync(ct);
        return ToResponse(plugin, revision, values);
    }

    public async Task<IReadOnlyDictionary<string, JsonElement>> ResolveGlobalForConnectionTestAsync(
        PluginManifest plugin, IReadOnlyDictionary<string, JsonElement> submitted, CancellationToken ct)
    {
        var current = await ReadAsync("SELECT encrypted_values FROM plugin_configurations WHERE plugin_id=@id", "id", plugin.Id, ct);
        var values = Merge(plugin.ConfigurationSchema, current, submitted, clear: null, flowOverride: false);
        var validation = Validate(plugin.ConfigurationSchema, values, requireRequired: true);
        if (validation is not null) throw new PluginConfigurationValidationException(validation);
        return values;
    }

    public async Task<PluginConfigurationResponse?> SaveFlowAsync(
        Guid flowId, PluginManifest plugin, SavePluginConfigurationRequest request, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var ensureFlow = Sql.Command("INSERT INTO flows(id) VALUES(@flow) ON CONFLICT DO NOTHING", connection, transaction))
        {
            ensureFlow.Parameters.AddWithValue("flow", flowId);
            await ensureFlow.ExecuteNonQueryAsync(ct);
        }
        // Lock the parent row so concurrent first writes for this flow are serialized even when
        // the plugin override does not exist yet. The last committed request wins.
        await using (var flowLock = Sql.Command("SELECT id FROM flows WHERE id=@flow FOR UPDATE", connection, transaction))
        {
            flowLock.Parameters.AddWithValue("flow", flowId);
            await flowLock.ExecuteScalarAsync(ct);
        }
        var current = await ReadAsync(
            "SELECT encrypted_values FROM flow_plugin_configurations WHERE flow_id=@flow AND plugin_id=@plugin FOR UPDATE",
            connection, transaction, ("flow", (object)flowId), ("plugin", plugin.Id), ct);
        var values = Merge(plugin.ConfigurationSchema, current, request.Values, request.ClearSecrets, flowOverride: true);
        var validation = Validate(plugin.ConfigurationSchema, values, requireRequired: false);
        if (validation is not null) throw new PluginConfigurationValidationException(validation);
        ValidateNodeDefaults(plugin, values, requireTarget: false);
        ValidatePathMappings(values);

        await using var command = Sql.Command("""
            INSERT INTO flow_plugin_configurations(flow_id,plugin_id,revision,encrypted_values)
            VALUES(@flow,@plugin,1,@values)
            ON CONFLICT(flow_id,plugin_id) DO UPDATE SET revision=flow_plugin_configurations.revision+1,
              encrypted_values=EXCLUDED.encrypted_values,updated_at=clock_timestamp()
            RETURNING revision
            """, connection, transaction);
        command.Parameters.AddWithValue("flow", flowId);
        command.Parameters.AddWithValue("plugin", plugin.Id);
        command.Parameters.AddWithValue("values", SqlKind.Bytes, Protect(values));
        var revision = await command.ScalarInt32Async(ct) ?? throw new InvalidOperationException("La dérogation du plugin n’a pas été enregistrée.");
        await transaction.CommitAsync(ct);
        return ToResponse(plugin, revision, values);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>>> ResolveEffectiveAsync(
        Guid flowId, IEnumerable<string> pluginIds, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        return await ResolveEffectiveAsync(flowId, pluginIds, connection, transaction, ct);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>>> ResolveEffectiveAsync(
        Guid flowId, IEnumerable<string> pluginIds, DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>(StringComparer.Ordinal);
        var ids = pluginIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return result;
        await using var command = Sql.Command("""
            SELECT requested.plugin_id,global.encrypted_values,flow.encrypted_values
            FROM unnest(@plugins::text[]) AS requested(plugin_id)
            LEFT JOIN plugin_configurations global ON global.plugin_id=requested.plugin_id
            LEFT JOIN flow_plugin_configurations flow ON flow.flow_id=@flow AND flow.plugin_id=requested.plugin_id
            ORDER BY requested.plugin_id
            """, connection, transaction);
        command.Parameters.AddWithValue("plugins", SqlKind.TextArray, ids);
        command.Parameters.AddWithValue("flow", flowId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var values = reader.IsDBNull(1) ? new Dictionary<string, JsonElement>(StringComparer.Ordinal) : Unprotect((byte[])reader[1]);
            if (!reader.IsDBNull(2))
                foreach (var pair in Unprotect((byte[])reader[2])) values[pair.Key] = pair.Value;
            result[reader.GetString(0)] = values;
        }
        return result;
    }

    private async Task<Dictionary<string, JsonElement>> ReadAsync(string sql, string parameter, string value, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(parameter, value);
        var encrypted = await command.ExecuteScalarAsync(ct);
        return encrypted is byte[] bytes ? Unprotect(bytes) : new(StringComparer.Ordinal);
    }

    private async Task<Dictionary<string, JsonElement>> ReadAsync(string sql, DbConnection connection, DbTransaction transaction,
        string parameter, string value, CancellationToken ct)
    {
        await using var command = Sql.Command(sql, connection, transaction);
        command.Parameters.AddWithValue(parameter, value);
        var encrypted = await command.ExecuteScalarAsync(ct);
        return encrypted is byte[] bytes ? Unprotect(bytes) : new(StringComparer.Ordinal);
    }

    private async Task<Dictionary<string, JsonElement>> ReadAsync(string sql, DbConnection connection, DbTransaction transaction,
        (string Name, object Value) first, (string Name, object Value) second, CancellationToken ct)
    {
        await using var command = Sql.Command(sql, connection, transaction);
        command.Parameters.AddWithValue(first.Name, first.Value);
        command.Parameters.AddWithValue(second.Name, second.Value);
        var encrypted = await command.ExecuteScalarAsync(ct);
        return encrypted is byte[] bytes ? Unprotect(bytes) : new(StringComparer.Ordinal);
    }

    private async Task<Dictionary<string, JsonElement>> ReadAsync(string sql, (string Name, object Value) first,
        (string Name, object Value) second, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(first.Name, first.Value);
        command.Parameters.AddWithValue(second.Name, second.Value);
        var encrypted = await command.ExecuteScalarAsync(ct);
        return encrypted is byte[] bytes ? Unprotect(bytes) : new(StringComparer.Ordinal);
    }

    private Dictionary<string, JsonElement> Merge(FlowConfigurationSchema? schema, Dictionary<string, JsonElement> current,
        IReadOnlyDictionary<string, JsonElement> submitted, IReadOnlyList<string>? clear, bool flowOverride)
    {
        var fields = (schema?.Fields ?? []).ToDictionary(field => field.Id, StringComparer.Ordinal);
        var result = new Dictionary<string, JsonElement>(current, StringComparer.Ordinal);
        foreach (var key in clear ?? [])
        {
            if (!fields.TryGetValue(key, out var field) || field.Type != "secret")
                throw new PluginConfigurationValidationException($"Seul un secret déclaré peut être supprimé : {key}.");
            result.Remove(key);
        }
        foreach (var (key, value) in submitted)
        {
            if (!fields.TryGetValue(key, out var field))
                throw new PluginConfigurationValidationException($"Paramètre inconnu : {key}.");
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                || flowOverride && value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()))
            {
                result.Remove(key);
                continue;
            }
            if (field.Type == "secret" && value.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(value.GetString()))
                continue;
            result[key] = value.Clone();
        }
        return result;
    }

    private static string? Validate(FlowConfigurationSchema? schema, IReadOnlyDictionary<string, JsonElement> values, bool requireRequired)
    {
        foreach (var key in values.Keys)
            if (!(schema?.Fields ?? []).Any(field => field.Id == key)) return $"Paramètre inconnu : {key}.";
        foreach (var field in schema?.Fields ?? [])
        {
            values.TryGetValue(field.Id, out var value);
            var empty = value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                || value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString());
            if (empty)
            {
                if (requireRequired && field.Required) return $"Le paramètre « {field.Label} » est obligatoire.";
                continue;
            }
            switch (field.Type)
            {
                case "text":
                case "secret":
                    if (value.ValueKind != JsonValueKind.String) return $"Le paramètre « {field.Label} » doit être un texte.";
                    break;
                case "number":
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number)
                        || field.Min is { } min && number < min || field.Max is { } max && number > max)
                        return $"Le paramètre « {field.Label} » est hors limites.";
                    break;
                case "boolean":
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return $"Le paramètre « {field.Label} » doit être Oui ou Non.";
                    break;
                case "select":
                    if (value.ValueKind != JsonValueKind.String || !(field.Options ?? []).Any(option => option.Value == value.GetString()))
                        return $"Le paramètre « {field.Label} » doit correspondre à une option disponible.";
                    break;
                default:
                    return $"Le type du paramètre « {field.Label} » n’est pas pris en charge.";
            }
        }
        return null;
    }

    private static void ValidatePathMappings(IReadOnlyDictionary<string, JsonElement> values)
    {
        if (!values.TryGetValue("pathMappings", out var mappings) || mappings.ValueKind != JsonValueKind.String) return;
        try { LibraryPathMapping.Parse(mappings.GetString()); }
        catch (FormatException exception) { throw new PluginConfigurationValidationException(exception.Message); }
    }

    private static void ValidateNodeDefaults(PluginManifest plugin,IReadOnlyDictionary<string,JsonElement> values,bool requireTarget)
    {
        if(plugin.Id!="nodes") return;
        values.TryGetValue("targetNodeId",out var target);
        var provided=target.ValueKind==JsonValueKind.String && !string.IsNullOrWhiteSpace(target.GetString());
        if(provided && (!Guid.TryParse(target.GetString(),out var id) || id==Guid.Empty))
            throw new PluginConfigurationValidationException("L'identifiant du worker est invalide.");
        if(values.TryGetValue("targetGroupId",out var groupTarget) && groupTarget.ValueKind==JsonValueKind.String
            && !string.IsNullOrWhiteSpace(groupTarget.GetString()) && (!Guid.TryParse(groupTarget.GetString(),out var groupId) || groupId==Guid.Empty))
            throw new PluginConfigurationValidationException("L'identifiant du groupe destinataire est invalide.");
        if(requireTarget && values.TryGetValue("mode",out var mode) && mode.GetString()=="node" && !provided)
            throw new PluginConfigurationValidationException("Choisissez le nœud par défaut pour cette destination.");
        if(requireTarget && values.TryGetValue("mode",out mode) && mode.GetString()=="selector"
            && !(values.TryGetValue("targetGroupId",out var group) && group.ValueKind==JsonValueKind.String && Guid.TryParse(group.GetString(),out _))
            && !(values.TryGetValue("requiredLabels",out var labels) && labels.ValueKind==JsonValueKind.String && !string.IsNullOrWhiteSpace(labels.GetString())))
            throw new PluginConfigurationValidationException("Choisissez un groupe ou des étiquettes pour cette destination.");
        if(values.TryGetValue("requiredLabels",out var labelSelector) && labelSelector.ValueKind==JsonValueKind.String
            && !string.IsNullOrWhiteSpace(labelSelector.GetString()) && !Encoding.Contracts.Nodes.NodeLabelSelector.IsValid(labelSelector.GetString()))
            throw new PluginConfigurationValidationException("Le filtre d’étiquettes doit utiliser clé ou clé=valeur, séparés par des virgules.");
    }

    private PluginConfigurationResponse ToResponse(PluginManifest plugin, int revision, IReadOnlyDictionary<string, JsonElement> values)
    {
        var secretIds = (plugin.ConfigurationSchema?.Fields ?? []).Where(field => field.Type == "secret").Select(field => field.Id).ToHashSet(StringComparer.Ordinal);
        return new(plugin.Id, revision,
            values.Where(pair => !secretIds.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value),
            values.Keys.Where(secretIds.Contains).ToArray());
    }

    private byte[] ProtectData(byte[] data) => protector.Protect(data);
    private byte[] UnprotectData(byte[] data) => protector.Unprotect(data);
    private byte[] Protect(IReadOnlyDictionary<string, JsonElement> values) => ProtectData(JsonSerializer.SerializeToUtf8Bytes(values, Json));
    internal byte[] ProtectSnapshot(IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>> values)
        => ProtectData(JsonSerializer.SerializeToUtf8Bytes(values, Json));
    internal IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>> ReadSnapshot(byte[] values)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, IReadOnlyDictionary<string, JsonElement>>>(UnprotectData(values), Json)
                ?? new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>();
        }
        catch (CryptographicException)
        {
            return new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>();
        }
    }

    private Dictionary<string, JsonElement> Unprotect(byte[] values)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(UnprotectData(values), Json) ?? new(StringComparer.Ordinal);
        }
        catch (CryptographicException)
        {
            return new(StringComparer.Ordinal);
        }
    }
}

public sealed class PluginConfigurationValidationException(string message) : Exception(message);
