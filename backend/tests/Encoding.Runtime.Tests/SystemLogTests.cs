using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Encoding.Api.Executions;
using Encoding.Api.Flows;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Libraries;
using Encoding.Api.Logging;
using Encoding.Api.Mcp;
using Encoding.Api.Nodes;
using Encoding.Api.Plugins;
using Encoding.Api.Resources;
using Encoding.Contracts.Logging;
using Encoding.Contracts.Tools;
using Encoding.Core.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class SystemLogSanitizerTests
{
    [Fact]
    public void Redacts_Bearer_Tokens()
    {
        var input = "Request headers: Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.xyz and more";
        var sanitized = SystemLogSanitizer.Sanitize(input);
        Assert.Contains("Bearer ***REDACTED***", sanitized);
        Assert.DoesNotContain("eyJhbGci", sanitized);
    }

    [Fact]
    public void Redacts_Personal_Access_Tokens()
    {
        var input = "Authenticating with personal access token enc_pat_3a8b4c2d1e0f987654321fedcba";
        var sanitized = SystemLogSanitizer.Sanitize(input);
        Assert.Contains("***REDACTED***", sanitized);
        Assert.DoesNotContain("3a8b4c2d1e0f987654321fedcba", sanitized);
    }

    [Fact]
    public void Redacts_Node_And_Container_Secrets()
    {
        var input = "Node paired with secret enc_sec_4f9a0c2e4b6d8a0f and node token enc_node_9988776655443322";
        var sanitized = SystemLogSanitizer.Sanitize(input);
        Assert.DoesNotContain("enc_sec_", sanitized);
        Assert.DoesNotContain("enc_node_", sanitized);
        Assert.Contains("***REDACTED***", sanitized);
    }

    [Fact]
    public void Redacts_Passwords_And_Api_Keys_In_KeyValue_Formats()
    {
        var inputs = new[]
        {
            "Config: password=\"MySecretPassword!123\"",
            "Plex connection: x-plex-token='plex_token_abc_xyz'",
            "URL: https://sonarr.local/api/v3?api_key=sonarr_secret_api_key_456&mode=full",
            "Payload: {\"secret\": \"super_top_secret_val\"}",
            "Auth: token=session_token_7788"
        };

        foreach (var text in inputs)
        {
            var sanitized = SystemLogSanitizer.Sanitize(text);
            Assert.Contains("***REDACTED***", sanitized);
            Assert.DoesNotContain("MySecretPassword", sanitized);
            Assert.DoesNotContain("plex_token_abc_xyz", sanitized);
            Assert.DoesNotContain("sonarr_secret_api_key_456", sanitized);
            Assert.DoesNotContain("super_top_secret_val", sanitized);
            Assert.DoesNotContain("session_token_7788", sanitized);
        }
    }

    [Fact]
    public void Redacts_Private_Keys()
    {
        var input = """
            -----BEGIN RSA PRIVATE KEY-----
            MIIEowIBAAKCAQEA0Y8v...
            some private key contents
            -----END RSA PRIVATE KEY-----
            """;
        var sanitized = SystemLogSanitizer.Sanitize(input);
        Assert.Contains("***REDACTED PRIVATE KEY***", sanitized);
        Assert.DoesNotContain("MIIEowIBAAKCAQEA0Y8v", sanitized);
    }

    [Fact]
    public void Redacts_Exceptions()
    {
        var ex = new InvalidOperationException("Failed with token enc_pat_998877aabbcc and password=admin",
            new ArgumentException("Sub failure with secret='inner_secret_val'"));

        var sanitized = SystemLogSanitizer.SanitizeException(ex);
        Assert.NotNull(sanitized);
        Assert.Contains("***REDACTED***", sanitized);
        Assert.DoesNotContain("enc_pat_998877aabbcc", sanitized);
        Assert.DoesNotContain("inner_secret_val", sanitized);
    }
}

public sealed class SystemLogFilterPolicyTests
{
    [Fact]
    public void Drops_Routine_Kestrel_200_Logs()
    {
        var category = "Microsoft.AspNetCore.Hosting.Diagnostics";
        var message = "Request finished in 1.45ms 200 application/json";

        var captured = SystemLogFilterPolicy.ShouldCapture(category, LogLevel.Information, message, allowRoutineDiagnostics: false);
        Assert.False(captured);
    }

    [Fact]
    public void Retains_Routine_Kestrel_Logs_When_Allowed()
    {
        var category = "Microsoft.AspNetCore.Hosting.Diagnostics";
        var message = "Request finished in 1.45ms 200 application/json";

        var captured = SystemLogFilterPolicy.ShouldCapture(category, LogLevel.Information, message, allowRoutineDiagnostics: true);
        Assert.True(captured);
    }

    [Fact]
    public void Drops_HealthCheck_Routine_Logs()
    {
        var category = "Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckMiddleware";
        var message = "Health check Ping completed after 0.2ms with status Healthy";

        var captured = SystemLogFilterPolicy.ShouldCapture(category, LogLevel.Information, message);
        Assert.False(captured);
    }

    [Fact]
    public void Drops_Mesh_Trace_And_Debug_Logs()
    {
        var category = "Encoding.ClusterMesh.Transport";
        var message = "Heartbeat packet exchanged with peer";

        Assert.False(SystemLogFilterPolicy.ShouldCapture(category, LogLevel.Debug, message));
        Assert.False(SystemLogFilterPolicy.ShouldCapture(category, LogLevel.Trace, message));
        Assert.True(SystemLogFilterPolicy.ShouldCapture(category, LogLevel.Information, message));
    }

    [Fact]
    public void Never_Drops_Warnings_Errors_Or_Criticals()
    {
        var kestrelCategory = "Microsoft.AspNetCore.Hosting.Diagnostics";
        var healthCategory = "Microsoft.AspNetCore.Diagnostics.HealthChecks";
        var meshCategory = "Encoding.ClusterMesh";

        Assert.True(SystemLogFilterPolicy.ShouldCapture(kestrelCategory, LogLevel.Warning, "Connection reset"));
        Assert.True(SystemLogFilterPolicy.ShouldCapture(healthCategory, LogLevel.Error, "Health check failed: Disk full"));
        Assert.True(SystemLogFilterPolicy.ShouldCapture(meshCategory, LogLevel.Critical, "Mesh exchange failed"));
    }
}

public sealed class SystemLogRingBufferTests
{
    [Fact]
    public void RingBuffer_Keeps_Most_Recent_Entries_When_Overflowing()
    {
        var buffer = new SystemLogRingBuffer(capacity: 5);

        for (var i = 1; i <= 10; i++)
        {
            buffer.Add(new SystemLogEntry(
                Id: i.ToString(),
                Timestamp: DateTimeOffset.UtcNow.AddSeconds(i),
                Level: SystemLogLevel.Information,
                Category: "App.Component",
                Message: $"Message {i}"));
        }

        Assert.Equal(5, buffer.Count);
        Assert.Equal(10, buffer.TotalWritten);

        var query = buffer.Query(new SystemLogFilterQuery { Limit = 10 });
        Assert.Equal(5, query.Count);

        // Ordre antichronologique : du plus récent au plus ancien (10, 9, 8, 7, 6)
        Assert.Equal("10", query[0].Id);
        Assert.Equal("9", query[1].Id);
        Assert.Equal("8", query[2].Id);
        Assert.Equal("7", query[3].Id);
        Assert.Equal("6", query[4].Id);
    }

    [Fact]
    public void RingBuffer_Filters_By_MinLevel_Category_Search_And_Node()
    {
        var buffer = new SystemLogRingBuffer(capacity: 100);
        var node1 = Guid.NewGuid();
        var node2 = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        buffer.Add(new SystemLogEntry("1", now.AddMinutes(1), SystemLogLevel.Information, "Encoding.Probe", "Probe GPU started", NodeId: node1));
        buffer.Add(new SystemLogEntry("2", now.AddMinutes(2), SystemLogLevel.Warning, "Encoding.Probe", "GPU NVENC throttled", NodeId: node1));
        buffer.Add(new SystemLogEntry("3", now.AddMinutes(3), SystemLogLevel.Error, "Encoding.Plugins", "Plugin Python crashed", NodeId: node2));
        buffer.Add(new SystemLogEntry("4", now.AddMinutes(4), SystemLogLevel.Information, "Encoding.Plugins", "Plugin loaded successfully", NodeId: node2));

        // Filter by MinLevel Warning
        var warnEntries = buffer.Query(new SystemLogFilterQuery { MinLevel = SystemLogLevel.Warning });
        Assert.Equal(2, warnEntries.Count);
        Assert.Contains(warnEntries, e => e.Id == "2");
        Assert.Contains(warnEntries, e => e.Id == "3");

        // Filter by Category
        var probeEntries = buffer.Query(new SystemLogFilterQuery { Category = "Encoding.Probe" });
        Assert.Equal(2, probeEntries.Count);
        Assert.All(probeEntries, e => Assert.Equal("Encoding.Probe", e.Category));

        // Filter by Search term
        var searchEntries = buffer.Query(new SystemLogFilterQuery { Search = "crashed" });
        Assert.Single(searchEntries);
        Assert.Equal("3", searchEntries[0].Id);

        // Filter by Node
        var node1Entries = buffer.Query(new SystemLogFilterQuery { NodeId = node1 });
        Assert.Equal(2, node1Entries.Count);
        Assert.All(node1Entries, e => Assert.Equal(node1, e.NodeId));

        // Categories
        var categories = buffer.GetCategories();
        Assert.Equal(2, categories.Count);
        Assert.Contains("Encoding.Plugins", categories);
        Assert.Contains("Encoding.Probe", categories);
    }

    [Fact]
    public void RingBuffer_Handles_Concurrent_Writes_Safely()
    {
        var buffer = new SystemLogRingBuffer(capacity: 200);

        Parallel.For(0, 1000, i =>
        {
            buffer.Add(new SystemLogEntry(
                Id: i.ToString(),
                Timestamp: DateTimeOffset.UtcNow,
                Level: SystemLogLevel.Information,
                Category: "App.Concurrency",
                Message: $"Concurrent message {i}"));
        });

        Assert.Equal(200, buffer.Count);
        Assert.Equal(1000, buffer.TotalWritten);
        var results = buffer.Query(new SystemLogFilterQuery { Limit = 50 });
        Assert.Equal(50, results.Count);
    }
}

public sealed class SystemLogProviderTests
{
    [Fact]
    public void SystemLogger_Captures_And_Sanitizes_Through_ILogger()
    {
        var ringBuffer = new SystemLogRingBuffer(capacity: 100);
        var broadcaster = new SystemLogStreamBroadcaster();
        var nodeId = Guid.NewGuid();
        var nodeContext = new DefaultSystemLogNodeContext(nodeId, "Worker-01");

        using var provider = new SystemLogProvider(ringBuffer, broadcaster, nodeContext);
        var logger = provider.CreateLogger("Encoding.NodeAgent.Services.ToolCapabilityProbe");

        logger.LogInformation("Sonde GPU terminée avec token enc_pat_1122334455667788");

        var entries = ringBuffer.Query(new SystemLogFilterQuery { Limit = 10 });
        Assert.Single(entries);
        var entry = entries[0];
        Assert.Equal("Encoding.NodeAgent.Services.ToolCapabilityProbe", entry.Category);
        Assert.Equal(SystemLogLevel.Information, entry.Level);
        Assert.Contains("***REDACTED***", entry.Message);
        Assert.DoesNotContain("enc_pat_1122334455667788", entry.Message);
        Assert.Equal(nodeId, entry.NodeId);
        Assert.Equal("Worker-01", entry.NodeName);
    }
}

public sealed class SystemLogRepositoryTests
{
    [Fact]
    public async Task Inserts_Queries_And_Prunes_Logs()
    {
        await using var db = await TestDatabase.CreateSqliteAsync("system-logs-repo");
        var contentRoot = Harness.RepositoryRoot();
        await DatabaseMigrator.ApplyAsync(db.Db, contentRoot, CancellationToken.None);

        var repo = new SystemLogRepository(db.Db);

        var id1 = Guid.NewGuid().ToString("N");
        var id2 = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;

        await repo.InsertAsync(new SystemLogEntry(id1, now.AddMinutes(-10), SystemLogLevel.Warning, "Encoding.Tools", "Tool Warning"));
        await repo.InsertAsync(new SystemLogEntry(id2, now, SystemLogLevel.Error, "Encoding.Plugins", "Plugin Error", Exception: "System.Exception: fail"));

        var all = await repo.QueryAsync(new SystemLogFilterQuery());
        Assert.Equal(2, all.Count);
        Assert.Equal(id2, all[0].Id); // Newest first

        var errorOnly = await repo.QueryAsync(new SystemLogFilterQuery { MinLevel = SystemLogLevel.Error });
        Assert.Single(errorOnly);
        Assert.Equal(id2, errorOnly[0].Id);

        var categories = await repo.GetCategoriesAsync();
        Assert.Equal(2, categories.Count);
        Assert.Contains("Encoding.Plugins", categories);
        Assert.Contains("Encoding.Tools", categories);

        // Test Pruning
        await repo.PruneAsync(maxRows: 1);
        var pruned = await repo.QueryAsync(new SystemLogFilterQuery());
        Assert.Single(pruned);
        Assert.Equal(id2, pruned[0].Id); // Keeps newest
    }
}

public sealed class SystemLogMcpTests
{
    [Fact]
    public async Task McpService_Exposes_And_Executes_GetSystemLogs()
    {
        await using var db = await TestDatabase.CreateSqliteAsync("mcp-system-logs");
        var contentRoot = Harness.RepositoryRoot();
        await DatabaseMigrator.ApplyAsync(db.Db, contentRoot, CancellationToken.None);

        var ringBuffer = new SystemLogRingBuffer();
        var repo = new SystemLogRepository(db.Db);

        ringBuffer.Add(new SystemLogEntry(
            Id: "log-mcp-1",
            Timestamp: DateTimeOffset.UtcNow,
            Level: SystemLogLevel.Warning,
            Category: "Encoding.Tools.FFmpeg",
            Message: "Hardware acceleration dropped back to CPU"));

        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(config);
        services.AddDataProtection();
        services.AddHttpClient();
        services.AddSingleton(db.Db);
        services.AddSingleton<LibraryRepository>();
        services.AddSingleton<LibraryWorkflowRepository>();
        services.AddSingleton<LibraryNodeAccessRepository>();
        services.AddSingleton<LibraryProcessingRepository>();
        services.AddSingleton<FlowRepository>();
        services.AddSingleton<FlowComponentRegistry>();
        services.AddSingleton<FlowDefinitionValidator>();
        services.AddSingleton<NodeRepository>();
        services.AddSingleton<NodeGroupRepository>();
        services.AddSingleton(new NodePresenceOptions(TimeSpan.FromSeconds(30)));
        services.AddSingleton<ExecutionRepository>();
        services.AddSingleton<FlowScriptRepository>();
        services.AddSingleton(new ResourceBlobStore(config));
        services.AddSingleton<ResourceRepository>();
        services.AddSingleton<EncodingProfileRepository>();
        services.AddSingleton<FlowTemplateRepository>();
        services.AddSingleton<FlowComponentPackRepository>();
        services.AddSingleton<PluginConfigurationRepository>();
        services.AddSingleton<PluginConnectionTester>();
        services.AddSingleton(ringBuffer);
        services.AddSingleton(repo);
        services.AddLogging();
        services.AddSingleton<Encoding.Api.Updates.ReleaseService>();
        services.AddSingleton<McpLibrariesHandler>();
        services.AddSingleton<McpAuthoringHandler>();
        services.AddSingleton<McpExecutionsHandler>();
        services.AddSingleton<McpPluginsHandler>();
        services.AddSingleton<IMcpDomainHandler>(provider => provider.GetRequiredService<McpLibrariesHandler>());
        services.AddSingleton<IMcpDomainHandler>(provider => provider.GetRequiredService<McpAuthoringHandler>());
        services.AddSingleton<IMcpDomainHandler>(provider => provider.GetRequiredService<McpExecutionsHandler>());
        services.AddSingleton<IMcpDomainHandler>(provider => provider.GetRequiredService<McpPluginsHandler>());
        services.AddSingleton<McpToolRegistry>();
        services.AddSingleton<McpService>();

        var sp = services.BuildServiceProvider();
        var mcp = sp.GetRequiredService<McpService>();

        // 1. tools/list contains get_system_logs
        var listResult = await mcp.HandleMessageAsync(
            new McpJsonRpcRequest("2.0", "tools/list", JsonDocument.Parse("1").RootElement),
            new System.Security.Claims.ClaimsPrincipal(),
            CancellationToken.None);

        Assert.NotNull(listResult);
        var listJson = JsonSerializer.Serialize(listResult);
        Assert.Contains("get_system_logs", listJson);
        Assert.Contains("get_version", listJson);
        Assert.Contains("get_updates", listJson);
        foreach (var tool in new[] { "get_version", "get_updates" })
        {
            var versionResult = await mcp.HandleMessageAsync(
                new McpJsonRpcRequest("2.0", "tools/call", JsonSerializer.SerializeToElement(10),
                    JsonSerializer.SerializeToElement(new { name = tool, arguments = new { } })),
                new System.Security.Claims.ClaimsPrincipal(), CancellationToken.None);
            var result = Assert.IsType<McpCallToolResult>(versionResult?.Result);
            Assert.False(result.IsError);
            Assert.Contains("0.0.0-dev", result.Content[0].Text);
        }

        // 2. tools/call get_system_logs returns the log
        var callRequest = new McpJsonRpcRequest(
            "2.0",
            "tools/call",
            JsonDocument.Parse("2").RootElement,
            JsonDocument.Parse("""{"name": "get_system_logs", "arguments": {"min_level": "Warning"}}""").RootElement);

        var callResult = await mcp.HandleMessageAsync(
            callRequest,
            new System.Security.Claims.ClaimsPrincipal(),
            CancellationToken.None);

        Assert.NotNull(callResult);
        var callJson = JsonSerializer.Serialize(callResult);
        Assert.Contains("log-mcp-1", callJson);
        Assert.Contains("Hardware acceleration dropped back to CPU", callJson);

        var assistantRead = await mcp.CallAssistantReadToolAsync(new AiAssistantToolCall("list_flows"), CancellationToken.None);
        Assert.Equal("list_flows", assistantRead.Name);
        Assert.Equal("/flows", assistantRead.Href);
        await Assert.ThrowsAsync<ArgumentException>(() => mcp.CallAssistantReadToolAsync(
            new AiAssistantToolCall("delete_flow", FlowId: Guid.NewGuid()), CancellationToken.None));

        var viewerWrite = await mcp.HandleMessageAsync(
            new McpJsonRpcRequest(
                "2.0",
                "tools/call",
                JsonDocument.Parse("3").RootElement,
                JsonDocument.Parse("""{"name":"delete_flow","arguments":{"flowId":"11111111-1111-4111-8111-111111111111"}}""").RootElement),
            new System.Security.Claims.ClaimsPrincipal(),
            CancellationToken.None);
        var deniedWrite = Assert.IsType<McpCallToolResult>(viewerWrite?.Result);
        Assert.True(deniedWrite.IsError);
        Assert.Contains("Permission refusée", deniedWrite.Content[0].Text);
    }
}

public sealed class SystemLogApiFactoryTests
{
    /// <summary>Garde-fou : l'enregistrement du fournisseur de logs ne doit ni bloquer ni créer d'ambiguïté DI au démarrage de l'API.</summary>
    [Fact(Timeout = 60000)]
    public async Task Api_Host_Starts_With_System_Logging()
    {
        await using var database=await TestDatabase.CreateSqliteAsync("system-log-host");
        var directory=Path.Combine(Path.GetTempPath(),"system-log-host-"+Guid.NewGuid().ToString("N"));
        try
        {
            using var factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Encoding.Api.Auth.AuthOptions>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting(Microsoft.AspNetCore.Hosting.WebHostDefaults.ContentRootKey, AppContext.BaseDirectory);
                foreach(var setting in database.Settings()) builder.UseSetting(setting.Key,setting.Value);
                builder.UseSetting("Node:DataDirectory",directory);
                builder.UseSetting("PluginSettings:KeyDirectory",Path.Combine(directory,"keys"));
                builder.UseSetting("Logging:LogLevel:Default", "Error");
                builder.UseSetting("Auth:Enabled", "false");
                builder.ConfigureServices(services =>
                {
                    foreach (var descriptor in services.Where(item => item.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)).ToArray())
                        services.Remove(descriptor);
                });
            });
            using var client = factory.CreateClient();
            Assert.NotNull(factory.Services.GetService(typeof(Encoding.Core.Logging.SystemLogRingBuffer)));
        }
        finally {if(Directory.Exists(directory)) Directory.Delete(directory,true);}
    }
}

public sealed class SystemLogStreamingRegressionTests
{
    [Fact(Timeout = 45000)]
    public async Task StreamStillDeliversNamedEventsAfterIdleHeartbeat()
    {
        await using var db = await TestDatabase.CreateSqliteAsync("system-log-stream");
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        Microsoft.AspNetCore.TestHost.WebHostBuilderExtensions.UseTestServer(builder.WebHost);
        builder.Services.AddAuthorization(options => options.AddPolicy(Encoding.Api.Auth.AuthExtensions.AdministratorPolicy,
            policy => policy.RequireAssertion(_ => true)));
        builder.Services.AddSingleton(new SystemLogRingBuffer());
        builder.Services.AddSingleton(new SystemLogStreamBroadcaster());
        builder.Services.AddSingleton(new SystemLogRepository(db.Db));
        await using var app = builder.Build();
        app.MapSystemLogEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        using var response = await client.GetAsync("/api/v1/system/logs/stream", HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellation.Token));
        Assert.Equal(": heartbeat", await reader.ReadLineAsync(cancellation.Token));
        Assert.Equal("", await reader.ReadLineAsync(cancellation.Token));
        app.Services.GetRequiredService<SystemLogStreamBroadcaster>().Publish(new SystemLogEntry(
            "after-heartbeat", DateTimeOffset.UtcNow, SystemLogLevel.Warning, "CLI", "Still connected"));
        Assert.Equal("event: log", await reader.ReadLineAsync(cancellation.Token));
        Assert.Contains("after-heartbeat", await reader.ReadLineAsync(cancellation.Token));
        await cancellation.CancelAsync();
    }
}
