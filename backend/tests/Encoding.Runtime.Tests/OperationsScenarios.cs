using Encoding.Api.Infrastructure.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Encoding.Api.Executions;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Operations;
using Encoding.Api.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Encoding.Runtime.Tests;

/// <summary>Outillage d’exploitation : migrations versionnées, métriques, health checks et rétention des artefacts.</summary>
internal static class OperationsScenarios
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private const string First = "0033_baseline";

    public static void Run()
    {
        var repositoryMigrations = Path.Combine(Harness.RepositoryRoot(), "migrations", "sqlite");
        var catalog = MigrationCatalog.Load(repositoryMigrations);
        Check(catalog.Count >= 4 && catalog.Select(file => file.Number).Distinct().Count() == catalog.Count, "Chaque numéro de migration SQLite du dépôt est unique.");
        Check(catalog.Any(file => file.Version == "0033_baseline"), "Le catalogue SQLite commence par son schéma de référence.");

        var scratch = Directory.CreateTempSubdirectory("encoding-migrations-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(scratch, "0001_a.sql"), "SELECT 1;");
            File.WriteAllText(Path.Combine(scratch, "0001_b.sql"), "SELECT 1;");
            Check(Throws(() => MigrationCatalog.Load(scratch), "0001"), "Deux migrations de même numéro sont refusées.");
            File.Delete(Path.Combine(scratch, "0001_b.sql"));
            File.WriteAllText(Path.Combine(scratch, "2_bad.sql"), "SELECT 1;");
            Check(Throws(() => MigrationCatalog.Load(scratch), "2_bad.sql"), "Un nom de migration invalide est refusé.");
        }
        finally { Directory.Delete(scratch, true); }
        Check(MigrationCatalog.Checksum("a\r\nb") == MigrationCatalog.Checksum("a\nb"), "L’empreinte ignore les fins de ligne.");

        var writer = new PrometheusWriter().Gauge("x_total", "Aide\nligne", 1.5, ("l", "a\"b\\c\nd")).Gauge("x_total", "Aide", 2, ("l", "z"));
        var text = writer.ToString();
        Check(text.Split('\n').Count(line => line.StartsWith("# TYPE x_total", StringComparison.Ordinal)) == 1, "Le type d’une métrique n’est déclaré qu’une fois.");
        Check(text.Contains("x_total{l=\"a\\\"b\\\\c\\nd\"} 1.5\n", StringComparison.Ordinal) && text.Contains("x_total{l=\"z\"} 2\n", StringComparison.Ordinal), "Les valeurs d’étiquettes sont échappées.");

        Check(OperationsEndpoints.IsValidCorrelationId("abc-123_x.y") && !OperationsEndpoints.IsValidCorrelationId("a b") && !OperationsEndpoints.IsValidCorrelationId(new string('a', 65)) && !OperationsEndpoints.IsValidCorrelationId(""), "Les identifiants de corrélation sont bornés et sûrs pour les journaux.");
        var context = new DefaultHttpContext();
        Check(OperationsEndpoints.Authorized(context, null), "Sans jeton configuré, /metrics est ouvert.");
        Check(!OperationsEndpoints.Authorized(context, "secret"), "Un jeton configuré est exigé.");
        context.Request.Headers.Authorization = "Bearer secret";
        Check(OperationsEndpoints.Authorized(context, "secret"), "Le bon jeton est accepté.");
        context.Request.Headers.Authorization = "Bearer secreT";
        Check(!OperationsEndpoints.Authorized(context, "secret"), "Un mauvais jeton est refusé.");

        Check(Throws(() => new ArtifactRetentionOptions { Enabled = true, Days = -1 }.Validate(), "Days"), "La rétention exige une durée positive.");
        new ArtifactRetentionOptions().Validate();
        Console.WriteLine("PASS: migration catalog, Prometheus format, correlation ids and operations options.");
    }

    public static async Task RunDatabaseAsync(string connection)
    {
        await using var database = await TestDatabase.CreateAsync(connection, "ops");
        var directory = Directory.CreateTempSubdirectory("encoding-ops-").FullName;
        var contentRoot = Harness.RepositoryRoot();
        try
        {
            var db = database.Db;

            var before = await DatabaseMigrator.StatusAsync(db, contentRoot, CancellationToken.None);
            Check(before.Applied == 0 && before.Pending.Count > 0, "Une base vide a toutes les migrations en attente.");
            await DatabaseMigrator.ApplyAsync(db, contentRoot, CancellationToken.None);
            await DatabaseMigrator.ApplyAsync(db, contentRoot, CancellationToken.None);
            var status = await DatabaseMigrator.StatusAsync(db, contentRoot, CancellationToken.None);
            Check(status.Pending.Count == 0 && status.Unknown.Count == 0, "Les migrations appliquées deux fois restent cohérentes.");
            Check(await Scalar(db, "SELECT count(*) FROM schema_migrations WHERE checksum IS NULL") == 0, "Chaque migration enregistre son empreinte.");

            await Exec(db, "INSERT INTO schema_migrations(version, checksum) VALUES ('9999_from_a_newer_node', 'x')");
            Check((await DatabaseMigrator.StatusAsync(db, contentRoot, CancellationToken.None)).Unknown.SequenceEqual(["9999_from_a_newer_node"]), "Une migration d’un nœud plus récent est signalée sans bloquer.");
            await Exec(db, "DELETE FROM schema_migrations WHERE version = '9999_from_a_newer_node'");
            await Exec(db, $"UPDATE schema_migrations SET checksum = 'tampered' WHERE version = '{First}'");
            var rejected = false;
            try { await DatabaseMigrator.ApplyAsync(db, contentRoot, CancellationToken.None); } catch (InvalidOperationException exception) when (exception.Message.Contains(First, StringComparison.Ordinal)) { rejected = true; }
            Check(rejected, "Une migration publiée modifiée bloque le démarrage.");
            await Exec(db, $"UPDATE schema_migrations SET checksum = NULL WHERE version = '{First}'");
            await DatabaseMigrator.ApplyAsync(db, contentRoot, CancellationToken.None);
            Check(await Scalar(db, "SELECT count(*) FROM schema_migrations WHERE checksum IS NULL AND version <> '0006_library_node_access_activation'") == 0, "Les empreintes absentes sont complétées sans échec.");

            await RetentionAsync(db, directory);
            await EndpointsAsync(db, directory, contentRoot);
            Console.WriteLine("PASS: versioned SQLite migrations, artifact retention, health checks and /metrics.");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task RetentionAsync(System.Data.Common.DbDataSource db, string directory)
    {
        var root = Path.Combine(directory, "artifacts");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Runtime:ArtifactDirectory"] = root }).Build();
        var store = new RuntimeArtifactStore(db, config);
        var options = new OperationsOptions { ArtifactRetention = new() { Enabled = true, Days = 30, FailedDays = 90, TempFileHours = 24 } };
        var retention = new ArtifactRetention(db, store, options, NullLogger<ArtifactRetention>.Instance);

        var library = Guid.NewGuid();
        await Exec(db, $"INSERT INTO libraries(id, name) VALUES ('{library}', 'ops')");
        var old = await Execution(db, store, library, "succeeded", 60, "published");
        var recent = await Execution(db, store, library, "succeeded", 5, "published");
        var failed = await Execution(db, store, library, "failed", 60, null);
        var unpublished = await Execution(db, store, library, "succeeded", 60, null, publicationRequested: true);
        var reserved = await Execution(db, store, library, "cancelled", 60, null);
        await Exec(db, $"INSERT INTO media_reservations(library_id, media_key, execution_id, lease_epoch, lease_until) VALUES ('{library}', 'k-reserved', '{reserved}', 1, now() - interval '1 hour')");
        var staleTemporary = Path.Combine(store.DirectoryFor(recent), "x.tmp");
        File.WriteAllText(staleTemporary, "partial"); File.SetLastWriteTimeUtc(staleTemporary, DateTime.UtcNow.AddDays(-3));
        var freshTemporary = Path.Combine(store.DirectoryFor(recent), "y.tmp");
        File.WriteAllText(freshTemporary, "partial");

        var dry = await new ArtifactRetention(db, store, new() { ArtifactRetention = new() { Enabled = true, Days = 30, FailedDays = 90, DryRun = true } }, NullLogger<ArtifactRetention>.Instance).RunOnceAsync(CancellationToken.None);
        Check(dry.DryRun && dry.Purged == 1 && dry.BytesFreed == 4 && Directory.Exists(store.DirectoryFor(old)) && File.Exists(staleTemporary), "La simulation compte sans supprimer.");

        var result = await retention.RunOnceAsync(CancellationToken.None);
        Check(result.Purged == 2 - 1 && result.BytesFreed == 4 && result.TempFilesRemoved == 1, "Seule l’exécution réussie ancienne est purgée.");
        Check(!Directory.Exists(store.DirectoryFor(old)), "Les artefacts anciens sont supprimés.");
        Check(Directory.Exists(store.DirectoryFor(recent)) && Directory.Exists(store.DirectoryFor(failed)) && Directory.Exists(store.DirectoryFor(unpublished)) && Directory.Exists(store.DirectoryFor(reserved)),
            "Une exécution récente, échouée, en attente de publication ou réservée est conservée.");
        Check(!File.Exists(staleTemporary) && File.Exists(freshTemporary), "Seuls les fichiers temporaires périmés sont supprimés.");
        Check(await Scalar(db, $"SELECT count(*) FROM execution_artifacts WHERE execution_id = '{old}'") == 1, "L’historique des artefacts reste en base.");
        var again = await retention.RunOnceAsync(CancellationToken.None);
        Check(again.Purged == 0, "La rétention est idempotente.");

        var withFailed = new ArtifactRetention(db, store, new() { ArtifactRetention = new() { Enabled = true, Days = 30, FailedDays = 30 } }, NullLogger<ArtifactRetention>.Instance);
        Check((await withFailed.RunOnceAsync(CancellationToken.None)).Purged == 1 && !Directory.Exists(store.DirectoryFor(failed)), "FailedDays purge aussi les échecs au-delà de la durée.");
    }

    private static async Task EndpointsAsync(System.Data.Common.DbDataSource db, string directory, string contentRoot)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = contentRoot });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Runtime:ArtifactDirectory"] = Path.Combine(directory, "endpoint-artifacts"),
            ["FlowBackups:Directory"] = Path.Combine(directory, "endpoint-backups"),
            ["Operations:Metrics:Token"] = "scrape-token",
            ["Operations:Health:MinFreeDiskBytes"] = "1",
        });
        builder.Services.AddSingleton<System.Data.Common.DbDataSource>(db);
        builder.Services.AddSingleton(new NodePresenceOptions(TimeSpan.FromSeconds(30)));
        builder.Services.AddSingleton<RuntimeArtifactStore>();
        builder.AddOperations();
        await using var app = builder.Build();
        app.UseRequestObservability();
        app.MapOperationsEndpoints();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()) };
            Check((await client.GetAsync("/health/live")).StatusCode == HttpStatusCode.OK, "/health/live répond sans dépendance.");
            var ready = await client.GetAsync("/health/ready");
            Check(ready.StatusCode == HttpStatusCode.OK && (await ready.Content.ReadAsStringAsync()).Contains("\"ready\"", StringComparison.Ordinal), "/health/ready conserve son contrat.");
            Check(ready.Headers.Contains(OperationsEndpoints.CorrelationHeader), "Chaque réponse porte un identifiant de corrélation.");

            using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
            request.Headers.Add(OperationsEndpoints.CorrelationHeader, "client-42");
            Check((await client.SendAsync(request)).Headers.GetValues(OperationsEndpoints.CorrelationHeader).Single() == "client-42", "L’identifiant du client est conservé.");

            using var details = JsonDocument.Parse(await client.GetStringAsync("/health/details"));
            var checks = details.RootElement.GetProperty("checks").EnumerateArray().ToDictionary(item => item.GetProperty("name").GetString()!, item => item.GetProperty("status").GetString());
            Check(new[] { "database", "migrations", "cluster", "cluster-reconciliation", "flow-backups", "artifact-storage", "backup-storage" }.All(checks.ContainsKey), "/health/details liste chaque contrôle.");
            Check(checks["cluster-reconciliation"] == "degraded", "Une réconciliation non démarrée est visible dans la santé détaillée sans bloquer la disponibilité locale.");
            Check(checks["database"] == "healthy" && checks["migrations"] == "healthy" && checks["artifact-storage"] == "healthy", "Base, schéma et volumes sont sains.");
            Check(checks["cluster"] == "degraded", "Un cluster sans worker en ligne est dégradé, pas en panne.");
            Check(!(await client.GetStringAsync("/health/details")).Contains(directory, StringComparison.Ordinal), "Les chemins du serveur ne sont pas exposés.");

            Check((await client.GetAsync("/metrics")).StatusCode == HttpStatusCode.Unauthorized, "/metrics exige le jeton configuré.");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "scrape-token");
            var metrics = await client.GetStringAsync("/metrics");
            foreach (var expected in new[] { "encoding_up 1", "encoding_database_up 1", "encoding_nodes{status=\"online\"} 0", "encoding_executions{state=\"queued\"}", "encoding_schema_migrations_pending 0",
                "encoding_flow_backup_jobs{status=\"failed\"}", "encoding_artifact_retention_runs_total", "encoding_http_requests_total{method=\"GET\",route=\"/health/live\",status=\"200\"}", "process_resident_memory_bytes" })
                Check(metrics.Contains(expected, StringComparison.Ordinal), $"/metrics expose {expected}.");
        }
        finally { await app.StopAsync(); }
    }

    private static async Task<Guid> Execution(System.Data.Common.DbDataSource db, RuntimeArtifactStore store, Guid library, string state, int ageDays, string? published, bool publicationRequested = false)
    {
        var id = Guid.NewGuid();
        await Exec(db, $$"""
            INSERT INTO executions(id, media, library_name, flow_name, flow_version, flow_definition, state, attempt, finished_at)
            VALUES ('{{id}}', 'm.mkv', 'ops', 'flow', 1, '{}', '{{state}}', 1, now() - interval '{{ageDays}} days')
            """);
        await Exec(db, $"INSERT INTO execution_work(execution_id, library_id, media_path, media_key, publication_requested, published_path) VALUES ('{id}', '{library}', 'm.mkv', 'k-{id:N}', {(publicationRequested ? "true" : "false")}, {(published is null ? "NULL" : $"'{published}'")})");
        await Exec(db, $"INSERT INTO execution_artifacts(execution_id, sha256, byte_length) VALUES ('{id}', '{new string('a', 64)}', 4)");
        Directory.CreateDirectory(store.DirectoryFor(id));
        File.WriteAllText(Path.Combine(store.DirectoryFor(id), new string('a', 64)), "data");
        return id;
    }

    private static async Task Exec(System.Data.Common.DbDataSource db, string sql) { await using var command = db.CreateCommand(sql); await command.ExecuteNonQueryAsync(); }
    private static async Task<long> Scalar(System.Data.Common.DbDataSource db, string sql) { await using var command = db.CreateCommand(sql); return Convert.ToInt64(await command.ExecuteScalarAsync()); }
    private static bool Throws(Action action, string messagePart)
    {
        try { action(); return false; }
        catch (InvalidOperationException exception) { return exception.Message.Contains(messagePart, StringComparison.Ordinal); }
    }
}
