using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Encoding.Core.Nodes;
using Encoding.NodeAgent;
using Encoding.NodeAgent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using static Encoding.Plugins.Tests.Support;

namespace Encoding.Plugins.Tests;

/// <summary>Rattachement d'un nœud à un cluster : le serveur local reste actif, départ, changement en deux phases et cluster injoignable.</summary>
internal static class MembershipTests
{
    public static async Task RunAsync()
    {
        var data = Path.Combine(Path.GetTempPath(), "encoding-membership-" + Guid.NewGuid().ToString("N"));
        var leaveStatus = HttpStatusCode.NoContent;
        var leaves = new List<(string Path, string Token)>();
        var mode = new NodeMode(true, true);

        var clusterBuilder = WebApplication.CreateBuilder();
        clusterBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var cluster = clusterBuilder.Build();
        cluster.MapPost("/api/v1/nodes/{id}/leave", (string id, HttpRequest request) =>
        {
            leaves.Add((request.Path.Value!, request.Headers["X-Node-Token"].ToString()));
            return Results.StatusCode((int)leaveStatus);
        });
        await cluster.StartAsync();
        var clusterUrl = cluster.Urls.Single().TrimEnd('/') + "/";

        var launcherBuilder = WebApplication.CreateBuilder();
        launcherBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var launcherApp = launcherBuilder.Build();
        launcherApp.MapGet("/mode", () => Results.Ok(mode));
        launcherApp.MapPut("/mode", (NodeMode requested) => { mode = requested; return Results.Ok(mode); });
        await launcherApp.StartAsync();
        var launcher = new LauncherControlClient(new HttpClient { BaseAddress = new Uri(launcherApp.Urls.Single().TrimEnd('/') + "/") });

        try
        {
            Directory.CreateDirectory(data);
            var secretFile = Path.Combine(data, "secret");
            File.WriteAllText(secretFile, "secret");
            var localUrl = "http://127.0.0.1:18080/";
            var options = new NodeAgentOptions("membership", [Encoding.Contracts.Nodes.NodeRole.Server, Encoding.Contracts.Nodes.NodeRole.Worker], 1, data,
                localUrl, false, TimeSpan.FromSeconds(10), 8081, secretFile);
            var store = new WorkerIdentityStore(options);
            var enrollment = new WorkerEnrollmentService(options, store, new WorkerClusterDiscovery(options, NullLogger<WorkerClusterDiscovery>.Instance),
                null!, NullLogger<WorkerEnrollmentService>.Instance, Microsoft.Extensions.Options.Options.Create(new Microsoft.AspNetCore.Http.Json.JsonOptions()), launcher);

            // Arrivée dans un autre cluster : le serveur local reste actif.
            await enrollment.KeepLocalServerActiveAsync(CancellationToken.None);
            Check(mode == new NodeMode(true, true), "A server+worker node keeps its local server when joining another cluster.");
            await enrollment.KeepLocalServerActiveAsync(CancellationToken.None);
            Check(mode == new NodeMode(true, true), "Keeping the local server active twice changes nothing.");

            mode = new NodeMode(false, true);
            await enrollment.KeepLocalServerActiveAsync(CancellationToken.None);
            Check(mode == new NodeMode(true, true), "A worker-only node enables its local server when joining another cluster.");

            // Rattaché au cluster distant : le départ le prévient avec son jeton, efface l'identité et rend le serveur local.
            var joined = await store.SaveEnrollmentAsync(Guid.NewGuid().ToString("D"), "Cluster B", clusterUrl, CancellationToken.None);
            await store.MarkEnrolledAsync(CancellationToken.None);
            leaveStatus = HttpStatusCode.Conflict;
            var busy = await Assert.ThrowsAsync<InvalidOperationException>(() => enrollment.LeaveAsync(CancellationToken.None));
            var stillJoined = await store.GetAsync(CancellationToken.None);
            Check(stillJoined.IsEnrolled && stillJoined.Id == joined.Id && mode == new NodeMode(true, true), "A node busy with a task cannot leave: identity and mode are unchanged.");
            Check(busy.Message.Contains("traite encore"), "The refusal explains that the node is still processing.");

            leaveStatus = HttpStatusCode.NoContent;
            await enrollment.LeaveAsync(CancellationToken.None);
            var left = await store.GetAsync(CancellationToken.None);
            Check(leaves.Last().Path == $"/api/v1/nodes/{joined.Id:D}/leave" && leaves.Last().Token == joined.NodeToken, "The node tells its cluster, authenticated by its own token.");
            Check(!left.HasCredentials && left.Id != joined.Id, "After leaving, the node restarts from a fresh identity (a removed identity never comes back).");
            Check(mode == new NodeMode(true, true), "After leaving, the node gets its local server back (standalone node).");

            // Cluster distant injoignable : le départ reste possible, localement.
            var orphan = await store.SaveEnrollmentAsync(Guid.NewGuid().ToString("D"), "Cluster perdu", "http://127.0.0.1:1/", CancellationToken.None);
            await enrollment.LeaveAsync(CancellationToken.None);
            Check(!(await store.GetAsync(CancellationToken.None)).HasCredentials, "An unreachable cluster does not trap the node.");
            Check(orphan.NodeToken is not null, "Sanity: the abandoned enrollment had credentials.");

            // Changement de cluster en deux phases (0040) : l'ancien rattachement reste intact jusqu'à la confirmation du nouveau.
            mode = new NodeMode(true, true);
            var clusterA = await store.SaveEnrollmentAsync(Guid.NewGuid().ToString("D"), "Cluster A", clusterUrl, CancellationToken.None);
            await store.MarkEnrolledAsync(CancellationToken.None);
            await store.BeginSwitchAsync(TimeSpan.FromMinutes(5), CancellationToken.None);
            var target = await store.SaveEnrollmentAsync(Guid.NewGuid().ToString("D"), "Cluster B", clusterUrl, CancellationToken.None);
            var switching = await store.GetAsync(CancellationToken.None);
            Check(switching.IsSwitching && switching.Previous!.IsEnrolled && switching.Previous.Id == clusterA.Id && switching.Previous.NodeToken == clusterA.NodeToken,
                "During a switch the previous membership is kept intact.");
            Check(switching.Id != clusterA.Id && !switching.IsEnrolled && switching.ClusterName == "Cluster B", "The new membership starts from a fresh identity, still pending.");
            Check(!await enrollment.RollbackExpiredSwitchAsync(CancellationToken.None) && (await store.GetAsync(CancellationToken.None)).IsSwitching, "A switch still within its deadline is not rolled back.");
            leaves.Clear();
            Check(await store.RollbackSwitchAsync("annulé à la main", CancellationToken.None) && (await store.GetAsync(CancellationToken.None)).Id == clusterA.Id, "A switch can be cancelled by hand.");

            // Échéance dépassée sans approbation : retour automatique à l'ancien cluster, avec la raison.
            await store.BeginSwitchAsync(TimeSpan.FromSeconds(-1), CancellationToken.None);
            await store.SaveEnrollmentAsync(Guid.NewGuid().ToString("D"), "Cluster B", clusterUrl, CancellationToken.None);
            Check(await enrollment.RollbackExpiredSwitchAsync(CancellationToken.None), "A switch whose deadline passed is rolled back.");
            var restored = await store.GetAsync(CancellationToken.None);
            Check(restored.IsEnrolled && restored.Id == clusterA.Id && restored.ClusterName == "Cluster A" && !restored.IsSwitching, "The rollback restores the previous membership as it was.");
            Check(restored.SwitchFailure is { } failure && failure.Contains("Cluster B") && leaves.Count == 0 && mode == new NodeMode(true, true),
                "The reason is kept, the old cluster was never told to remove the node and the local server kept running.");

            // Confirmation du nouveau cluster : l'ancien est prévenu, son identité oubliée, le serveur local reste actif.
            await store.BeginSwitchAsync(TimeSpan.FromMinutes(5), CancellationToken.None);
            var arrived = await store.SaveEnrollmentAsync(Guid.NewGuid().ToString("D"), "Cluster B", "http://127.0.0.1:2/", CancellationToken.None);
            await store.MarkEnrolledAsync(CancellationToken.None);
            await enrollment.CompleteSwitchAsync(CancellationToken.None);
            var done = await store.GetAsync(CancellationToken.None);
            Check(done.IsEnrolled && done.Id == arrived.Id && !done.IsSwitching, "After confirmation the node belongs to the new cluster only.");
            Check(leaves.Single().Path == $"/api/v1/nodes/{clusterA.Id:D}/leave" && leaves.Single().Token == clusterA.NodeToken, "The old cluster is told to remove the node, with the old token.");
            Check(mode == new NodeMode(true, true), "The local server stays active once the new cluster is confirmed.");

            // Le code de vérification est le même des deux côtés et change avec l'identité.
            var code = NodeVerificationCode.FromToken(arrived.Id, arrived.NodeToken!);
            Check(code.Length == NodeVerificationCode.Length && code == NodeVerificationCode.FromTokenHash(arrived.Id, System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(arrived.NodeToken!))),
                "The node and the cluster compute the same verification code.");
            Check(code != NodeVerificationCode.FromToken(Guid.NewGuid(), arrived.NodeToken!) && code != NodeVerificationCode.FromToken(arrived.Id, "autre-jeton"), "Another identity has another code.");

            // Cluster local du conteneur : rien à arrêter.
            mode = new NodeMode(true, true);
            await enrollment.KeepLocalServerActiveAsync(CancellationToken.None);
            Check(mode == new NodeMode(true, true), "Staying in the local cluster keeps the local server.");
            Console.WriteLine("PASS: cluster membership (leave, two-phase switch, rollback, verification code, local server availability).");
        }
        finally
        {
            try { Directory.Delete(data, true); } catch (IOException) { }
        }
    }

    private static class Assert
    {
        public static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
        {
            try { await action(); }
            catch (T exception) { return exception; }
            throw new InvalidOperationException($"{typeof(T).Name} attendu.");
        }
    }
}
