using Encoding.Core.Runtime;
using Encoding.Contracts.Logging;
using Encoding.Core.Logging;
using Encoding.Core.Workers;
using Microsoft.AspNetCore.Mvc;
using Encoding.WorkerAgent;
using Encoding.WorkerAgent.Services;
using Encoding.WorkerAgent.Runtime;
using Encoding.WorkerAgent.Runtime.Media;
using System.Text.Json;
using System.Text.Json.Serialization;

if (args.FirstOrDefault() == "--isolated-process")
{
    Environment.Exit(ProcessSandbox.Run(args));
    return;
}
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = Path.GetFullPath("../api/wwwroot", AppContext.BaseDirectory)
});
var options = WorkerAgentOptions.FromConfiguration(builder.Configuration);
if (options.WorkerImage && options.LocalEnrollmentSecretFile is { } localSecretFile)
    LocalEnrollment.Create(localSecretFile);
Environment.SetEnvironmentVariable(ManagedToolCatalog.DirectoryEnvironmentVariable,
    builder.Configuration["Runtime:ManagedToolDirectory"] is { Length: > 0 } managedToolDirectory ? managedToolDirectory : ManagedToolCatalog.DefaultDirectory);
builder.WebHost.ConfigureKestrel(server =>
{
    if (builder.Configuration.GetValue("WorkerUi:Expose", false))
        server.ListenAnyIP(options.LocalApiPort);
    else
        server.ListenLocalhost(options.LocalApiPort);
});
builder.Services.AddSingleton(options);
builder.Services.AddHttpClient(CentralUiProxy.ClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(CentralUiProxy.CreateHandler);
builder.Services.AddSingleton<WorkerIdentityStore>();
builder.Services.AddSingleton<ISystemLogWorkerContext>(sp =>
{
    var identityStore = sp.GetRequiredService<WorkerIdentityStore>();
    return new DefaultSystemLogWorkerContext(() =>
    {
        return (identityStore.CurrentId, identityStore.CurrentDisplayName);
    });
});
builder.Services.AddSystemLogging();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ProcessToolRunner>();
builder.Services.AddSingleton<IProcessToolRunner>(provider => provider.GetRequiredService<ProcessToolRunner>());
builder.Services.AddSingleton<PluginProcessHost>();
builder.Services.AddSingleton<IPublicationGuardInvoker,PluginPublicationGuardInvoker>();
builder.Services.AddSingleton<PublicationGuardRunner>();
builder.Services.AddSingleton<MediaScanner>();
builder.Services.AddSingleton<MediaActions>();
builder.Services.AddSingleton<LocalPublicationArtifactStore>();
builder.Services.AddSingleton<ILocalPublicationArtifactStore>(provider => provider.GetRequiredService<LocalPublicationArtifactStore>());
builder.Services.AddSingleton<RuntimeFlowRunner>();
builder.Services.AddHostedService<RuntimeTransferService>();
builder.Services.AddHostedService<WorkspaceCleanupService>();
builder.Services.AddHostedService<SourceReplacementService>();
builder.Services.AddHostedService<LibraryDiscoveryService>();
builder.Services.AddHostedService<WorkerDirectoryBrowserService>();
builder.Services.AddHostedService<RuntimeWorkerService>();
builder.Services.AddHostedService<ResourcePrefetchWorker>();
builder.Services.AddSingleton<ToolCapabilityProbe>();
builder.Services.AddSingleton<IToolCapabilityProbe>(provider => provider.GetRequiredService<ToolCapabilityProbe>());
builder.Services.AddSingleton<WorkerCentralDiscovery>();
builder.Services.AddSingleton<WorkerEnrollmentService>();
builder.Services.AddHostedService<WorkerCentralDiscovery>(provider => provider.GetRequiredService<WorkerCentralDiscovery>());
builder.Services.AddHostedService<WorkerAutoEnrollmentService>();
builder.Services.AddHostedService<WorkerHeartbeatService>();
builder.Services.AddHostedService<PluginSyncService>();
builder.Services.AddHostedService<ToolPackageSyncService>();
builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

var app = builder.Build();
CentralUiProxy.Use(
    app,
    async context =>
    {
        var identity = await context.RequestServices.GetRequiredService<WorkerIdentityStore>()
            .GetAsync(context.RequestAborted);
        if (!string.IsNullOrWhiteSpace(identity.ApiBaseUrl))
            return identity.ApiBaseUrl;
        if (!string.IsNullOrWhiteSpace(options.ClusterApiBaseUrl))
            return options.ClusterApiBaseUrl;
        return context.RequestServices.GetRequiredService<WorkerCentralDiscovery>()
            .List()
            .FirstOrDefault(cluster => cluster.Source == "manual" && cluster.Reachable)
            ?.ApiBaseUrl;
    },
    options.LocalApiPort,
    options.LocalEnrollmentSecretFile);
app.UseDefaultFiles();
app.UseStaticFiles();
WorkerAgentGate.Use(app, options);

app.MapGet("/health/ready", (HttpContext context) =>
{
    context.Response.Headers.AccessControlAllowOrigin = "*";
    return Results.Ok(new { status = "ready" });
});
app.MapGet(WorkerAgentGate.HardwarePath, async (IToolCapabilityProbe capabilityProbe, CancellationToken cancellationToken) =>
    Results.Ok(new WorkerHardwareResponse(
        HardwareInventory.Current() with { FontSets = null },
        (await capabilityProbe.DiscoverAsync(cancellationToken))
            .Select(tool => new WorkerHardwareTool(tool.ToolId, tool.AccelerationTypes ?? []))
            .ToArray()))).WithName("GetWorkerHardware");
app.MapGet("/api/v1/worker/centrals", (WorkerCentralDiscovery discovery) => Results.Ok(discovery.List()));
app.MapPost("/api/v1/worker/centrals", async (WorkerCentralAddress request, WorkerCentralDiscovery discovery, CancellationToken cancellationToken) =>
{
    try
    {
        return (IResult)Results.Ok(await discovery.AddByAddressAsync(request.ApiBaseUrl, cancellationToken));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { title = exception.Message });
    }
    catch (HttpRequestException exception)
    {
        return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: exception.Message);
    }
}).WithName("AddWorkerClusterByAddress");
app.MapDelete("/api/v1/worker/centrals/{clusterId:guid}", (Guid clusterId, WorkerCentralDiscovery discovery) =>
    discovery.Forget(clusterId)
        ? Results.NoContent()
        : Results.NotFound(new { title = "Seule une adresse ajoutée à la main peut être oubliée ; ce central est détecté automatiquement." })).WithName("ForgetWorkerCluster");
app.MapGet("/api/v1/worker/status", async (WorkerAgentOptions agentOptions, WorkerIdentityStore identityStore, IToolCapabilityProbe capabilityProbe,
    WorkerEnrollmentService enrollment, CancellationToken cancellationToken) =>
{
    var identity = await identityStore.GetAsync(cancellationToken);
    var capabilities = await capabilityProbe.DiscoverAsync(cancellationToken);
    return Results.Ok(new
    {
        nodeId = identity.Id,
        name = agentOptions.Name,
        roles = agentOptions.Roles,
        maxConcurrentTasks = agentOptions.MaxConcurrentTasks,
        automaticEnrollmentEnabled = agentOptions.AutomaticEnrollmentEnabled,
        capabilities,
        inventory = HardwareInventory.Current(),
        identity.IsEnrolled,
        pairingKey = identity.IsPairingPending && identity.PairingExpiresAt > DateTimeOffset.UtcNow ? identity.PairingKey : null,
        pairingExpiresAt = identity.IsPairingPending ? identity.PairingExpiresAt : null,
        clusterId = identity.ClusterId,
        clusterName = identity.ClusterName,
        displayName = identity.DisplayName ?? agentOptions.DisplayName,
        verificationCode = identity.HasCredentials ? WorkerVerificationCode.FromToken(identity.Id, identity.NodeToken!) : null,
        enrollmentFailure = identity.EnrollmentFailure
    });
});
app.MapGet("/api/v1/worker/logs", (
    [FromQuery] SystemLogLevel? minLevel,
    [FromQuery] string? category,
    [FromQuery] string? search,
    [FromQuery] int? limit,
    [FromQuery] DateTimeOffset? since,
    SystemLogRingBuffer ringBuffer) =>
{
    var query = new SystemLogFilterQuery(
        MinLevel: minLevel,
        Category: category,
        Search: search,
        Limit: limit.GetValueOrDefault(100),
        Since: since);
    return Results.Ok(ringBuffer.Query(query));
}).WithName("GetWorkerLogs");
app.MapPost("/api/v1/worker/enroll", async (
    JoinCentralRequest request,
    WorkerEnrollmentService enrollment,
    HttpContext http,
    CancellationToken cancellationToken) =>
{
    try
    {
        // Session du compte administrateur du central, transmise par le relais de l'interface (ADR 0034).
        var centralSession = http.Request.Headers[Encoding.Contracts.Auth.SessionCookie.RelayHeader].ToString();
        return (IResult)Results.Ok(await enrollment.EnrollAsync(request.ClusterId, cancellationToken, request.DisplayName, centralSession));
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { title = exception.Message });
    }
    catch (HttpRequestException exception)
    {
        return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: exception.Message);
    }
}).WithName("ConfirmWorkerEnrollment");

app.MapPost("/api/v1/worker/pairing-key", async (
    JoinCentralRequest request,
    WorkerEnrollmentService enrollment,
    CancellationToken cancellationToken) =>
{
    try
    {
        return (IResult)Results.Ok(await enrollment.CreatePairingKeyAsync(request.ClusterId, cancellationToken, request.DisplayName));
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { title = exception.Message });
    }
    catch (HttpRequestException exception)
    {
        return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: exception.Message);
    }
}).WithName("CreateWorkerPairingKey");

app.MapPost("/api/v1/worker/leave", async (WorkerEnrollmentService enrollment, CancellationToken cancellationToken) =>
{
    try
    {
        await enrollment.LeaveAsync(cancellationToken);
        return (IResult)Results.NoContent();
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { title = exception.Message });
    }
    catch (HttpRequestException exception)
    {
        return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: exception.Message);
    }
}).WithName("LeaveWorkerCluster");

app.MapFallbackToFile("index.html");

await app.RunAsync();

public sealed record WorkerCentralAddress(string ApiBaseUrl);

public sealed record JoinCentralRequest(Guid ClusterId, string? DisplayName = null);
