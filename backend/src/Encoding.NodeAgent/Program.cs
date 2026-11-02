using Encoding.Core.Runtime;
using Encoding.Contracts.Logging;
using Encoding.Core.Logging;
using Encoding.Core.Nodes;
using Microsoft.AspNetCore.Mvc;
using Encoding.NodeAgent;
using Encoding.NodeAgent.Services;
using Encoding.NodeAgent.Runtime;
using Encoding.NodeAgent.Runtime.Media;
using System.Text.Json;
using System.Text.Json.Serialization;

if (args.FirstOrDefault() == "--isolated-process")
{
    Environment.Exit(ProcessSandbox.Run(args));
    return;
}
var builder = WebApplication.CreateBuilder(args);
var options = NodeAgentOptions.FromConfiguration(builder.Configuration);
Environment.SetEnvironmentVariable(ManagedToolCatalog.DirectoryEnvironmentVariable,
    builder.Configuration["Runtime:ManagedToolDirectory"] is { Length: > 0 } managedToolDirectory ? managedToolDirectory : ManagedToolCatalog.DefaultDirectory);
builder.WebHost.ConfigureKestrel(server => server.ListenLocalhost(options.LocalApiPort));
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<WorkerIdentityStore>();
builder.Services.AddSingleton<ISystemLogNodeContext>(sp =>
{
    var identityStore = sp.GetRequiredService<WorkerIdentityStore>();
    return new DefaultSystemLogNodeContext(() =>
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
builder.Services.AddHostedService<NodeDirectoryBrowserService>();
builder.Services.AddHostedService<RuntimeWorkerService>();
builder.Services.AddHostedService<ResourcePrefetchWorker>();
builder.Services.AddSingleton<ToolCapabilityProbe>();
builder.Services.AddSingleton<IToolCapabilityProbe>(provider => provider.GetRequiredService<ToolCapabilityProbe>());
builder.Services.AddSingleton<WorkerClusterDiscovery>();
builder.Services.AddSingleton<WorkerEnrollmentService>();
builder.Services.AddSingleton<LeadershipReceiverService>();
builder.Services.AddHostedService<WorkerClusterDiscovery>(provider => provider.GetRequiredService<WorkerClusterDiscovery>());
builder.Services.AddHostedService<WorkerAutoEnrollmentService>();
builder.Services.AddHostedService<NodeHeartbeatService>();
builder.Services.AddHostedService<PluginSyncService>();
builder.Services.AddHostedService<ToolPackageSyncService>();
builder.Services.AddNodeModeControl(builder.Configuration[NodeModeEndpoints.ControlUrlSetting]);
builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

var app = builder.Build();
app.Use(async (context, next) =>
{
    // Canal interne du nœud : aucune dépendance aux en-têtes d'un navigateur.
    // Les lectures sont aussi protégées : l'état contient les clés de jumelage.
    if (context.Request.Path.StartsWithSegments("/api/v1/worker")
        && !LocalEnrollment.Matches(options.LocalEnrollmentSecretFile,
            context.Request.Headers[LocalEnrollment.HeaderName].ToString()))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    await next();
});

app.MapGet("/health/ready", (HttpContext context) =>
{
    context.Response.Headers.AccessControlAllowOrigin = "*";
    return Results.Ok(new { status = "ready" });
});
app.MapNodeMode("/api/v1/worker/mode");
app.MapGet("/api/v1/worker/clusters", (WorkerClusterDiscovery discovery) => Results.Ok(discovery.List()));
app.MapPost("/api/v1/worker/clusters", async (WorkerClusterAddress request, WorkerClusterDiscovery discovery, CancellationToken cancellationToken) =>
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
app.MapDelete("/api/v1/worker/clusters/{clusterId:guid}", (Guid clusterId, WorkerClusterDiscovery discovery) =>
    discovery.Forget(clusterId)
        ? Results.NoContent()
        : Results.NotFound(new { title = "Seule une adresse ajoutée à la main peut être oubliée ; ce cluster est détecté automatiquement." })).WithName("ForgetWorkerCluster");
app.MapGet("/api/v1/worker/status", async (NodeAgentOptions agentOptions, WorkerIdentityStore identityStore, IToolCapabilityProbe capabilityProbe,
    WorkerEnrollmentService enrollment, CancellationToken cancellationToken) =>
{
    var identity = await identityStore.GetAsync(cancellationToken);
    var capabilities = await capabilityProbe.DiscoverAsync(cancellationToken);
    Encoding.Contracts.Cluster.ClusterPeerPublicIdentity? peerIdentity = null;
    try { peerIdentity = await enrollment.GetLocalPeerIdentityAsync(cancellationToken); }
    catch (HttpRequestException) { }
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
        verificationCode = identity.HasCredentials ? NodeVerificationCode.FromToken(identity.Id, identity.NodeToken!, peerIdentity?.PublicKey) : null,
        switching = identity.IsSwitching
            ? new { previousClusterName = identity.Previous!.ClusterName, deadline = identity.SwitchDeadline }
            : null,
        switchFailure = identity.SwitchFailure
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
    WorkerEnrollmentRequest request,
    WorkerEnrollmentService enrollment,
    CancellationToken cancellationToken) =>
{
    try
    {
        return (IResult)Results.Ok(await enrollment.EnrollAsync(request.ClusterId, cancellationToken, request.LeaveCurrent, request.DisplayName));
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
    WorkerEnrollmentRequest request,
    WorkerEnrollmentService enrollment,
    CancellationToken cancellationToken) =>
{
    try
    {
        return (IResult)Results.Ok(await enrollment.CreatePairingKeyAsync(request.ClusterId, cancellationToken, request.LeaveCurrent, request.DisplayName));
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

app.MapGet("/api/v1/worker/backup-status", async (LeadershipReceiverService leadershipReceiver, CancellationToken cancellationToken) =>
{
    var info = await leadershipReceiver.GetLocalBackupInfoAsync(cancellationToken);
    return info is null ? Results.NotFound(new { title = "Aucune sauvegarde miroir locale disponible." }) : Results.Ok(info);
}).WithName("GetWorkerBackupStatus");

app.MapPost("/api/v1/worker/emergency-promote", async (
    [FromBody] Encoding.Contracts.Cluster.EmergencyPromotionRequest? request,
    LeadershipReceiverService leadershipReceiver,
    CancellationToken cancellationToken) =>
{
    var response = await leadershipReceiver.EmergencyPromoteAsync(request?.Force ?? false, cancellationToken);
    return response.Success ? Results.Ok(response) : Results.Conflict(response);
}).WithName("EmergencyPromoteWorker");

await app.RunAsync();

public sealed record WorkerClusterAddress(string ApiBaseUrl);

public sealed record WorkerEnrollmentRequest(Guid ClusterId, bool LeaveCurrent = false, string? DisplayName = null);
