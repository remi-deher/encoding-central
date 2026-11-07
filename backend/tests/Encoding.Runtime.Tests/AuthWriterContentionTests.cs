using System.Diagnostics;
using Encoding.Api.Auth;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Infrastructure.Data.Sqlite;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>
/// #32 (banc NAS #18) : pendant un long traitement, l'écrivain SQLite est occupé. Valider une session ou un jeton API ne doit ni attendre
/// l'écrivain jusqu'à son délai (5 s) ni répondre 503 : la lecture se fait hors file d'écriture, la prolongation est tentée au mieux.
/// </summary>
public sealed class AuthWriterContentionTests
{
    private static readonly AuthOptions Options = new(TimeSpan.FromHours(8), TimeSpan.FromDays(7), null, null, 5, 20, 20);

    private static async Task<(TestDatabase Database, SqliteWriter Writer)> CreateAsync()
    {
        var database = await TestDatabase.CreateSqliteAsync("auth-contention");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);
        return (database, ((SqliteDataSource)database.Db).Writer);
    }

    private static async Task ExecuteAsync(TestDatabase database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = database.Db.CreateCommand(sql);
        foreach (var (name, value) in parameters)
            if (value is byte[] bytes) command.Parameters.AddWithValue(name, SqlKind.Bytes, bytes);
            else command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(UserRepository Users, byte[] Hash, Guid UserId)> SessionAsync(TestDatabase database)
    {
        var users = new UserRepository(database.Db);
        var admin = await users.CreateFirstAdministratorAsync("admin", "hash", CancellationToken.None);
        Assert.NotNull(admin);
        var hash = AuthService.TokenHash("session-" + Guid.NewGuid().ToString("N"));
        await users.CreateSessionAsync(admin!.Id, hash, Options, CancellationToken.None);
        // Dernière activité ancienne : la validation tente une prolongation (écriture).
        await ExecuteAsync(database, "UPDATE user_sessions SET last_seen_at = '2020-01-01 00:00:00.000000Z' WHERE token_hash = @hash", ("hash", hash));
        return (users, hash, admin.Id);
    }

    private static async Task<string?> LastSeenAsync(TestDatabase database, byte[] hash)
    {
        await using var command = database.Db.CreateCommand("SELECT last_seen_at FROM user_sessions WHERE token_hash = @hash");
        command.Parameters.AddWithValue("hash", SqlKind.Bytes, hash);
        return (await command.ExecuteScalarAsync())?.ToString();
    }

    [Fact]
    public async Task SessionValidationDoesNotWaitForABusyWriter()
    {
        var (database, writer) = await CreateAsync();
        await using var _ = database;
        var (users, hash, userId) = await SessionAsync(database);

        SessionUser? user;
        var watch = Stopwatch.StartNew();
        using (await writer.EnterAsync()) // un long traitement détient l'écrivain
            user = await users.ValidateSessionAsync(hash, Options, CancellationToken.None);
        watch.Stop();

        // Avant #32 : transaction d'écriture ouverte d'office → attente de 5 s puis SqliteWriteUnavailableException (503).
        Assert.Equal(userId, user?.Id);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"La validation a attendu l'écrivain {watch.Elapsed.TotalMilliseconds:F0} ms.");
        Assert.StartsWith("2020-01-01", await LastSeenAsync(database, hash)); // prolongation abandonnée, retentée plus tard

        // Écrivain libre : la prolongation se fait.
        Assert.Equal(userId, (await users.ValidateSessionAsync(hash, Options, CancellationToken.None))?.Id);
        Assert.DoesNotContain("2020-01-01", await LastSeenAsync(database, hash));
    }

    [Fact]
    public async Task ExpiredOrRevokedSessionsAreNeitherAcceptedNorRevived()
    {
        var (database, _) = await CreateAsync();
        await using var _db = database;
        var (users, hash, _) = await SessionAsync(database);

        await ExecuteAsync(database, "UPDATE user_sessions SET expires_at = '2020-01-02 00:00:00.000000Z' WHERE token_hash = @hash", ("hash", hash));
        Assert.Null(await users.ValidateSessionAsync(hash, Options, CancellationToken.None));
        Assert.Null(await users.ValidateSessionAsync(hash, Options, CancellationToken.None)); // la prolongation conditionnelle ne l'a pas ressuscitée

        Assert.Null(await users.ValidateSessionAsync(AuthService.TokenHash("absent"), Options, CancellationToken.None)); // session inconnue
    }

    [Fact]
    public async Task ApiTokenValidationDoesNotWaitForABusyWriter()
    {
        var (database, writer) = await CreateAsync();
        await using var _ = database;
        var tokens = new ApiTokenRepository(database.Db);
        var (summary, plaintext) = await tokens.CreateTokenAsync("bench", "viewer", null, CancellationToken.None);

        ApiTokenRecord? record;
        var watch = Stopwatch.StartNew();
        using (await writer.EnterAsync())
            record = await tokens.ValidateTokenAsync(AuthService.TokenHash(plaintext), CancellationToken.None);
        watch.Stop();

        Assert.Equal(summary.Id, record?.Id);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"La validation du jeton a attendu l'écrivain {watch.Elapsed.TotalMilliseconds:F0} ms.");
    }

    [Fact]
    public async Task BestEffortWriteGivesUpWithinItsBudgetWhenTheWriterIsHeld()
    {
        var (database, writer) = await CreateAsync();
        await using var _ = database;
        bool done;
        var watch = Stopwatch.StartNew();
        using (await writer.EnterAsync())
            done = await BestEffortWrite.TryAsync(async limit =>
            {
                await using var command = database.Db.CreateCommand("UPDATE users SET updated_at = updated_at");
                await command.ExecuteNonQueryAsync(limit);
            }, CancellationToken.None);
        watch.Stop();
        Assert.False(done);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.True(await BestEffortWrite.TryAsync(async limit =>
        {
            await using var command = database.Db.CreateCommand("UPDATE users SET updated_at = updated_at");
            await command.ExecuteNonQueryAsync(limit);
        }, CancellationToken.None));
    }
}
