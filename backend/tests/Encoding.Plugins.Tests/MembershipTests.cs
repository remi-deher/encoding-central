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

/// <summary>Rattachement d'un worker à un central : départ, changement en deux phases et cluster injoignable.</summary>
internal static class MembershipTests
{
    public static async Task RunAsync()
    {
        var data = Path.Combine(Path.GetTempPath(), "encoding-membership-" + Guid.NewGuid().ToString("N"));
        var leaveStatus = HttpStatusCode.NoContent;
        var leaves = new List<(string Path, string Token)>();

        var clusterBuilder = WebApplication.CreateBuilder();
        clusterBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var cluster = clusterBuilder.Build();
        cluster.MapPost("/api/v1/workers/{id}/leave", (string id, HttpRequest request) =>
        {
            leaves.Add((request.Path.Value!, request.Headers["X-Node-Token"].ToString()));
            return Results.StatusCode((int)leaveStatus);
        });
        await cluster.StartAsync();
        var clusterUrl = cluster.Urls.Single().TrimEnd('/') + "/";

        try
        {
            Directory.CreateDirectory(data);
            var secretFile = Path.Combine(data, "secret");
            File.WriteAllText(secretFile, "secret");
            var localUrl = "http://127.0.0.1:18080/";
            var options = new WorkerAgentOptions("membership", [Encoding.Contracts.Nodes.NodeRole.Server, Encoding.Contracts.Nodes.NodeRole.Worker], 1, data,
                localUrl, false, TimeSpan.FromSeconds(10), 8081, secretFile);
            var store = new WorkerIdentityStore(options);
            var enrollment = new WorkerEnrollmentService(options, store, new WorkerCentralDiscovery(options, NullLogger<WorkerCentralDiscovery>.Instance),
                null!, NullLogger<WorkerEnrollmentService>.Instance, Microsoft.Extensions.Options.Options.Create(new Microsoft.AspNetCore.Http.Json.JsonOptions()));

            // Rattaché au central distant : le départ le prévient avec son jeton, efface l'identité et efface l'identité locale.
            var joined = await store.SaveEnrollmentAsync(Guid.NewGuid().ToString("D"), "Cluster B", clusterUrl, CancellationToken.None);
            await store.MarkEnrolledAsync(CancellationToken.None);
            leaveStatus = HttpStatusCode.Conflict;
            var busy = await Assert.ThrowsAsync<InvalidOperationException>(() => enrollment.LeaveAsync(CancellationToken.None));
            var stillJoined = await store.GetAsync(CancellationToken.None);
            Check(stillJoined.IsEnrolled && stillJoined.Id == joined.Id, "A node busy with a task cannot leave: its identity is unchanged.");
            Check(busy.Message.Contains("traite encore"), "The refusal explains that the node is still processing.");

            leaveStatus = HttpStatusCode.NoContent;
            await enrollment.LeaveAsync(CancellationToken.None);
            var left = await store.GetAsync(CancellationToken.None);
            Check(leaves.Last().Path == $"/api/v1/workers/{joined.Id:D}/leave" && leaves.Last().Token == joined.NodeToken, "The node tells its cluster, authenticated by its own token.");
            Check(!left.HasCredentials && left.Id != joined.Id, "After leaving, the node restarts from a fresh identity (a removed identity never comes back).");

            // Cluster distant injoignable : le départ reste possible, localement.
            var orphan = await store.SaveEnrollmentAsync(Guid.NewGuid().ToString("D"), "Central perdu", "http://127.0.0.1:1/", CancellationToken.None);
            await enrollment.LeaveAsync(CancellationToken.None);
            Check(!(await store.GetAsync(CancellationToken.None)).HasCredentials, "An unreachable cluster does not trap the node.");
            Check(orphan.NodeToken is not null, "Sanity: the abandoned enrollment had credentials.");

            // Un worker n'a qu'un central : après avoir quitté, il peut en rejoindre un autre avec une identité neuve.
            var arrived = await store.SaveEnrollmentAsync(Guid.NewGuid().ToString("D"), "Central B", "http://127.0.0.1:2/", CancellationToken.None);
            await store.MarkEnrolledAsync(CancellationToken.None);
            var done = await store.GetAsync(CancellationToken.None);
            Check(done.IsEnrolled && done.Id == arrived.Id, "After enrolling, the node belongs to its central.");

            // Le code de vérification est le même des deux côtés et change avec l'identité.
            var code = WorkerVerificationCode.FromToken(arrived.Id, arrived.NodeToken!);
            Check(code.Length == WorkerVerificationCode.Length && code == WorkerVerificationCode.FromTokenHash(arrived.Id, System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(arrived.NodeToken!))),
                "The node and the cluster compute the same verification code.");
            Check(code != WorkerVerificationCode.FromToken(Guid.NewGuid(), arrived.NodeToken!) && code != WorkerVerificationCode.FromToken(arrived.Id, "autre-jeton"), "Another identity has another code.");

            Console.WriteLine("PASS: central membership (leave, verification code).");
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
