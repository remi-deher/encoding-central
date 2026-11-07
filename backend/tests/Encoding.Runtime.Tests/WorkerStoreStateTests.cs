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

/// <summary>Issue #17 P0 et ADR 0046 (T1) : état appliqué par worker contre la révision globale du central, écart, validation, permissions.</summary>
public sealed class WorkerStoreStateTests
{
    private static readonly string Sha = new('a', 64);
    private static StoreAppliedItem Applied(string id = "acme.tools", string version = "1.0.0", string status = "applied", string? error = null) =>
        new("plugin", id, version, null, Sha, status, error);

    private static async Task<(TestDatabase Database, WorkerStoreStateRepository States, CentralStoreStateRepository Central, Guid First, Guid Second)> CreateAsync()
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
        return (database, new WorkerStoreStateRepository(database.Db), new CentralStoreStateRepository(database.Db), first, second);
    }

    /// <summary>Fait avancer la révision globale du central sans installer de paquet : une empreinte différente par appel.</summary>
    private static async Task AdvanceAsync(CentralStoreStateRepository central, int times = 1)
    {
        for (var i = 0; i < times; i++) await central.SyncAsync(Guid.NewGuid().ToString("N"), CancellationToken.None);
    }

    [Fact]
    public async Task NewWorkerHasEmptyAppliedStateAndUnknownWorkerHasNone()
    {
        var (database, states, _, first, _) = await CreateAsync();
        await using var _ = database;
        var applied = await states.GetAppliedAsync(first, CancellationToken.None);
        Assert.NotNull(applied);
        Assert.Equal(0, applied.Revision); Assert.Empty(applied.Items);
        Assert.Null(await states.GetAppliedAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task AppliedReportIsBoundedByTheGlobalRevisionAndNeverGoesBackwards()
    {
        var (database, states, central, first, second) = await CreateAsync();
        await using var _ = database;
        Assert.Equal(StoreReportOutcome.UnknownRevision, (await states.ReportAppliedAsync(first, 1, [Applied()], CancellationToken.None)).Outcome); // central à 0
        await AdvanceAsync(central, 2);

        var unknown = await states.ReportAppliedAsync(first, 3, [Applied()], CancellationToken.None);
        Assert.Equal(StoreReportOutcome.UnknownRevision, unknown.Outcome);
        Assert.Equal(2, unknown.DesiredRevision); // le worker apprend la révision attendue
        Assert.Equal(StoreReportOutcome.Accepted, (await states.ReportAppliedAsync(first, 2, [Applied(version: "1.1.0")], CancellationToken.None)).Outcome); // la ligne se crée au premier rapport
        Assert.Equal(StoreReportOutcome.Accepted, (await states.ReportAppliedAsync(first, 2, [Applied(version: "1.1.0")], CancellationToken.None)).Outcome); // idempotent
        var stale = await states.ReportAppliedAsync(first, 1, [Applied()], CancellationToken.None);
        Assert.Equal(StoreReportOutcome.StaleRevision, stale.Outcome);
        Assert.Equal(2, stale.DesiredRevision);

        var applied = (await states.GetAppliedAsync(first, CancellationToken.None))!;
        Assert.Equal(2, applied.Revision); Assert.Equal("1.1.0", Assert.Single(applied.Items).Version);
        // Les rapports d'un worker n'affectent jamais un autre worker.
        Assert.Equal(0, (await states.GetAppliedAsync(second, CancellationToken.None))!.Revision);
        Assert.Equal(StoreReportOutcome.UnknownWorker, (await states.ReportAppliedAsync(Guid.NewGuid(), 1, [], CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task DesiredRevisionColumnKeepsTheMaxOfGlobalAndAppliedRevision()
    {
        var (database, states, central, first, _) = await CreateAsync();
        await using var _ = database;
        await AdvanceAsync(central, 3);
        await states.ReportAppliedAsync(first, 2, [], CancellationToken.None);
        await using var read = database.Db.CreateCommand("SELECT desired_revision, applied_revision FROM worker_store_state WHERE worker_id = @id");
        read.Parameters.AddWithValue("id", first);
        await using var reader = await read.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(3, reader.GetInt64(0)); // max(globale, appliquée), jamais inférieure à l'appliquée (CHECK de 0076)
        Assert.Equal(2, reader.GetInt64(1));
    }

    [Fact]
    public void GapReportsMissingMismatchedFailedAndExtraPackages()
    {
        static StoreDesiredItem Want(string id, string version, string sha) => new("plugin", id, version, null, sha);
        var desired = new StoreDesiredState(5, [Want("a", "1.0.0", Sha), Want("b", "1.0.0", Sha), Want("c", "1.0.0", Sha), Want("d", "1.0.0", Sha), Want("e", "1.0.0", Sha)], null);
        var applied = new StoreAppliedState(4,
        [
            new("plugin", "a", "1.0.0", null, Sha, "applied", null),                        // conforme
            new("plugin", "b", "0.9.0", null, Sha, "applied", null),                        // autre version
            new("plugin", "c", "1.0.0", null, Sha, "failed", "sha256_mismatch"),            // échec rapporté
            new("plugin", "e", "1.0.0", null, new string('f', 64), "applied", null),        // même version, autre empreinte
            new("plugin", "z", "1.0.0", null, Sha, "applied", null),                        // plus installé sur le central
            new("plugin", "y", "1.0.0", null, Sha, "removed", null)                         // déjà retiré : pas un écart
        ], null);
        var gap = WorkerStoreStateService.ComputeGap(desired, applied);
        Assert.False(gap.InSync);
        Assert.Equal(1, gap.RevisionLag);
        Assert.Equal(new[] { "missing:d", "version_mismatch:b", "failed:c", "version_mismatch:e", "extra:z" }.Order(),
            gap.Items.Select(item => item.Reason + ":" + item.Id).Order());
        Assert.Equal("sha256_mismatch", gap.Items.Single(item => item.Reason == "failed").ErrorCode);

        var synced = WorkerStoreStateService.ComputeGap(new StoreDesiredState(2, [Want("a", "1.0.0", Sha)], null),
            new StoreAppliedState(2, [new("plugin", "a", "1.0.0", null, Sha, "applied", null)], null));
        Assert.True(synced.InSync); Assert.Equal(0, synced.RevisionLag); Assert.Empty(synced.Items);
        // Un worker qui n'a rien rapporté a tout en retard.
        var fresh = WorkerStoreStateService.ComputeGap(new StoreDesiredState(2, [Want("a", "1.0.0", Sha)], null), new StoreAppliedState(0, [], null));
        Assert.Equal("missing", Assert.Single(fresh.Items).Reason); Assert.Equal(2, fresh.RevisionLag);
    }

    [Fact]
    public void AppliedReportsAreValidatedAndErrorsStayCodes()
    {
        Assert.Throws<StoreStateException>(() => WorkerStoreStateRepository.ValidateApplied([new("plugin", "acme.tools", "latest", null, Sha, "applied", null)]));
        Assert.Throws<StoreStateException>(() => WorkerStoreStateRepository.ValidateApplied([new("plugin", "acme.tools", "1.0.0", "linux-x64", Sha, "applied", null)]));
        Assert.Throws<StoreStateException>(() => WorkerStoreStateRepository.ValidateApplied([new("tool", "ffmpeg", "7.1.0", null, Sha, "applied", null)]));
        Assert.Throws<StoreStateException>(() => WorkerStoreStateRepository.ValidateApplied([new("plugin", "../etc", "1.0.0", null, Sha, "applied", null)]));
        Assert.Throws<StoreStateException>(() => WorkerStoreStateRepository.ValidateApplied([new("plugin", "acme.tools", "1.0.0", null, "", "applied", null)]));
        Assert.Throws<StoreStateException>(() => WorkerStoreStateRepository.ValidateApplied([Applied(), Applied()]));
        Assert.Throws<StoreStateException>(() => WorkerStoreStateRepository.ValidateApplied([Applied(status: "installing")]));
        Assert.Throws<StoreStateException>(() => WorkerStoreStateRepository.ValidateApplied([Applied(status: "failed", error: "System.IO.IOException: /home/x token=abc")]));
        Assert.Single(WorkerStoreStateRepository.ValidateApplied([Applied(status: "failed", error: "sha256_mismatch")]));
        Assert.Equal(Sha, Assert.Single(WorkerStoreStateRepository.ValidateApplied([new("tool", "ffmpeg", "7.1.0", "linux-x64", Sha.ToUpperInvariant(), "applied", null)])).Sha256);
    }

    [Fact]
    public async Task WorkerTokenNeverGrantsWritesAndOnlyAdminsWrite()
    {
        var (database, _, _, _, _) = await CreateAsync();
        await using var _ = database;
        var handler = new ManagementAccessHandler(new WorkerRepository(database.Db));
        // Un worker retiré (départ ou suppression) : son ancien jeton ne vaut plus rien.
        await using (var retired = database.Db.CreateCommand("INSERT INTO workers(id,name,roles,max_concurrent_tasks,capabilities,last_seen_at,worker_token_hash,deleted_at) VALUES(@id,'w','worker',1,'[]',(strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z'),@hash,(strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z'))"))
        {
            retired.Parameters.AddWithValue("id", Guid.NewGuid());
            retired.Parameters.AddWithValue("hash", SqlKind.Bytes, SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("token-gone")));
            await retired.ExecuteNonQueryAsync();
        }

        async Task<bool> Succeeds(string method, string? token, ClaimsPrincipal user, bool nodeCredentials, bool report = false)
        {
            var http = new DefaultHttpContext { User = user };
            http.Request.Method = method;
            if (token is not null) http.Request.Headers["X-Node-Token"] = token;
            http.Request.Headers[ManagementAccessHandler.CsrfHeader] = "1";
            var metadata = report ? new EndpointMetadataCollection(new WorkerCredentialAccessMetadata(), new WorkerReportAccessMetadata())
                : nodeCredentials ? new EndpointMetadataCollection(new WorkerCredentialAccessMetadata()) : EndpointMetadataCollection.Empty;
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

        // #23 : le rapport d'état appliqué (PUT, AllowNodeCredentialReport) accepte le jeton d'un worker enrôlé, et lui seul.
        Assert.True(await Succeeds("PUT", "token-one", anonymous, nodeCredentials: true, report: true));
        Assert.False(await Succeeds("PUT", null, anonymous, nodeCredentials: true, report: true));          // jeton absent
        Assert.False(await Succeeds("PUT", "token-unknown", anonymous, nodeCredentials: true, report: true)); // jeton inconnu
        Assert.False(await Succeeds("PUT", "token-gone", anonymous, nodeCredentials: true, report: true));    // worker retiré
        Assert.False(await Succeeds("PUT", null, Person("viewer"), nodeCredentials: true, report: true));    // un lecteur ne remet pas de rapport
        Assert.False(await Succeeds("POST", "token-one", anonymous, nodeCredentials: true)); // AllowNodeCredentials seul n'ouvre aucune écriture
        Assert.False(await Succeeds("DELETE", "token-one", anonymous, nodeCredentials: true));
    }
}
