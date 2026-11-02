using System.Text.Json;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class UserCollisionReconciliationTests
{
    [Theory]
    [InlineData("admin")]
    [InlineData("ADMIN")]
    public async Task CollisionPreservesIdentitiesUntilExplicitRename(string incomingName)
    {
        await using var source = await TestDatabase.CreateSqliteAsync("users_source");
        await using var target = await TestDatabase.CreateSqliteAsync("users_target");
        var cluster = Guid.NewGuid();
        var hostUser = Guid.NewGuid();
        var joiningUser = Guid.NewGuid();
        foreach (var db in new[] { source.Db, target.Db })
        {
            await DatabaseMigrator.ApplyAsync(db, Harness.RepositoryRoot(), default);
            await new ClusterIdentityRepository(db).AdoptAsync(cluster, "Test", default);
            await using var insert = db.CreateCommand("INSERT INTO users(id,username,password_hash,role) VALUES(@id,@name,@hash,'admin')");
            insert.Parameters.AddWithValue("id", db == source.Db ? hostUser : joiningUser);
            insert.Parameters.AddWithValue("name", db == source.Db ? "admin" : incomingName);
            insert.Parameters.AddWithValue("hash", db == source.Db ? "synthetic-host-hash" : "synthetic-joining-hash");
            await insert.ExecuteNonQueryAsync();
        }
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        using var sourceKey = new ClusterDataKeyStore(cluster, bytes);
        using var targetKey = new ClusterDataKeyStore(cluster, bytes);
        var from = new ClusterEntityReconciliation(source.Db, sourceKey, new EphemeralDataProtectionProvider(), NullLogger<ClusterEntityReconciliation>.Instance);
        var to = new ClusterEntityReconciliation(target.Db, targetKey, new EphemeralDataProtectionProvider(), NullLogger<ClusterEntityReconciliation>.Instance);
        await from.CaptureLocalChangesAsync(default);
        var changes = await new ClusterReconciliationRepository(source.Db).GetEventsAfterAsync(cluster, new Dictionary<Guid,long>(), ClusterReconciliationRepository.MaximumBatchSize, default);
        await new ClusterReconciliationRepository(target.Db).ApplyIncomingAsync(changes, default);
        await to.ApplyPendingAsync(default);
        var diagnostic = Assert.Single(await to.ProjectionDiagnosticsAsync(default));
        Assert.Equal("username_conflict", diagnostic.Cause);
        Assert.DoesNotContain("synthetic-host-hash", JsonSerializer.Serialize(diagnostic));
        // Explicit operator choice: rename only the joining identity. Neither UUID nor credentials change.
        await using (var rename = target.Db.CreateCommand("UPDATE users SET username='admin-50' WHERE id=@id"))
        {
            rename.Parameters.AddWithValue("id", joiningUser);
            await rename.ExecuteNonQueryAsync();
        }
        await to.ApplyPendingAsync(default);
        Assert.Empty(await to.ProjectionDiagnosticsAsync(default));
        await using var accounts = target.Db.CreateCommand("SELECT id,username,password_hash FROM users ORDER BY username");
        await using var reader = await accounts.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(hostUser, reader.GetGuid(0));
        Assert.Equal("admin", reader.GetString(1));
        Assert.Equal("synthetic-host-hash", reader.GetString(2));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(joiningUser, reader.GetGuid(0));
        Assert.Equal("admin-50", reader.GetString(1));
        Assert.Equal("synthetic-joining-hash", reader.GetString(2));
        Assert.False(await reader.ReadAsync());
    }
}
