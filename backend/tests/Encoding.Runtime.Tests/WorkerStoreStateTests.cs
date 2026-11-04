using System.Security.Claims;
using System.Security.Cryptography;
using Encoding.Api.Auth;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Plugins;
using Encoding.Api.Workers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>Issue #17, P0 : état Store désiré/appliqué par worker, révisions monotones, défauts vides, permissions.</summary>
public sealed class WorkerStoreStateTests
{
    private static readonly string Sha = new('a', 64);
    private static StoreDesiredItem Plugin(string id = "acme.tools", string version = "1.0.0") => new("plugin", id, version, null, Sha);
    private static StoreAppliedItem Applied(string id = "acme.tools", string version = "1.0.0", string status = "applied", string? error = null) =>
        new("plugin", id, version, null, Sha, status, error);

    private static async Task<(TestDatabase Database, WorkerStoreStateRepository States, Guid First, Guid Second)> CreateAsync()
    {
        var database = await TestDatabase.CreateSqliteAsync("store-state");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        foreach (var (id, token) in new[] { (first, "token-one"), (second, "token-two") })
        {
            await using var insert = database.Db.CreateCommand("INSERT INTO workers(id,name,roles,max_concurrent_tasks,capabilities,last_seen_at,worker_token_hash) VALUES(@id,'w','worker',1,'[]',(strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z'),@hash)");
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("hash", SqlKind.Bytes, SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
            await insert.ExecuteNonQueryAsync();
        }
        return (database, new WorkerStoreStateRepository(database.Db), first, second);
    }

    [Fact]
    public async Task NewWorkerHasEmptyStateAndUnknownWorkerHasNone()
    {
        var (database, states, first, _) = await CreateAsync();
        await using var _ = database;
        var state = await states.GetAsync(first, CancellationToken.None);
        Assert.NotNull(state);
        Assert.Equal(0, state.Desired.Revision); Assert.Empty(state.Desired.Items);
        Assert.Equal(0, state.Applied.Revision); Assert.Empty(state.Applied.Items);
        Assert.Null(await states.GetAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Null(await states.ReplaceDesiredAsync(Guid.NewGuid(), [Plugin()], CancellationToken.None));
    }

    [Fact]
    public async Task DesiredRevisionIsMonotonePerWorkerAndSeparateFromApplied()
    {
        var (database, states, first, second) = await CreateAsync();
        await using var _ = database;
        Assert.Equal(1, (await states.ReplaceDesiredAsync(first, [Plugin()], CancellationToken.None))!.Revision);
        Assert.Equal(2, (await states.ReplaceDesiredAsync(first, [Plugin(version: "1.1.0")], CancellationToken.None))!.Revision);
        Assert.Equal(1, (await states.ReplaceDesiredAsync(second, [], CancellationToken.None))!.Revision);

        var state = (await states.GetAsync(first, CancellationToken.None))!;
        Assert.Equal("1.1.0", Assert.Single(state.Desired.Items).Version);
        Assert.Equal(0, state.Applied.Revision); // le désiré ne touche pas l'appliqué
        Assert.Empty((await states.GetAsync(second, CancellationToken.None))!.Desired.Items);
    }

    [Fact]
    public async Task AppliedReportIsBoundedByDesiredAndNeverGoesBackwards()
    {
        var (database, states, first, second) = await CreateAsync();
        await using var _ = database;
        Assert.Equal(StoreReportOutcome.UnknownRevision, (await states.ReportAppliedAsync(first, 1, [Applied()], CancellationToken.None)).Outcome);
        await states.ReplaceDesiredAsync(first, [Plugin()], CancellationToken.None);
        await states.ReplaceDesiredAsync(first, [Plugin(version: "1.1.0")], CancellationToken.None);

        Assert.Equal(StoreReportOutcome.UnknownRevision, (await states.ReportAppliedAsync(first, 3, [Applied()], CancellationToken.None)).Outcome);
        Assert.Equal(StoreReportOutcome.Accepted, (await states.ReportAppliedAsync(first, 2, [Applied(version: "1.1.0")], CancellationToken.None)).Outcome);
        Assert.Equal(StoreReportOutcome.Accepted, (await states.ReportAppliedAsync(first, 2, [Applied(version: "1.1.0")], CancellationToken.None)).Outcome); // idempotent
        var stale = await states.ReportAppliedAsync(first, 1, [Applied()], CancellationToken.None);
        Assert.Equal(StoreReportOutcome.StaleRevision, stale.Outcome);
        Assert.Equal(2, stale.DesiredRevision);

        var state = (await states.GetAsync(first, CancellationToken.None))!;
        Assert.Equal(2, state.Applied.Revision); Assert.Equal("1.1.0", Assert.Single(state.Applied.Items).Version);
        // Les rapports d'un worker n'affectent jamais un autre worker.
        Assert.Equal(0, (await states.GetAsync(second, CancellationToken.None))!.Applied.Revision);
        Assert.Equal(StoreReportOutcome.UnknownRevision, (await states.ReportAppliedAsync(second, 1, [Applied()], CancellationToken.None)).Outcome);
        Assert.Equal(StoreReportOutcome.UnknownWorker, (await states.ReportAppliedAsync(Guid.NewGuid(), 1, [], CancellationToken.None)).Outcome);
    }

    [Theory]
    [InlineData("plugin", "acme.tools", "latest", null)]
    [InlineData("plugin", "acme.tools", "1.0", null)]
    [InlineData("plugin", "acme.tools", "1.0.0", "linux-x64")]
    [InlineData("tool", "ffmpeg", "7.1.0", null)]
    [InlineData("other", "acme.tools", "1.0.0", null)]
    [InlineData("plugin", "../etc", "1.0.0", null)]
    public void InvalidDesiredItemsAreRefused(string kind, string id, string version, string? platform) =>
        Assert.Throws<StoreStateException>(() => WorkerStoreStateRepository.ValidateDesired([new(kind, id, version, platform, Sha)]));

    [Fact]
    public void DesiredItemsRequireShaAreUniqueAndAppliedErrorsStayCodes()
    {
        Assert.Throws<StoreStateException>(() => WorkerStoreStateRepository.ValidateDesired([new("plugin", "acme.tools", "1.0.0", null, "")]));
        Assert.Throws<StoreStateException>(() => WorkerStoreStateRepository.ValidateDesired([Plugin(), Plugin()]));
        Assert.Single(WorkerStoreStateRepository.ValidateDesired([new("tool", "ffmpeg", "7.1.0", "linux-x64", Sha.ToUpperInvariant())]));
        Assert.Throws<StoreStateException>(() => WorkerStoreStateRepository.ValidateApplied([Applied(status: "installing")]));
        Assert.Throws<StoreStateException>(() => WorkerStoreStateRepository.ValidateApplied([Applied(status: "failed", error: "System.IO.IOException: /home/x token=abc")]));
        Assert.Single(WorkerStoreStateRepository.ValidateApplied([Applied(status: "failed", error: "sha256_mismatch")]));
    }

    [Fact]
    public async Task WorkerTokenNeverGrantsWritesAndOnlyAdminsWrite()
    {
        var (database, _, _, _) = await CreateAsync();
        await using var _ = database;
        var handler = new ManagementAccessHandler(new WorkerRepository(database.Db));

        async Task<bool> Succeeds(string method, string? token, ClaimsPrincipal user, bool nodeCredentials)
        {
            var http = new DefaultHttpContext { User = user };
            http.Request.Method = method;
            if (token is not null) http.Request.Headers["X-Node-Token"] = token;
            http.Request.Headers[ManagementAccessHandler.CsrfHeader] = "1";
            var metadata = nodeCredentials ? new EndpointMetadataCollection(new WorkerCredentialAccessMetadata()) : EndpointMetadataCollection.Empty;
            http.SetEndpoint(new Endpoint(null, metadata, "test"));
            var context = new AuthorizationHandlerContext([new ManagementAccessRequirement()], user, http);
            await handler.HandleAsync(context);
            return context.HasSucceeded;
        }
        ClaimsPrincipal Person(string role) => new(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test"));
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.False(await Succeeds("PUT", "token-one", anonymous, nodeCredentials: true)); // un worker ne modifie pas l'état désiré
        Assert.False(await Succeeds("PUT", "token-one", anonymous, nodeCredentials: false));
        Assert.False(await Succeeds("GET", "token-one", anonymous, nodeCredentials: false)); // route de lecture non ouverte aux jetons worker
        Assert.False(await Succeeds("PUT", null, anonymous, nodeCredentials: false));
        Assert.False(await Succeeds("PUT", null, Person("viewer"), nodeCredentials: false)); // un lecteur ne modifie pas
        Assert.True(await Succeeds("PUT", null, Person("admin"), nodeCredentials: false));
        Assert.True(await Succeeds("GET", null, Person("viewer"), nodeCredentials: false)); // un lecteur peut lire
    }
}
