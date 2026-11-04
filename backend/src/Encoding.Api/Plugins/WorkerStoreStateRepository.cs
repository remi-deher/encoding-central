using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using Encoding.Api.Infrastructure.Data;

namespace Encoding.Api.Plugins;

/// <summary>Élément désiré d'un worker : version exacte et empreinte SHA-256 vérifiée par le central (jamais fournie par un client).</summary>
public sealed record StoreDesiredItem(string Kind, string Id, string Version, string? Platform, string Sha256);

/// <summary>Élément appliqué rapporté par le worker. <c>ErrorCode</c> est un code court, jamais un message d'exception.</summary>
public sealed record StoreAppliedItem(string Kind, string Id, string Version, string? Platform, string Sha256, string Status, string? ErrorCode);

public sealed record StoreDesiredState(long Revision, IReadOnlyList<StoreDesiredItem> Items, DateTimeOffset? UpdatedAt);
public sealed record StoreAppliedState(long Revision, IReadOnlyList<StoreAppliedItem> Items, DateTimeOffset? ReportedAt);
public sealed record WorkerStoreState(Guid WorkerId, StoreDesiredState Desired, StoreAppliedState Applied);

public enum StoreReportOutcome { Accepted, StaleRevision, UnknownRevision, UnknownWorker }

/// <summary>Résultat d'un rapport d'état appliqué. <c>DesiredRevision</c> permet au worker de se resynchroniser après un conflit.</summary>
public sealed record StoreReportResult(StoreReportOutcome Outcome, long DesiredRevision);

public sealed class StoreStateException(string message) : Exception(message);

/// <summary>
/// État Store par worker : l'état désiré (central, administrateur) et l'état appliqué (rapporté par le worker) sont persistés
/// séparément, chacun avec une révision monotone. Un worker sans ligne a un état vide : rien n'est activé par défaut.
/// </summary>
public sealed partial class WorkerStoreStateRepository(DbDataSource dataSource)
{
    public const int MaxItems = 500;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("^[0-9a-f]{64}$")] private static partial Regex Sha256Pattern();
    [GeneratedRegex(@"^\d+\.\d+\.\d+$")] private static partial Regex VersionPattern();
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,127}$")] private static partial Regex IdPattern();
    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,31}$")] private static partial Regex PlatformPattern();
    [GeneratedRegex("^[a-z0-9_.-]{1,64}$")] private static partial Regex ErrorCodePattern();

    public static readonly IReadOnlySet<string> AppliedStatuses = new HashSet<string>(["applied", "failed", "removed"], StringComparer.Ordinal);

    /// <summary>Lit l'état d'un worker ; <c>null</c> si le worker est inconnu ou retiré.</summary>
    public async Task<WorkerStoreState?> GetAsync(Guid workerId, CancellationToken ct)
    {
        await using var exists = dataSource.CreateCommand("SELECT EXISTS(SELECT 1 FROM workers WHERE id = @id AND deleted_at IS NULL)");
        exists.Parameters.AddWithValue("id", workerId);
        if (!await exists.ScalarBoolAsync(ct)) return null;
        await using var command = dataSource.CreateCommand("""
            SELECT desired_revision, desired_items, desired_updated_at, applied_revision, applied_items, applied_reported_at
            FROM worker_store_state WHERE worker_id = @id
            """);
        command.Parameters.AddWithValue("id", workerId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new WorkerStoreState(workerId, new(0, [], null), new(0, [], null));
        return new WorkerStoreState(workerId,
            new(reader.GetInt64(0), Parse<StoreDesiredItem>(reader.GetString(1)), Instant(reader, 2)),
            new(reader.GetInt64(3), Parse<StoreAppliedItem>(reader.GetString(4)), Instant(reader, 5)));
    }

    /// <summary>
    /// Remplace l'état désiré d'un worker et incrémente sa révision de façon atomique. Les éléments doivent déjà porter l'empreinte
    /// officielle résolue par le central. Retourne <c>null</c> si le worker est inconnu.
    /// </summary>
    public async Task<StoreDesiredState?> ReplaceDesiredAsync(Guid workerId, IReadOnlyList<StoreDesiredItem> items, CancellationToken ct)
    {
        var normalized = ValidateDesired(items);
        await using var command = dataSource.CreateCommand("""
            INSERT INTO worker_store_state(worker_id, desired_revision, desired_items, desired_updated_at)
            SELECT id, 1, @items, (strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z') FROM workers WHERE id = @id AND deleted_at IS NULL
            ON CONFLICT (worker_id) DO UPDATE SET
                desired_revision = worker_store_state.desired_revision + 1,
                desired_items = EXCLUDED.desired_items,
                desired_updated_at = EXCLUDED.desired_updated_at
            RETURNING desired_revision
            """);
        command.Parameters.AddWithValue("id", workerId);
        command.Parameters.AddWithValue("items", SqlKind.Text, JsonSerializer.Serialize(normalized, Json));
        var revision = await Int64Async(command, ct);
        return revision is { } value ? new StoreDesiredState(value, normalized, DateTimeOffset.UtcNow) : null;
    }

    /// <summary>
    /// Enregistre l'état appliqué rapporté par le worker authentifié <paramref name="workerId"/>. La révision ne peut ni reculer
    /// (obsolète) ni dépasser la révision désirée (inconnue). Re-rapporter la même révision est idempotent.
    /// </summary>
    public async Task<StoreReportResult> ReportAppliedAsync(Guid workerId, long revision, IReadOnlyList<StoreAppliedItem> items, CancellationToken ct)
    {
        var normalized = ValidateApplied(items);
        await using var update = dataSource.CreateCommand("""
            UPDATE worker_store_state SET applied_revision = @revision, applied_items = @items,
                applied_reported_at = (strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z')
            WHERE worker_id = @id AND @revision >= applied_revision AND @revision <= desired_revision
            """);
        update.Parameters.AddWithValue("id", workerId);
        update.Parameters.AddWithValue("revision", SqlKind.Integer, revision);
        update.Parameters.AddWithValue("items", SqlKind.Text, JsonSerializer.Serialize(normalized, Json));
        if (await update.ExecuteNonQueryAsync(ct) == 1)
            return new(StoreReportOutcome.Accepted, await DesiredRevisionAsync(workerId, ct) ?? revision);

        var state = await GetAsync(workerId, ct);
        if (state is null) return new(StoreReportOutcome.UnknownWorker, 0);
        var outcome = revision < state.Applied.Revision ? StoreReportOutcome.StaleRevision : StoreReportOutcome.UnknownRevision;
        return new(outcome, state.Desired.Revision);
    }

    private async Task<long?> DesiredRevisionAsync(Guid workerId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("SELECT desired_revision FROM worker_store_state WHERE worker_id = @id");
        command.Parameters.AddWithValue("id", workerId);
        return await Int64Async(command, ct);
    }

    public static IReadOnlyList<StoreDesiredItem> ValidateDesired(IReadOnlyList<StoreDesiredItem> items)
    {
        var result = items.Select(item => item with { Sha256 = (item.Sha256 ?? "").ToLowerInvariant() }).ToList();
        foreach (var item in result) ValidateCommon(item.Kind, item.Id, item.Version, item.Platform, item.Sha256);
        EnsureUnique(result.Select(item => (item.Kind, item.Id, item.Platform)));
        return result.OrderBy(item => item.Kind, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal)
            .ThenBy(item => item.Platform, StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyList<StoreAppliedItem> ValidateApplied(IReadOnlyList<StoreAppliedItem> items)
    {
        var result = items.Select(item => item with { Sha256 = (item.Sha256 ?? "").ToLowerInvariant() }).ToList();
        foreach (var item in result)
        {
            ValidateCommon(item.Kind, item.Id, item.Version, item.Platform, item.Sha256);
            if (!AppliedStatuses.Contains(item.Status ?? "")) throw new StoreStateException("Statut appliqué invalide : applied, failed ou removed attendu.");
            if (item.ErrorCode is not null && !ErrorCodePattern().IsMatch(item.ErrorCode)) throw new StoreStateException("Le code d'erreur doit être un code court, sans message.");
        }
        EnsureUnique(result.Select(item => (item.Kind, item.Id, item.Platform)));
        return result;
    }

    private static void ValidateCommon(string kind, string id, string version, string? platform, string sha256)
    {
        if (kind is not ("plugin" or "tool")) throw new StoreStateException("Le type doit être « plugin » ou « tool ».");
        if (id is null || !IdPattern().IsMatch(id)) throw new StoreStateException("Identifiant d'élément invalide.");
        if (version is null || !VersionPattern().IsMatch(version)) throw new StoreStateException("Une version exacte X.Y.Z est exigée : pas de version flottante.");
        if (!Sha256Pattern().IsMatch(sha256)) throw new StoreStateException("L'empreinte SHA-256 est obligatoire (64 caractères hexadécimaux).");
        if (kind == "tool" && (platform is null || !PlatformPattern().IsMatch(platform))) throw new StoreStateException("La plateforme est requise pour un outil.");
        if (kind == "plugin" && platform is not null) throw new StoreStateException("Un plugin n'a pas de plateforme.");
    }

    private static void EnsureUnique(IEnumerable<(string, string, string?)> keys)
    {
        var list = keys.ToList();
        if (list.Count > MaxItems) throw new StoreStateException($"Au plus {MaxItems} éléments par worker.");
        if (list.Distinct().Count() != list.Count) throw new StoreStateException("Un élément ne peut apparaître qu'une fois.");
    }

    private static async Task<long?> Int64Async(DbCommand command, CancellationToken ct) =>
        await command.ExecuteScalarAsync(ct) is { } value and not DBNull ? Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) : null;

    private static IReadOnlyList<T> Parse<T>(string json) => JsonSerializer.Deserialize<List<T>>(json, Json) ?? [];

    private static DateTimeOffset? Instant(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);
}
