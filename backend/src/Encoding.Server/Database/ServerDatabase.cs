using Encoding.Server.Auth;
using Microsoft.Data.Sqlite;

namespace Encoding.Server.Database;

internal sealed record Account(string Id, string Username, string Role);

internal sealed record ServerRecord(string Id, string DisplayName, string PublicBaseUrl, long CreatedAt);

internal sealed record WorkerRecord(string Id, string DisplayName, long CreatedAt);

internal sealed class ServerDatabase : IDisposable
{
    public const long SessionLifetimeMs = 12 * 60 * 60 * 1000;
    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly string _dummyPasswordHash = Passwords.Hash("dummy-password-timing");

    public ServerDatabase(string path)
    {
        _path = path;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        ApplyMigrations();
    }

    public static string ResolvePath(string configured, string contentRoot)
    {
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("Database:Path est requis.");
        return Path.IsPathRooted(configured) ? configured : Path.Combine(contentRoot, configured);
    }

    public bool IsReady()
    {
        return Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            return command.ExecuteScalar() is not null;
        });
    }

    public bool IsSetup()
    {
        return Read(connection => ScalarLong(connection, "SELECT COUNT(*) FROM users") > 0);
    }

    public IReadOnlyList<string> TableNames()
    {
        return Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var reader = command.ExecuteReader();
            var names = new List<string>();
            while (reader.Read())
                names.Add(reader.GetString(0));
            return (IReadOnlyList<string>)names;
        });
    }

    public (ServerRecord Server, Account Account, string SessionToken) Setup(
        string username,
        string password,
        string displayName,
        string publicBaseUrl)
    {
        var now = Now();
        var server = new ServerRecord(Guid.NewGuid().ToString("D"), displayName, publicBaseUrl, now);
        var account = new Account(Guid.NewGuid().ToString("D"), username, "admin");
        var token = Tokens.Create();
        Write((connection, transaction) =>
        {
            if (ScalarLong(connection, "SELECT COUNT(*) FROM users", transaction) > 0)
                throw new SetupAlreadyCompletedException();
            Execute(
                connection,
                transaction,
                """
                INSERT INTO server (id, display_name, public_base_url, created_at)
                VALUES ($id, $name, $url, $created)
                """,
                ("$id", server.Id),
                ("$name", server.DisplayName),
                ("$url", server.PublicBaseUrl),
                ("$created", server.CreatedAt));
            Execute(
                connection,
                transaction,
                """
                INSERT INTO users (id, username, password_hash, role, created_at)
                VALUES ($id, $username, $hash, 'admin', $created)
                """,
                ("$id", account.Id),
                ("$username", account.Username),
                ("$hash", Passwords.Hash(password)),
                ("$created", now));
            InsertSession(connection, transaction, account.Id, token, now);
        });
        return (server, account, token);
    }

    public (Account Account, string SessionToken)? Login(string username, string password)
    {
        var now = Now();
        Account? account = null;
        string? hash = null;
        Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, username, password_hash, role FROM users WHERE username = $username";
            command.Parameters.AddWithValue("$username", username);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return 0;
            account = new Account(reader.GetString(0), reader.GetString(1), reader.GetString(3));
            hash = reader.GetString(2);
            return 1;
        });

        var verified = Passwords.Verify(password, hash ?? _dummyPasswordHash);
        if (account is null || hash is null || !verified)
            return null;

        var token = Tokens.Create();
        Write((connection, transaction) => InsertSession(connection, transaction, account.Id, token, now));
        return (account, token);
    }

    public Account? ResolveSession(string token)
    {
        var now = Now();
        var hash = Tokens.Hash(token);
        Account? account = null;
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    SELECT u.id, u.username, u.role, s.expires_at
                    FROM sessions s
                    JOIN users u ON u.id = s.user_id
                    WHERE s.token_hash = $hash
                    """;
                command.Parameters.AddWithValue("$hash", hash);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                {
                    transaction.Commit();
                    return null;
                }

                var expiresAt = reader.GetInt64(3);
                if (expiresAt <= now)
                {
                    reader.Close();
                    Execute(connection, transaction, "DELETE FROM sessions WHERE token_hash = $hash", ("$hash", hash));
                    transaction.Commit();
                    return null;
                }

                account = new Account(reader.GetString(0), reader.GetString(1), reader.GetString(2));
            }

            transaction.Commit();
            return account;
        }
    }

    public void DeleteSession(string token)
    {
        var hash = Tokens.Hash(token);
        Write((connection, transaction) =>
            Execute(connection, transaction, "DELETE FROM sessions WHERE token_hash = $hash", ("$hash", hash)));
    }

    public ServerRecord GetServer()
    {
        return Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, display_name, public_base_url, created_at FROM server";
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException("La ligne du serveur est absente.");
            return new ServerRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3));
        });
    }

    public ServerRecord UpdateServer(string displayName, string publicBaseUrl)
    {
        Write((connection, transaction) =>
        {
            var changed = Execute(
                connection,
                transaction,
                "UPDATE server SET display_name = $name, public_base_url = $url",
                ("$name", displayName),
                ("$url", publicBaseUrl));
            if (changed != 1)
                throw new InvalidOperationException("La ligne du serveur est absente.");
        });
        return GetServer();
    }

    public IReadOnlyList<WorkerRecord> ListWorkers()
    {
        return Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, display_name, created_at FROM workers ORDER BY display_name, id";
            using var reader = command.ExecuteReader();
            var workers = new List<WorkerRecord>();
            while (reader.Read())
                workers.Add(new WorkerRecord(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
            return (IReadOnlyList<WorkerRecord>)workers;
        });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
    }

    private void InsertSession(SqliteConnection connection, SqliteTransaction transaction, string userId, string token, long now)
    {
        Execute(
            connection,
            transaction,
            """
            INSERT INTO sessions (token_hash, user_id, expires_at, created_at)
            VALUES ($hash, $user, $expires, $created)
            """,
            ("$hash", Tokens.Hash(token)),
            ("$user", userId),
            ("$expires", now + SessionLifetimeMs),
            ("$created", now));
    }

    private void ApplyMigrations()
    {
        Write((connection, transaction) =>
        {
            using (var probe = connection.CreateCommand())
            {
                probe.Transaction = transaction;
                probe.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'schema_migrations'";
                if (probe.ExecuteScalar() is not null
                    && ScalarLong(connection, "SELECT COUNT(*) FROM schema_migrations WHERE id = 1", transaction) > 0)
                    return;
            }

            var sql = ReadMigration("0001_server.sql");
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
            Execute(
                connection,
                transaction,
                "INSERT INTO schema_migrations (id, name, applied_at) VALUES (1, '0001_server', $at)",
                ("$at", DateTimeOffset.UtcNow.ToString("O")));
        });
    }

    private static string ReadMigration(string fileName)
    {
        var assembly = typeof(ServerDatabase).Assembly;
        var name = assembly.GetManifestResourceNames().Single(resource => resource.EndsWith(fileName, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Migration {fileName} absente.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private T Read<T>(Func<SqliteConnection, T> query)
    {
        lock (_gate)
        {
            using var connection = Open();
            return query(connection);
        }
    }

    private void Write(Action<SqliteConnection, SqliteTransaction> command)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            command(connection, transaction);
            transaction.Commit();
        }
    }

    private static long ScalarLong(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command.ExecuteNonQuery();
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

internal sealed class SetupAlreadyCompletedException : Exception;
