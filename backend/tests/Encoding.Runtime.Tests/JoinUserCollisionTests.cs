using Encoding.Api.Auth;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Contracts.Cluster;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class JoinUserCollisionTests
{
    [Theory]
    [InlineData("admin")]
    [InlineData("ADMIN")]
    public async Task JoinWaitsForOperatorChoiceAndRenamePreservesCredentialsAndSession(string username)
    {
        await using var db = await TestDatabase.CreateSqliteAsync("join-collisions");
        await DatabaseMigrator.ApplyAsync(db.Db, Harness.RepositoryRoot(), default);
        var users = new UserRepository(db.Db);
        var local = (await users.CreateFirstAdministratorAsync(username, "synthetic-password-hash", default))!;
        var token = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        await users.CreateSessionAsync(local.Id, token, AuthOptions.From(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()), default);
        var hostId = Guid.NewGuid();
        var bootstrap = new ClusterJoinBootstrap(Guid.NewGuid(), "Host", Guid.NewGuid(), [],
            new(Guid.NewGuid(), "0", [new(hostId, "admin")], "verified-by-endpoint"));
        var collisions = new ClusterJoinCollisionRepository(db.Db);
        Assert.False(await collisions.PrepareAsync(bootstrap, default));
        var collision = Assert.Single(await collisions.ListAsync(default));
        Assert.Equal(local.Id, collision.LocalUserId);
        Assert.False(await collisions.ResolveAsync(local.Id, new(username, "AdMiN"), default));
        Assert.False(await collisions.ResolveAsync(local.Id, new("stale", "admin-50"), default));
        Assert.True(await collisions.ResolveAsync(local.Id, new(username, "admin-50"), default));
        Assert.True(await collisions.PrepareAsync(bootstrap, default));
        Assert.Empty(await collisions.ListAsync(default));
        var credentials = await users.FindCredentialsAsync("admin-50", default);
        Assert.NotNull(credentials);
        Assert.Equal(local.Id, credentials.Id);
        Assert.Equal("synthetic-password-hash", credentials.PasswordHash);
        Assert.NotNull(await users.ValidateSessionAsync(token, AuthOptions.From(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()), default));
    }
}
