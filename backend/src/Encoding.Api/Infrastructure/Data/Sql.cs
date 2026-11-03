using System.Data.Common;
using Encoding.Api.Infrastructure.Data.Sqlite;
using Microsoft.Data.Sqlite;

namespace Encoding.Api.Infrastructure.Data;

/// <summary>Indication de type des paramètres SQLite (texte JSON, binaire…) ; les tableaux se passent en JSON, sans type dédié.</summary>
public enum SqlKind { Text, Json, Bytes, Integer, Decimal, Uuid }

/// <summary>
/// Seul point du backend qui connaît le fournisseur de base de données.
/// Le SQL des dépôts est du SQLite natif, exécuté tel quel (aucune traduction) ; voir l'ADR 0043 et l'ADR 0043.
/// </summary>
public static class Sql
{
    public static DbCommand Command(string sql, DbConnection connection, DbTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    public static bool IsSqlite(DbConnection connection) => connection is SqliteConnectionWrapper;
    public static bool IsSqlite(DbDataSource dataSource) => dataSource is SqliteDataSource;

    /// <summary>
    /// Instant courant en SQLite natif, au format texte UTC de largeur fixe (<see cref="Instant"/>) : l'ordre alphabétique est l'ordre
    /// chronologique. Insérer dans une requête interpolée ; à défaut, écrire l'expression littérale (voir <c>BACKEND_RULES.md</c>).
    /// </summary>
    public const string UtcNow = "(strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z')";

    public static DbParameter Parameter(string name, SqlKind kind, object? value)
    {
        _ = kind;
        return new SqliteParameter(name, value ?? DBNull.Value);
    }

    public static DbParameter AddWithValue(this DbParameterCollection parameters, string name, object? value)
    {
        DbParameter parameter = new SqliteParameter(name, value ?? DBNull.Value);
        parameters.Add(parameter);
        return parameter;
    }

    public static DbParameter AddWithValue(this DbParameterCollection parameters, string name, SqlKind kind, object? value)
    {
        var parameter = Parameter(name, kind, value);
        parameters.Add(parameter);
        return parameter;
    }

    /// <summary>Violation d'unicité signalée par le fournisseur.</summary>
    public static string[] GetStringArray(this DbDataReader reader, int ordinal) => ToStringArray(reader.GetValue(ordinal)) ?? [];

    public static string[]? ToStringArray(object? value) => value switch
    {
        string[] array => array,
        string json => System.Text.Json.JsonSerializer.Deserialize<string[]>(json),
        _ => null
    };

    public static async Task<int?> ScalarInt32Async(this DbCommand command, CancellationToken cancellationToken = default)
    {
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public static async Task<Guid?> ScalarGuidAsync(this DbCommand command, CancellationToken cancellationToken = default)
    {
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value switch { Guid id => id, string text when Guid.TryParse(text, out var id) => id, _ => null };
    }

    public static async Task<bool?> ScalarNullableBoolAsync(this DbCommand command, CancellationToken cancellationToken = default)
    {
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public static async Task<DateTime?> ScalarInstantAsync(this DbCommand command, CancellationToken cancellationToken = default)
    {
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value switch
        {
            DateTime instant => DateTime.SpecifyKind(instant, DateTimeKind.Utc),
            string text => DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal).UtcDateTime,
            _ => null
        };
    }

    public static async Task<bool> ScalarBoolAsync(this DbCommand command, CancellationToken cancellationToken = default)
    {
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is not null and not DBNull && Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public static bool IsUniqueViolation(Exception exception) =>
        exception is SqliteException { SqliteErrorCode: 19, SqliteExtendedErrorCode: 2067 or 1555 };
}
