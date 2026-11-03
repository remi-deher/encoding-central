using System.Data.Common;
using Microsoft.AspNetCore.DataProtection;
using System.Text.Json;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;

namespace Encoding.Api.Tools;

/// <summary>Points de reprise IA chiffrés (Data Protection) dans les registres locaux du central. Ne stocke jamais les identifiants des CLI.</summary>
public sealed class AiCentralStore(DbDataSource database, RegisterRepository registers, IDataProtectionProvider protection)
{
    private readonly IDataProtector protector = protection.CreateProtector("Encoding.Ai.v2");

    public Task<string> ProtectAsync<T>(T value, string purpose, CancellationToken ct)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(value);
        if (plain.Length > 512 * 1024) throw new ArgumentException("Le contexte IA dépasse la limite de 512 Kio. Ouvrez une nouvelle session.");
        return Task.FromResult(Convert.ToBase64String(protector.CreateProtector(purpose).Protect(plain)));
    }
    public Task<T> UnprotectAsync<T>(string encoded, string purpose, CancellationToken ct)
    {
        var bytes = Convert.FromBase64String(encoded);
        if (bytes.Length > 1024 * 1024) throw new ArgumentException("Message IA invalide.");
        var plain = protector.CreateProtector(purpose).Unprotect(bytes);
        return Task.FromResult(JsonSerializer.Deserialize<T>(plain) ?? throw new ArgumentException("Message IA vide."));
    }
    public async Task SaveAsync<T>(string key, T value, CancellationToken ct) =>
        await registers.SetAsync("ai/" + key, JsonSerializer.Serialize(await ProtectAsync(value, key, ct)), ct);

    /// <summary>Supprime la valeur du registre (tombstone « null »).</summary>
    public Task DeleteAsync(string key, CancellationToken ct) =>
        registers.SetAsync("ai/" + key, "null", ct);
    public async Task<IReadOnlyList<T>> ListSessionsAsync<T>(int offset, int limit, CancellationToken ct, bool includeAssistant = false)
    {
        if (offset < 0 || limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        // Bounded pagination over the existing encrypted registers, ordered by their last observed update.
        var rows = new List<(string Key, string Value)>();
        await using (var command = database.CreateCommand("SELECT s.register_key, s.value FROM registers s WHERE s.register_key LIKE 'ai/session/%' AND s.value <> 'null' " + (includeAssistant ? "" : "AND NOT EXISTS (SELECT 1 FROM registers owner WHERE owner.register_key = 'ai/assistant/session-owner/' || substr(s.register_key, 12) AND owner.value <> 'null') ") + "ORDER BY s.updated_at DESC, s.register_key LIMIT @limit OFFSET @offset"))
        {
            command.Parameters.AddWithValue("limit", limit);
            command.Parameters.AddWithValue("offset", offset);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) rows.Add((reader.GetString(0)[3..], reader.GetString(1)));
        }
        var sessions = new List<T>();
        foreach (var row in rows)
        {
            var encoded = JsonSerializer.Deserialize<string>(row.Value);
            if (!string.IsNullOrWhiteSpace(encoded))
                sessions.Add(await UnprotectAsync<T>(encoded, row.Key, ct));
        }
        return sessions;
    }

    /// <summary>Reads one stable key-ordered page so retention jobs can traverse more than the first 1,000 registers.</summary>
    public async Task<IReadOnlyList<(string Key, T Value)>> ReadPrefixPageAsync<T>(string prefix, string? afterRegisterKey,
        int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        var rows = new List<(string Key, string Value)>();
        var sql = afterRegisterKey is null
            ? "SELECT register_key,value FROM registers WHERE register_key LIKE @prefix AND value <> 'null' ORDER BY register_key LIMIT @limit"
            : "SELECT register_key,value FROM registers WHERE register_key LIKE @prefix AND value <> 'null' AND register_key > @afterKey ORDER BY register_key LIMIT @limit";
        await using (var command = database.CreateCommand(sql))
        {
            command.Parameters.AddWithValue("prefix", "ai/" + prefix + "%");
            if (afterRegisterKey is not null) command.Parameters.AddWithValue("afterKey", afterRegisterKey);
            command.Parameters.AddWithValue("limit", limit);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        var page = new List<(string Key, T Value)>(rows.Count);
        foreach (var (key, value) in rows)
        {
            var encoded = JsonSerializer.Deserialize<string>(value);
            if (string.IsNullOrWhiteSpace(encoded)) continue;
            var purpose = key["ai/".Length..];
            page.Add((key, await UnprotectAsync<T>(encoded, purpose, ct)));
        }
        return page;
    }

    public async Task<IReadOnlyList<T>> ReadPrefixLatestAsync<T>(string prefix, int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        var rows = new List<(string Key, string Value)>();
        await using (var command = database.CreateCommand("SELECT register_key,value FROM registers WHERE register_key LIKE @prefix AND value <> 'null' ORDER BY updated_at DESC, register_key DESC LIMIT @limit"))
        {
            command.Parameters.AddWithValue("prefix", "ai/" + prefix + "%");
            command.Parameters.AddWithValue("limit", limit);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        var result = new List<T>(rows.Count);
        foreach (var (key, value) in rows)
        {
            var encoded = JsonSerializer.Deserialize<string>(value);
            if (string.IsNullOrWhiteSpace(encoded)) continue;
            result.Add(await UnprotectAsync<T>(encoded, key["ai/".Length..], ct));
        }
        return result;
    }
    public async Task<T?> ReadAsync<T>(string key, CancellationToken ct)
    {
        await using var command = database.CreateCommand("SELECT value FROM registers WHERE register_key=@key");
        command.Parameters.AddWithValue("key", "ai/" + key);
        var value = await command.ExecuteScalarAsync(ct);
        if (value is null or DBNull) return default;
        var encoded = JsonSerializer.Deserialize<string>((string)value);
        return encoded is null ? default : await UnprotectAsync<T>(encoded, key, ct);
    }

    public async Task<IReadOnlyList<T>> ReadPrefixAsync<T>(string prefix, CancellationToken ct)
    {
        await using var command = database.CreateCommand("SELECT register_key,value FROM registers WHERE register_key LIKE @prefix AND value <> 'null' ORDER BY register_key LIMIT 1000");
        command.Parameters.AddWithValue("prefix", "ai/" + prefix + "%");
        var values = new List<(string Key, string Encoded)>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) values.Add((reader.GetString(0), reader.GetString(1)));
        }

        var result = new List<T>(values.Count);
        foreach (var (key, value) in values)
        {
            var purpose = key["ai/".Length..];
            var encoded = JsonSerializer.Deserialize<string>(value);
            if (encoded is null) continue;
            var item = await UnprotectAsync<T>(encoded, purpose, ct);
            if (item is not null) result.Add(item);
        }
        return result;
    }
}
