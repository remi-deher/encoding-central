using System.Collections;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Encoding.Api.Infrastructure.Data.Sqlite;

/// <summary>
/// Source de données SQLite utilisée par chaque serveur (architecture 0043).
/// Les connexions traduisent le SQL partagé à l'exécution et ouvrent leurs transactions en écriture immédiate,
/// ce qui remplace les verrous de lignes et les verrous consultatifs (un seul écrivain à la fois).
/// Une lecture cohérente demande <see cref="IsolationLevel.Snapshot"/> : la transaction est alors différée et ne prend pas le verrou d'écriture.
/// </summary>
public sealed class SqliteDataSource : DbDataSource
{
    private readonly SemaphoreSlim journalModeGate = new(1, 1);
    private int journalModeConfigured;

    public SqliteDataSource(string connectionString, SqliteWriterOptions? writerOptions = null)
    {
        ConnectionString = connectionString;
        Writer = new(writerOptions);
        // Le dossier de la base est créé au premier usage (volume vide d'un conteneur neuf).
        var file = new SqliteConnectionStringBuilder(connectionString).DataSource;
        var directory = string.IsNullOrEmpty(file) || file.StartsWith(':') ? null : Path.GetDirectoryName(Path.GetFullPath(file));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    }

    public override string ConnectionString { get; }
    public SqliteWriter Writer { get; }

    protected override DbConnection CreateDbConnection() => new SqliteConnectionWrapper(new SqliteConnection(ConnectionString), this);

    internal void EnsureJournalMode(SqliteConnection connection)
    {
        if (Volatile.Read(ref journalModeConfigured) != 0) return;
        journalModeGate.Wait();
        try
        {
            if (journalModeConfigured != 0) return;
            SetJournalMode(connection);
            Volatile.Write(ref journalModeConfigured, 1);
        }
        finally { journalModeGate.Release(); }
    }

    internal async Task EnsureJournalModeAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (Volatile.Read(ref journalModeConfigured) != 0) return;
        await journalModeGate.WaitAsync(ct);
        try
        {
            if (journalModeConfigured != 0) return;
            await SetJournalModeAsync(connection, ct);
            Volatile.Write(ref journalModeConfigured, 1);
        }
        finally { journalModeGate.Release(); }
    }

    private static void SetJournalMode(SqliteConnection connection)
    {
        var timeout = connection.DefaultTimeout;
        connection.DefaultTimeout = 30;
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout = 30000; PRAGMA journal_mode = WAL;";
            command.ExecuteNonQuery();
        }
        finally { connection.DefaultTimeout = timeout; }
    }

    private static async Task SetJournalModeAsync(SqliteConnection connection, CancellationToken ct)
    {
        var timeout = connection.DefaultTimeout;
        connection.DefaultTimeout = 30;
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout = 30000; PRAGMA journal_mode = WAL;";
            await command.ExecuteNonQueryAsync(ct);
        }
        finally { connection.DefaultTimeout = timeout; }
    }
}

public sealed class SqliteConnectionWrapper(SqliteConnection inner, SqliteDataSource dataSource) : DbConnection
{
    internal SqliteConnection Inner { get; } = inner;
    internal SqliteWriter Writer => dataSource.Writer;
    internal SqliteTransactionWrapper? Transaction { get; set; }
    internal HashSet<DbDataReader> Readers { get; } = [];

    [AllowNull] public override string ConnectionString { get => Inner.ConnectionString; set => Inner.ConnectionString = value; }
    public override string Database => Inner.Database;
    public override string DataSource => Inner.DataSource;
    public override string ServerVersion => Inner.ServerVersion;
    public override ConnectionState State => Inner.State;

    public override void ChangeDatabase(string databaseName) => Inner.ChangeDatabase(databaseName);
    public override void Close()
    {
        try
        {
            foreach (var reader in Readers.ToArray()) reader.Dispose();
        }
        finally
        {
            try { Transaction?.Dispose(); }
            finally { Inner.Close(); }
        }
    }

    public override void Open()
    {
        Inner.Open();
        if (Writer.Options.Enabled) Inner.DefaultTimeout = 1;
        dataSource.EnsureJournalMode(Inner);
        Initialize();
    }

    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        await Inner.OpenAsync(cancellationToken);
        if (Writer.Options.Enabled) Inner.DefaultTimeout = 1;
        await dataSource.EnsureJournalModeAsync(Inner, cancellationToken);
        await using var command = Inner.CreateCommand();
        command.CommandText = ConnectionPragmas;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // Infrastructure exception: connection initialization must precede transactions.
    private string ConnectionPragmas => $"PRAGMA busy_timeout = {(Writer.Options.Enabled ? 1000 : 30000)}; PRAGMA foreign_keys = ON; PRAGMA synchronous = NORMAL; PRAGMA query_only = OFF;";

    private void Initialize()
    {
        using var command = Inner.CreateCommand();
        command.CommandText = ConnectionPragmas;
        command.ExecuteNonQuery();
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        BeginDbTransactionAsync(isolationLevel, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(IsolationLevel isolationLevel, CancellationToken ct)
    {
        if (Transaction is not null) throw new InvalidOperationException("Une transaction est déjà active sur cette connexion.");
        var snapshot = isolationLevel == IsolationLevel.Snapshot;
        var lease = !snapshot ? await Writer.EnterAsync(ct) : null;
        try
        {
            if (snapshot) SetQueryOnly(true);
            for (var attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    return Transaction = new(this, Inner.BeginTransaction(deferred: snapshot), lease, snapshot);
                }
                catch (SqliteException exception) when (!snapshot && Writer.Options.Enabled
                    && exception.SqliteErrorCode is 5 or 6 && attempt < Writer.Options.BeginRetries)
                {
                    // Only BEGIN is retried: no business statement has executed yet.
                    Writer.Retried();
                    await Task.Delay(TimeSpan.FromMilliseconds(25 * (attempt + 1)), ct);
                }
            }
        }
        catch
        {
            try { if (snapshot) SetQueryOnly(false); }
            finally { lease?.Dispose(); }
            throw;
        }
    }

    internal void SetQueryOnly(bool value)
    {
        if (Inner.State != ConnectionState.Open) return;
        using var command = Inner.CreateCommand();
        command.CommandText = value ? "PRAGMA query_only = ON" : "PRAGMA query_only = OFF";
        command.ExecuteNonQuery();
    }

    protected override DbCommand CreateDbCommand() => new SqliteCommandWrapper { WrappedConnection = this };

    protected override void Dispose(bool disposing)
    {
        if (disposing) { try { Close(); } finally { Inner.Dispose(); } }
        base.Dispose(disposing);
    }
}

public sealed class SqliteTransactionWrapper(SqliteConnectionWrapper connection, SqliteTransaction inner,
    IDisposable? writerLease, bool snapshot) : DbTransaction
{
    private bool completed;
    internal SqliteTransaction Inner { get; } = inner;
    protected override DbConnection DbConnection => connection;
    public override IsolationLevel IsolationLevel => Inner.IsolationLevel;
    public override void Commit() { Inner.Commit(); Complete(); }
    public override void Rollback() { Inner.Rollback(); Complete(); }
    private void Complete()
    {
        if (completed) return;
        completed = true;
        connection.Transaction = null;
        try { if (snapshot) connection.SetQueryOnly(false); }
        finally { writerLease?.Dispose(); }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { try { Inner.Dispose(); } finally { Complete(); } }
        base.Dispose(disposing);
    }
}

public sealed class SqliteCommandWrapper : DbCommand
{
    private static readonly ConcurrentDictionary<string, string> Translations = new(StringComparer.Ordinal);
    private readonly SqliteParameterCollection parameters = new SqliteCommand().Parameters;
    private SqliteCommand? executing;

    internal SqliteConnectionWrapper? WrappedConnection { get; set; }
    private SqliteTransactionWrapper? wrappedTransaction;

    [AllowNull] public override string CommandText { get; set; } = "";
    public override int CommandTimeout { get; set; } = 30;
    public override CommandType CommandType { get; set; } = CommandType.Text;
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }
    protected override DbConnection? DbConnection { get => WrappedConnection; set => WrappedConnection = (SqliteConnectionWrapper?)value; }
    protected override DbParameterCollection DbParameterCollection => parameters;
    protected override DbTransaction? DbTransaction { get => wrappedTransaction; set => wrappedTransaction = (SqliteTransactionWrapper?)value; }

    public override void Cancel() { }
    public override void Prepare() { }
    protected override DbParameter CreateDbParameter() => new SqliteParameter();

    private static readonly bool Trace = Environment.GetEnvironmentVariable("ENCODING_SQLITE_TRACE") == "1";

    private SqliteCommand Build()
    {
        executing?.Dispose();
        var connection = WrappedConnection ?? throw new InvalidOperationException("La commande n'est reliée à aucune connexion.");
        var command = connection.Inner.CreateCommand();
        command.Transaction = (wrappedTransaction ?? connection.Transaction)?.Inner;
        command.CommandTimeout = connection.Writer.Options.Enabled ? 1 : CommandTimeout;
        var sql = Translations.GetOrAdd(CommandText,
            text => { try { return SqliteTranslator.Translate(text); } catch (FormatException exception) { throw new FormatException($"{exception.Message} [SQL: {text}]"); } });
        var arrays = SqliteTranslator.ArrayParameters(sql);
        foreach (SqliteParameter parameter in parameters)
        {
            var name = parameter.ParameterName.TrimStart('@', ':', '$');
            if (arrays.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                var items = ((IEnumerable)(parameter.Value ?? Array.Empty<object>())).Cast<object?>().ToList();
                sql = SqliteTranslator.ExpandArray(sql, name, items.Count);
                for (var i = 0; i < items.Count; i++) command.Parameters.AddWithValue($"@{name}_{i}", Convert(items[i]));
            }
            else command.Parameters.AddWithValue("@" + name, Convert(parameter.Value));
        }
        command.CommandText = sql;
        if (Trace) Console.Error.WriteLine("SQLTRACE " + sql.Replace('\n', ' ') + " :: " + string.Join(", ", command.Parameters.Cast<SqliteParameter>().Select(p => p.ParameterName + "=" + p.Value)));
        return executing = command;
    }

    /// <summary>Valeur SQLite d'un paramètre : instants en texte UTC de largeur fixe, durées en modificateurs, tableaux en JSON.</summary>
    internal static object Convert(object? value) => value switch
    {
        null or DBNull => DBNull.Value,
        DateTimeOffset instant => Instant(instant.UtcDateTime),
        DateTime instant => Instant(instant.Kind == DateTimeKind.Local ? instant.ToUniversalTime() : DateTime.SpecifyKind(instant, DateTimeKind.Utc)),
        TimeSpan span => (span < TimeSpan.Zero ? "-" : "+") + Math.Abs(span.TotalSeconds).ToString("0.######", CultureInfo.InvariantCulture) + " seconds",
        Guid id => id.ToString("D"),
        string or byte[] => value,
        IEnumerable items => JsonSerializer.Serialize(items.Cast<object?>().Select(Convert).ToList()),
        _ => value
    };

    public static string Instant(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture) + "Z";

    private T Run<T>(Func<SqliteCommand, T> action)
    {
        var command = Build();
        using var lease = EnterAsync(command, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        try { return action(command); }
        catch (SqliteException exception) { throw new SqliteException($"{exception.Message} [SQL: {command.CommandText}]", exception.SqliteErrorCode, exception.SqliteExtendedErrorCode); }
    }

    private async Task<T> RunAsync<T>(Func<SqliteCommand, Task<T>> action, CancellationToken ct)
    {
        var command = Build();
        using var lease = await EnterAsync(command, ct);
        try { return await action(command); }
        catch (SqliteException exception) { throw new SqliteException($"{exception.Message} [SQL: {command.CommandText}]", exception.SqliteErrorCode, exception.SqliteExtendedErrorCode); }
    }

    public override int ExecuteNonQuery() => Run(command => command.ExecuteNonQuery());
    public override object? ExecuteScalar() => Run(command => command.ExecuteScalar());
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => ExecuteDbDataReaderAsync(behavior, CancellationToken.None).GetAwaiter().GetResult();
    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) => RunAsync(command => command.ExecuteNonQueryAsync(cancellationToken), cancellationToken);
    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken) => RunAsync(command => command.ExecuteScalarAsync(cancellationToken), cancellationToken);
    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        var command = Build();
        var lease = await EnterAsync(command, cancellationToken);
        try
        {
            var reader = await command.ExecuteReaderAsync(behavior & ~CommandBehavior.CloseConnection, cancellationToken);
            TransactionReader? wrapped = null;
            wrapped = new TransactionReader(reader, null, WrappedConnection!,
                (behavior & CommandBehavior.CloseConnection) != 0, lease,
                () => WrappedConnection!.Readers.Remove(wrapped!));
            WrappedConnection!.Readers.Add(wrapped);
            return wrapped;
        }
        catch { lease?.Dispose(); throw; }
    }

    private ValueTask<IDisposable?> EnterAsync(SqliteCommand command, CancellationToken ct)
    {
        var connection = WrappedConnection!;
        // Existing transactions already own admission. Snapshot is enforced by SQLite query_only,
        // so even a write hidden in a CTE or a multi-statement batch cannot upgrade the reader.
        if (connection.Transaction is not null || IsSimpleRead(command.CommandText))
            return ValueTask.FromResult<IDisposable?>(null);
        return EnterWriterAsync(connection.Writer, ct);
    }

    private static async ValueTask<IDisposable?> EnterWriterAsync(SqliteWriter writer, CancellationToken ct) => await writer.EnterAsync(ct);

    // Conservative fast path; CTEs, comments and batches go through admission. No SQL write
    // is inferred from ExecuteNonQuery vs ExecuteReader (UPDATE ... RETURNING uses a reader).
    private static bool IsSimpleRead(string sql)
    {
        var text = sql.Trim().TrimEnd(';').TrimEnd();
        return text.StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase) && !text.Contains(';');
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) executing?.Dispose();
        base.Dispose(disposing);
    }
}
