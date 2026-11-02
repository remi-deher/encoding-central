using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Api.Executions;
using Encoding.Api.Flows;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Infrastructure.Data.Sqlite;
using Encoding.Api.Libraries;
using Encoding.Api.Nodes;
using Encoding.Api.Plugins;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Libraries;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static Encoding.Plugins.Tests.Support;

namespace Encoding.Plugins.Tests;

/// <summary>Webhook d’import → demande durable → découverte → exécution, avec une base SQLite isolée.</summary>
internal static class TriggerTests
{
    private const string Secret = "webhook-secret-for-tests";

    public static async Task RunAsync()
    {
        var schema = "plugins_" + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), schema);
        Directory.CreateDirectory(directory);
        try
        {
            await using var db = new SqliteDataSource($"Data Source={Path.Combine(directory, "cluster.db")};Pooling=False");
            await DatabaseMigrator.ApplyAsync(db, Path.GetFullPath("backend"), CancellationToken.None);
            await ScenarioAsync(db, directory);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task ScenarioAsync(System.Data.Common.DbDataSource db, string directory)
    {
        var registry = StoreCatalogFixture.Create();
        var validator = new FlowDefinitionValidator(registry);
        var presence = new NodePresenceOptions(TimeSpan.FromHours(1));
        var workflow = new LibraryWorkflowRepository(db, validator, presence);
        var configurations = new PluginConfigurationRepository(db, DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directory, "keys"))));
        var triggers = new IntegrationTriggerRepository(db);
        var service = new IntegrationTriggerService(triggers, workflow, NullLogger<IntegrationTriggerService>.Instance);

        var node = Guid.NewGuid();
        var library = Guid.NewGuid();
        var token = "fixture-node-token-is-long-enough-for-authentication";
        await using (var setup = db.CreateCommand("""
            INSERT INTO cluster_nodes(id,name,roles,max_concurrent_tasks,capabilities,last_seen_at,node_token_hash) VALUES(@node,'scanner',ARRAY['worker'],1,'[]'::jsonb,clock_timestamp(),@token);
            INSERT INTO libraries(id,name) VALUES(@library,'Séries');
            INSERT INTO library_node_accesses(library_id,node_id,mode,local_path,can_read,can_write) VALUES(@library,@node,'direct',@root,true,true);
            """))
        {
            setup.Parameters.AddWithValue("node", node);
            setup.Parameters.AddWithValue("library", library);
            setup.Parameters.AddWithValue("root", directory);
            setup.Parameters.AddWithValue("token", SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
            await setup.ExecuteNonQueryAsync();
        }
        var flows = new FlowRepository(db);
        var entry = Node(registry, "entry", "base-input-file");
        var end = Node(registry, "end", "base-complete-flow");
        var flow = new FlowDefinition(1, Guid.NewGuid(), 1, "Trigger fixture", "video", [entry, end], [new("a", "entry", "success", "end")]);
        Check(validator.Validate(flow).IsValid && await flows.SaveAsync(new(0, flow), CancellationToken.None) is not null, "Publish the fixture flow.");
        Check(await workflow.AssignAsync(library, new(flow.Id, 1, 0), CancellationToken.None), "Assign the flow to the library.");

        var sonarr = registry.Plugins.Single(plugin => plugin.Id == "sonarr");
        var invalidMapping = false;
        try { await configurations.SaveGlobalAsync(sonarr, new(0, new Dictionary<string, JsonElement> { ["baseUrl"] = J("http://sonarr:8989"), ["apiKey"] = J("k"), ["pathMappings"] = J("nope") }), CancellationToken.None); }
        catch (PluginConfigurationValidationException) { invalidMapping = true; }
        Check(invalidMapping, "Invalid path mappings are refused when saving the plugin configuration.");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton(configurations);
        builder.Services.AddSingleton(triggers);
        builder.Services.AddSingleton<WebhookAuthThrottle>();
        await using var app = builder.Build();
        app.MapArrWebhookEndpoints();
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        async Task<HttpResponseMessage> Post(string plugin, object body, string? secret = Secret, bool basic = false)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/integrations/{plugin}/webhook") { Content = JsonContent.Create(body) };
            if (secret is not null && basic) request.Headers.TryAddWithoutValidation("Authorization", "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("sonarr:" + secret)));
            else if (secret is not null) request.Headers.TryAddWithoutValidation("X-Encoding-Webhook-Secret", secret);
            try { return await http.SendAsync(request); }
            catch (HttpRequestException)
            {
                // Connexion réutilisée fermée par le serveur après un refus anticipé : une seule nouvelle tentative.
                using var retry = new HttpRequestMessage(HttpMethod.Post, request.RequestUri) { Content = JsonContent.Create(body) };
                foreach (var header in request.Headers) retry.Headers.TryAddWithoutValidation(header.Key, header.Value);
                return await http.SendAsync(retry);
            }
        }
        object Import(string path) => new { eventType = "Download", series = new { path = "/tv/Show" }, episodeFile = new { path } };

        Check((await Post("sonarr", new { eventType = "Test" })).StatusCode == HttpStatusCode.Conflict, "Without a configured secret the webhook is closed (409).");
        Check(await configurations.SaveGlobalAsync(sonarr, new(0, new Dictionary<string, JsonElement>
        {
            ["baseUrl"] = J("http://sonarr:8989"), ["apiKey"] = J("api"), ["webhookSecret"] = J(Secret), ["pathMappings"] = J($"{library}=/tv")
        }), CancellationToken.None) is not null, "Configure the Sonarr plugin.");

        Check((await Post("sonarr", new { eventType = "Test" }, secret: null)).StatusCode == HttpStatusCode.Unauthorized, "A missing secret is refused.");
        Check((await Post("sonarr", new { eventType = "Test" }, secret: "wrong")).StatusCode == HttpStatusCode.Unauthorized, "A wrong secret is refused.");
        Check((await Post("sonarr", new { eventType = "Test" })).StatusCode == HttpStatusCode.OK, "A Test event is acknowledged with the header secret.");
        Check((await Post("sonarr", new { eventType = "Test" }, basic: true)).StatusCode == HttpStatusCode.OK, "The secret is also accepted as the Basic password Sonarr can send.");
        Check((await Post("plex", new { eventType = "Test" })).StatusCode == HttpStatusCode.NotFound, "Only integration plugins expose a webhook.");
        Check((await Post("sonarr", new { eventType = "Grab" })).StatusCode == HttpStatusCode.Accepted, "Other events are ignored.");
        Check(await Count(db) == 0, "Rejected and ignored deliveries create no request.");
        var outside = await (await Post("sonarr", Import("/other/e1.mkv"))).Content.ReadFromJsonAsync<JsonElement>();
        Check(outside.GetProperty("unmapped").GetInt32() == 1 && await Count(db) == 0, "A file outside every mapped root creates no request.");

        var accepted = await (await Post("sonarr", Import("/tv/Show/S01/e1.mkv"))).Content.ReadFromJsonAsync<JsonElement>();
        Check(accepted.GetProperty("accepted").GetInt32() == 1 && await Count(db) == 1, "An import creates a durable request.");
        var repeated = await (await Post("sonarr", Import("/tv/Show/S01/e1.mkv"))).Content.ReadFromJsonAsync<JsonElement>();
        Check(repeated.GetProperty("duplicate").GetInt32() == 1 && await Count(db) == 1, "A repeated delivery is idempotent while the request is open.");

        // Aucune découverte n’a eu lieu depuis la demande : le service en demande une et laisse la demande en attente.
        Check(await service.ProcessAsync(CancellationToken.None) == 1, "The service claims the request.");
        Check(await State(db) == "pending" && await ScalarAsync(db, "SELECT count(*) FROM library_scans WHERE library_id=@id AND state IN('queued','running')", library) == 1, "A scan is requested and the request keeps waiting.");
        Check(await ScalarAsync(db, "SELECT count(*) FROM executions") == 0, "No execution exists before the file is discovered.");

        // Un nœud en accès direct découvre la bibliothèque (chemin réel de la découverte), sans le fichier attendu.
        await CompleteScanAsync(workflow, node, []);
        await db.CreateCommand("UPDATE integration_trigger_requests SET claimed_until=NULL").ExecuteNonQueryAsync();
        await service.ProcessAsync(CancellationToken.None);
        Check(await State(db) == "skipped", "A discovery that does not contain the file ends the request with an explicit reason.");
        Check(await ScalarAsync(db, "SELECT count(*) FROM executions") == 0, "A path never found in the library is never executed.");

        // Même import, cette fois présent dans la découverte.
        await Post("sonarr", Import("/tv/Show/S01/e1.mkv"));
        await service.ProcessAsync(CancellationToken.None);
        await CompleteScanAsync(workflow, node, [new("Show/S01/e1.mkv", 1234, DateTimeOffset.UtcNow.AddMinutes(-5))]);
        await db.CreateCommand("UPDATE integration_trigger_requests SET claimed_until=NULL").ExecuteNonQueryAsync();
        await service.ProcessAsync(CancellationToken.None);
        Check(await State(db) == "queued" && await ScalarAsync(db, "SELECT count(*) FROM executions") == 1, "A discovered file creates the execution of the assigned flow.");
        Check(await ScalarAsync(db, "SELECT count(*) FROM execution_work WHERE library_id=@id AND media_path='Show/S01/e1.mkv'", library) == 1
            && await ScalarAsync(db, "SELECT count(*) FROM library_processing_receipts WHERE library_id=@id", library) == 1, "The media is reserved and its fingerprint receipt recorded.");

        // Une livraison tardive du même fichier inchangé ne retraite pas la même empreinte.
        await Post("sonarr", Import("/tv/Show/S01/e1.mkv"));
        await service.ProcessAsync(CancellationToken.None);
        await CompleteScanAsync(workflow, node, [new("Show/S01/e1.mkv", 1234, DateTimeOffset.UtcNow.AddMinutes(-5))]);
        await db.CreateCommand("UPDATE integration_trigger_requests SET claimed_until=NULL").ExecuteNonQueryAsync();
        await service.ProcessAsync(CancellationToken.None);
        Check(await ScalarAsync(db, "SELECT count(*) FROM executions") == 1, "The same file fingerprint is not processed twice.");

        // Plusieurs serveurs : une demande n'est réclamée qu'une fois tant que la réclamation est valide.
        await Post("sonarr", Import("/tv/Show/S01/e9.mkv"));
        var first = await triggers.ClaimAsync(10, CancellationToken.None);
        var second = await triggers.ClaimAsync(10, CancellationToken.None);
        Check(first.Count == 1 && second.Count == 0, "A claimed request is not handed to a second server.");
        await db.CreateCommand("UPDATE integration_trigger_requests SET claimed_until=clock_timestamp()-interval '1 second' WHERE state='processing'").ExecuteNonQueryAsync();
        Check((await triggers.ClaimAsync(10, CancellationToken.None)).Count == 1, "An expired claim (lost server) is taken over.");

        // Expiration.
        await db.CreateCommand("UPDATE integration_trigger_requests SET requested_at=clock_timestamp()-interval '2 hours',claimed_until=NULL WHERE state='processing'").ExecuteNonQueryAsync();
        await service.ProcessAsync(CancellationToken.None);
        Check(await ScalarAsync(db, "SELECT count(*) FROM integration_trigger_requests WHERE state='expired'") == 1, "A request whose file never appears expires.");
        using (var oversized = new HttpRequestMessage(HttpMethod.Post, "/api/v1/integrations/sonarr/webhook") { Content = new StringContent("{\"eventType\":\"Test\",\"pad\":\"" + new string('x', 1024 * 1024 + 16) + "\"}", System.Text.Encoding.UTF8, "application/json") })
        {
            oversized.Headers.TryAddWithoutValidation("X-Encoding-Webhook-Secret", Secret);
            // Le serveur peut fermer la connexion pendant l'envoi : refus par 413 ou connexion coupée, jamais un traitement.
            var refused = false;
            try { refused = (await http.SendAsync(oversized)).StatusCode == HttpStatusCode.RequestEntityTooLarge; }
            catch (HttpRequestException) { refused = true; }
            Check(refused, "A body above 1 MiB is refused before being parsed.");
        }
        for (var i = 0; i < WebhookAuthThrottle.MaxFailures; i++) await Post("sonarr", new { eventType = "Test" }, secret: "wrong-" + i);
        var throttled = await Post("sonarr", new { eventType = "Test" });
        Check(throttled.StatusCode == (HttpStatusCode)429 && throttled.Headers.Contains("Retry-After"), "Repeated authentication failures are throttled, even for the right secret.");
        Console.WriteLine("PASS: webhook authentication, idempotence, scan-gated execution creation, receipts, multi-server claims and expiry.");
    }

    private static async Task CompleteScanAsync(LibraryWorkflowRepository workflow, Guid node, DiscoveredLibraryFile[] files)
    {
        var lease = await workflow.ClaimScanAsync(node, CancellationToken.None) ?? throw new InvalidOperationException("A scan must be claimable by the direct-access node.");
        Check(await workflow.CompleteScanAsync(lease.Id, node, new(lease.Epoch, files, null), CancellationToken.None), "The node completes its discovery.");
    }

    private static FlowNodeDefinition Node(FlowComponentRegistry registry, string id, string componentId)
    {
        var plugin = registry.Plugins.Single(plugin => plugin.Components.Any(component => component.Id == componentId));
        var component = plugin.Components.Single(component => component.Id == componentId);
        var configuration = component.ConfigurationSchema.Fields.Where(field => field.DefaultValue is not null).ToDictionary(field => field.Id, field => field.DefaultValue!.Value);
        return new(id, null, new(plugin.Id, plugin.Version, component.Id, component.Version, component.ConfigurationSchema.Version), configuration);
    }

    private static async Task<long> Count(System.Data.Common.DbDataSource db) => await ScalarAsync(db, "SELECT count(*) FROM integration_trigger_requests");

    private static async Task<string> State(System.Data.Common.DbDataSource db)
    {
        await using var command = db.CreateCommand("SELECT state FROM integration_trigger_requests ORDER BY requested_at DESC LIMIT 1");
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> ScalarAsync(System.Data.Common.DbDataSource db, string sql, Guid? id = null)
    {
        await using var command = db.CreateCommand(sql);
        if (id is not null) command.Parameters.AddWithValue("id", id.Value);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
