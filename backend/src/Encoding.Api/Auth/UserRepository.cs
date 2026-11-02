using Encoding.Api.Infrastructure.Data;
using System.Data.Common;
using Encoding.Contracts.Auth;

namespace Encoding.Api.Auth;

public sealed record UserCredentials(Guid Id, string Username, string PasswordHash, UserRole Role, bool IsActive, int FailedAttempts, DateTimeOffset? LockedUntil);

public sealed record SessionUser(Guid Id, string Username, UserRole Role);

public enum UserChangeResult { Updated, NotFound, LastAdministrator }

public sealed class UserRepository(DbDataSource dataSource)
{
    private const string Columns = "id, username, role, is_active, created_at";

    public static string RoleName(UserRole role) => role == UserRole.Admin ? "admin" : "viewer";
    public static UserRole ParseRole(string role) => role == "admin" ? UserRole.Admin : UserRole.Viewer;

    public async Task<bool> AnyAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("SELECT EXISTS(SELECT 1 FROM users)");
        return (await command.ScalarBoolAsync(ct));
    }

    /// <summary>Crée le premier administrateur ; retourne null si un utilisateur existe déjà (course entre serveurs comprise).</summary>
    public async Task<UserResponse?> CreateFirstAdministratorAsync(string username, string passwordHash, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var exists = Sql.Command("SELECT EXISTS(SELECT 1 FROM users)", connection, transaction))
            if ((await exists.ScalarBoolAsync(ct))) return null;
        var user = await InsertAsync(connection, transaction, username, passwordHash, UserRole.Admin, ct);
        await transaction.CommitAsync(ct);
        return user;
    }

    public async Task<UserResponse?> CreateAsync(string username, string passwordHash, UserRole role, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        try { return await InsertAsync(connection, null, username, passwordHash, role, ct); }
        catch (DbException exception) when (Sql.IsUniqueViolation(exception)) { return null; }
    }

    private static async Task<UserResponse> InsertAsync(DbConnection connection, DbTransaction? transaction, string username, string passwordHash, UserRole role, CancellationToken ct)
    {
        await using var command = Sql.Command($"INSERT INTO users(id, username, password_hash, role) VALUES (@id, @username, @hash, @role) RETURNING {Columns}", connection, transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("username", username);
        command.Parameters.AddWithValue("hash", passwordHash);
        command.Parameters.AddWithValue("role", RoleName(role));
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return Read(reader);
    }

    public async Task<IReadOnlyList<UserResponse>> ListAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand($"SELECT {Columns} FROM users ORDER BY lower(username), id");
        await using var reader = await command.ExecuteReaderAsync(ct);
        var users = new List<UserResponse>();
        while (await reader.ReadAsync(ct)) users.Add(Read(reader));
        return users;
    }

    public async Task<UserCredentials?> FindCredentialsAsync(string username, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("SELECT id, username, password_hash, role, is_active, failed_attempts, locked_until FROM users WHERE lower(username) = lower(@username)");
        command.Parameters.AddWithValue("username", username);
        return await ReadCredentialsAsync(command, ct);
    }

    public async Task<UserCredentials?> FindCredentialsAsync(Guid id, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("SELECT id, username, password_hash, role, is_active, failed_attempts, locked_until FROM users WHERE id = @id");
        command.Parameters.AddWithValue("id", id);
        return await ReadCredentialsAsync(command, ct);
    }

    private static async Task<UserCredentials?> ReadCredentialsAsync(DbCommand command, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new UserCredentials(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), ParseRole(reader.GetString(3)), reader.GetBoolean(4), reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6));
    }

    /// <summary>Incrémente le compteur d’échecs ; au-delà du seuil, verrouille avec un délai exponentiel plafonné à 15 minutes.</summary>
    public async Task RecordFailureAsync(Guid id, int threshold, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE users SET failed_attempts = failed_attempts + 1,
                locked_until = CASE WHEN failed_attempts + 1 >= @threshold
                    THEN clock_timestamp() + LEAST(15, power(2, failed_attempts + 1 - @threshold)::integer) * interval '1 minute'
                    ELSE locked_until END
            WHERE id = @id
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("threshold", threshold);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordSuccessAsync(Guid id, string? upgradedHash, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("UPDATE users SET failed_attempts = 0, locked_until = NULL, password_hash = COALESCE(@hash, password_hash) WHERE id = @id");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("hash", SqlKind.Text, (object?)upgradedHash ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Remplace le mot de passe et révoque les sessions de l’utilisateur, sauf <paramref name="keepSession"/>.</summary>
    public async Task<bool> SetPasswordAsync(Guid id, string passwordHash, byte[]? keepSession, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var update = Sql.Command("UPDATE users SET password_hash = @hash, failed_attempts = 0, locked_until = NULL, updated_at = clock_timestamp() WHERE id = @id",
            connection, transaction);
        update.Parameters.AddWithValue("id", id);
        update.Parameters.AddWithValue("hash", passwordHash);
        if (await update.ExecuteNonQueryAsync(ct) == 0) return false;
        await RevokeSessionsAsync(connection, transaction, id, keepSession, ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<UserChangeResult> UpdateAsync(Guid id, UserRole role, bool isActive, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (!await ExistsAsync(connection, transaction, id, ct)) return UserChangeResult.NotFound;
        if ((role != UserRole.Admin || !isActive) && await IsLastActiveAdministratorAsync(connection, transaction, id, ct)) return UserChangeResult.LastAdministrator;
        await using var update = Sql.Command("UPDATE users SET role = @role, is_active = @active, updated_at = clock_timestamp() WHERE id = @id", connection, transaction);
        update.Parameters.AddWithValue("id", id);
        update.Parameters.AddWithValue("role", RoleName(role));
        update.Parameters.AddWithValue("active", isActive);
        await update.ExecuteNonQueryAsync(ct);
        if (!isActive) await RevokeSessionsAsync(connection, transaction, id, null, ct);
        await transaction.CommitAsync(ct);
        return UserChangeResult.Updated;
    }

    public async Task<UserChangeResult> DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (!await ExistsAsync(connection, transaction, id, ct)) return UserChangeResult.NotFound;
        if (await IsLastActiveAdministratorAsync(connection, transaction, id, ct)) return UserChangeResult.LastAdministrator;
        await using var delete = Sql.Command("DELETE FROM users WHERE id = @id", connection, transaction);
        delete.Parameters.AddWithValue("id", id);
        await delete.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return UserChangeResult.Updated;
    }

    private static async Task<bool> ExistsAsync(DbConnection connection, DbTransaction transaction, Guid id, CancellationToken ct)
    {
        await using var command = Sql.Command("SELECT EXISTS(SELECT 1 FROM users WHERE id = @id)", connection, transaction);
        command.Parameters.AddWithValue("id", id);
        return (await command.ScalarBoolAsync(ct));
    }

    private static async Task<bool> IsLastActiveAdministratorAsync(DbConnection connection, DbTransaction transaction, Guid id, CancellationToken ct)
    {
        await using var command = Sql.Command("""
            SELECT EXISTS(SELECT 1 FROM users WHERE id = @id AND role = 'admin' AND is_active)
               AND NOT EXISTS(SELECT 1 FROM users WHERE id <> @id AND role = 'admin' AND is_active)
            """, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        return (await command.ScalarBoolAsync(ct));
    }

    public async Task CreateSessionAsync(Guid userId, byte[] tokenHash, AuthOptions options, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            DELETE FROM user_sessions WHERE expires_at <= clock_timestamp();
            INSERT INTO user_sessions(token_hash, user_id, expires_at) VALUES (@hash, @user, @expires)
            """);
        command.Parameters.AddWithValue("hash", SqlKind.Bytes, tokenHash);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("expires",
            DateTimeOffset.UtcNow + (options.SessionIdleTimeout < options.SessionAbsoluteLifetime ? options.SessionIdleTimeout : options.SessionAbsoluteLifetime));
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Valide une session et prolonge son inactivité (au plus une écriture par minute), sans dépasser sa durée absolue.</summary>
    public async Task<SessionUser?> ValidateSessionAsync(byte[] tokenHash, AuthOptions options, CancellationToken ct)
    {
        return await ValidateSessionSqliteAsync(tokenHash, options, ct);
    }

    // SQLite n'accepte pas d'écriture dans une expression de table : lecture puis prolongation en deux ordres, dans une transaction.
    private async Task<SessionUser?> ValidateSessionSqliteAsync(byte[] tokenHash, AuthOptions options, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        SessionUser? user;
        DateTimeOffset createdAt, lastSeenAt;
        await using (var read = Sql.Command("""
            SELECT u.id, u.username, u.role, s.created_at, s.last_seen_at
            FROM user_sessions s JOIN users u ON u.id = s.user_id
            WHERE s.token_hash = @hash AND u.is_active AND s.expires_at > clock_timestamp()
            """, connection, transaction))
        {
            read.Parameters.AddWithValue("hash", SqlKind.Bytes, tokenHash);
            await using var reader = await read.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            user = new SessionUser(reader.GetGuid(0), reader.GetString(1), ParseRole(reader.GetString(2)));
            createdAt = reader.GetFieldValue<DateTimeOffset>(3);
            lastSeenAt = reader.GetFieldValue<DateTimeOffset>(4);
        }
        var now = DateTimeOffset.UtcNow;
        if (lastSeenAt < now - TimeSpan.FromMinutes(1))
        {
            try
            {
                var absolute = createdAt + options.SessionAbsoluteLifetime;
                var idle = now + options.SessionIdleTimeout;
                await using var touch = Sql.Command("UPDATE user_sessions SET last_seen_at = @now, expires_at = @expires WHERE token_hash = @hash", connection, transaction);
                touch.Parameters.AddWithValue("hash", SqlKind.Bytes, tokenHash);
                touch.Parameters.AddWithValue("now", now);
                touch.Parameters.AddWithValue("expires", absolute < idle ? absolute : idle);
                await touch.ExecuteNonQueryAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch
            {
                // Un échec de rafraîchissement d'expiration ne doit pas interrompre une session valide.
            }
        }
        else
        {
            try { await transaction.CommitAsync(ct); }
            catch { /* Ignorer si en lecture seule */ }
        }
        return user;
    }

    public async Task DeleteSessionAsync(byte[] tokenHash, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("DELETE FROM user_sessions WHERE token_hash = @hash");
        command.Parameters.AddWithValue("hash", SqlKind.Bytes, tokenHash);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task RevokeSessionsAsync(DbConnection connection, DbTransaction transaction, Guid userId, byte[]? keep, CancellationToken ct)
    {
        await using var command = Sql.Command("DELETE FROM user_sessions WHERE user_id = @user AND (@keep IS NULL OR token_hash <> @keep)", connection, transaction);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("keep", SqlKind.Bytes, (object?)keep ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static UserResponse Read(DbDataReader reader) =>
        new(reader.GetGuid(0), reader.GetString(1), ParseRole(reader.GetString(2)), reader.GetBoolean(3), reader.GetFieldValue<DateTimeOffset>(4));
}
