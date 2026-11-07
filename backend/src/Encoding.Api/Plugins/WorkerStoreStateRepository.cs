using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using Encoding.Api.Infrastructure.Data;

namespace Encoding.Api.Plugins;

/// <summary>Paquet installé sur le central, donc désiré par tous les workers : version exacte et empreinte SHA-256 de l'archive (jamais fournie par un client).</summary>
public sealed record StoreDesiredItem(string Kind, string Id, string Version, string? Platform, string Sha256);

/// <summary>Élément appliqué rapporté par le worker. <c>ErrorCode</c> est un code court, jamais un message d'exception.</summary>
public sealed record StoreAppliedItem(string Kind, string Id, string Version, string? Platform, string Sha256, string Status, string? ErrorCode);

public sealed record StoreDesiredState(long Revision, IReadOnlyList<StoreDesiredItem> Items, DateTimeOffset? UpdatedAt);
public sealed record StoreAppliedState(long Revision, IReadOnlyList<StoreAppliedItem> Items, DateTimeOffset? ReportedAt);

/// <summary>Écart d'un paquet entre l'état du central et le dernier rapport du worker. <c>Reason</c> : missing, version_mismatch, failed ou extra.</summary>
public sealed record StoreGapItem(string Kind, string Id, string? Platform, string Reason, string? DesiredVersion, string? AppliedVersion, string? ErrorCode);

/// <summary>Écart global : retard de révision et paquets qui diffèrent. <c>InSync</c> vaut vrai seulement si aucun paquet ne diffère.</summary>
public sealed record StoreGap(long RevisionLag, bool InSync, IReadOnlyList<StoreGapItem> Items);

/// <summary>État Store d'un worker : <c>Desired</c> est l'état global du central (identique pour tous les workers), <c>Applied</c> le dernier rapport du worker.</summary>
public sealed record WorkerStoreState(Guid WorkerId, StoreDesiredState Desired, StoreAppliedState Applied, StoreGap Gap);

public enum StoreReportOutcome { Accepted, StaleRevision, UnknownRevision, UnknownWorker }

/// <summary>Résultat d'un rapport d'état appliqué. <c>DesiredRevision</c> permet au worker de se resynchroniser après un conflit.</summary>
public sealed record StoreReportResult(StoreReportOutcome Outcome, long DesiredRevision);

public sealed class StoreStateException(string message) : Exception(message);

/// <summary>
/// État Store appliqué par worker (rapporté par le worker lui-même). L'état désiré n'est plus stocké par worker : c'est l'état global du
/// central (<see cref="CentralStoreStateService"/>, ADR 0046). Les colonnes <c>desired_*</c> de la migration 0076 ne sont plus lues ;
/// <c>desired_revision</c> reçoit seulement max(révision globale, révision appliquée) pour respecter le CHECK de 0076.
/// Un worker sans rapport a un état appliqué vide (révision 0).
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

    /// <summary>Lit le dernier état appliqué d'un worker ; <c>null</c> si le worker est inconnu ou retiré.</summary>
    public async Task<StoreAppliedState?> GetAppliedAsync(Guid workerId, CancellationToken ct)
    {
        await using var exists = dataSource.CreateCommand("SELECT EXISTS(SELECT 1 FROM workers WHERE id = @id AND deleted_at IS NULL)");
        exists.Parameters.AddWithValue("id", workerId);
        if (!await exists.ScalarBoolAsync(ct)) return null;
        await using var command = dataSource.CreateCommand("SELECT applied_revision, applied_items, applied_reported_at FROM worker_store_state WHERE worker_id = @id");
        command.Parameters.AddWithValue("id", workerId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new StoreAppliedState(reader.GetInt64(0), Parse<StoreAppliedItem>(reader.GetString(1)), Instant(reader, 2))
            : new StoreAppliedState(0, [], null);
    }

    /// <summary>
    /// Enregistre l'état appliqué rapporté par le worker authentifié <paramref name="workerId"/>. La révision ne peut ni reculer
    /// (obsolète) ni dépasser la révision globale du central (inconnue). Re-rapporter la même révision est idempotent.
    /// La ligne est créée au premier rapport (plus aucune écriture administrateur ne la précède).
    /// </summary>
    public async Task<StoreReportResult> ReportAppliedAsync(Guid workerId, long revision, IReadOnlyList<StoreAppliedItem> items, CancellationToken ct)
    {
        var normalized = ValidateApplied(items);
        await using var upsert = dataSource.CreateCommand($"""
            INSERT INTO worker_store_state(worker_id, desired_revision, desired_items, desired_updated_at, applied_revision, applied_items, applied_reported_at)
            SELECT w.id, MAX(c.revision, @revision), '[]', c.updated_at, @revision, @items, {Sql.UtcNow}
            FROM workers w, central_store_state c
            WHERE w.id = @id AND w.deleted_at IS NULL AND c.id = 1 AND @revision <= c.revision
            ON CONFLICT (worker_id) DO UPDATE SET
                desired_revision = MAX(worker_store_state.desired_revision, EXCLUDED.desired_revision),
                desired_updated_at = EXCLUDED.desired_updated_at,
                applied_revision = EXCLUDED.applied_revision,
                applied_items = EXCLUDED.applied_items,
                applied_reported_at = EXCLUDED.applied_reported_at
            WHERE EXCLUDED.applied_revision >= worker_store_state.applied_revision
            """);
        upsert.Parameters.AddWithValue("id", workerId);
        upsert.Parameters.AddWithValue("revision", SqlKind.Integer, revision);
        upsert.Parameters.AddWithValue("items", SqlKind.Text, JsonSerializer.Serialize(normalized, Json));
        if (await upsert.ExecuteNonQueryAsync(ct) == 1) return new(StoreReportOutcome.Accepted, await GlobalRevisionAsync(ct));

        var applied = await GetAppliedAsync(workerId, ct);
        if (applied is null) return new(StoreReportOutcome.UnknownWorker, 0);
        var outcome = revision < applied.Revision ? StoreReportOutcome.StaleRevision : StoreReportOutcome.UnknownRevision;
        return new(outcome, await GlobalRevisionAsync(ct));
    }

    private async Task<long> GlobalRevisionAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("SELECT revision FROM central_store_state WHERE id = 1");
        return await Int64Async(command, ct) ?? 0;
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
        if (list.Count > MaxItems) throw new StoreStateException($"Au plus {MaxItems} éléments dans un rapport.");
        if (list.Distinct().Count() != list.Count) throw new StoreStateException("Un élément ne peut apparaître qu'une fois.");
    }

    private static async Task<long?> Int64Async(DbCommand command, CancellationToken ct) =>
        await command.ExecuteScalarAsync(ct) is { } value and not DBNull ? Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) : null;

    private static IReadOnlyList<T> Parse<T>(string json) => JsonSerializer.Deserialize<List<T>>(json, Json) ?? [];

    private static DateTimeOffset? Instant(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);
}
