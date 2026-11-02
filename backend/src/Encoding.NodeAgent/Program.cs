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
if (options.LocalEnrollmentSecretFile is { Length: > 0 } secretFile && !File.Exists(secretFile))
    LocalEnrollment.Create(secretFile);
Environment.SetEnvironmentVariable(ManagedToolCatalog.DirectoryEnvironmentVariable,
    builder.Configuration["Runtime:ManagedToolDirectory"] is { Length: > 0 } managedToolDirectory ? managedToolDirectory : ManagedToolCatalog.DefaultDirectory);
var publicUi = builder.Configuration.GetValue("WorkerUi:Public", false);
builder.WebHost.ConfigureKestrel(server =>
{
    if (publicUi) server.ListenAnyIP(options.LocalApiPort);
    else server.ListenLocalhost(options.LocalApiPort);
});
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
builder.Services.AddSingleton<PluginSyncService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<PluginSyncService>());
builder.Services.AddSingleton<ToolPackageSyncService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<ToolPackageSyncService>());
builder.Services.AddNodeModeControl(builder.Configuration[NodeModeEndpoints.ControlUrlSetting]);
builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

var app = builder.Build();
if (publicUi)
{
    CentralUiProxy.Use(app, async context =>
    {
        var identity = await context.RequestServices.GetRequiredService<WorkerIdentityStore>()
            .GetAsync(context.RequestAborted);
        return identity.IsEnrolled ? identity.ApiBaseUrl : null;
    }, options.LocalApiPort, options.LocalEnrollmentSecretFile);
}
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/panel/status", async (NodeAgentOptions agentOptions, WorkerIdentityStore identityStore, PluginSyncService plugins, ToolPackageSyncService tools, CancellationToken cancellationToken) =>
{
    var identity = await identityStore.GetAsync(cancellationToken);
    return Results.Ok(new
    {
        name = agentOptions.Name,
        displayName = identity.DisplayName ?? agentOptions.DisplayName ?? agentOptions.Name,
        enrolled = identity.IsEnrolled,
        centralUrl = agentOptions.ClusterApiBaseUrl,
        fixedWorker = agentOptions.FixedWorker,
        plugins = new { desired = plugins.DesiredCount, alignedAt = plugins.LastAlignedAt, error = plugins.LastError },
        tools = new { desired = tools.DesiredCount, alignedAt = tools.LastAlignedAt, error = tools.LastError }
    });
});
app.Use(async (context, next) =>
{
    // Canal interne du nœud : aucune dépendance aux en-têtes d'un navigateur.
    // Les lectures sont aussi protégées : l'état contient les clés de jumelage.
    if (context.Request.Path.StartsWithSegments("/api/v1/worker/link"))
    {
        await next();
        return;
    }
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
app.MapGet("/api/v1/worker/link/machine", (
    NodeAgentOptions agentOptions,
    ToolCapabilityProbe capabilityProbe) =>
{
    // La sonde FFmpeg vérifie chaque encodeur : elle ne bloque pas l'affichage du matériel.
    var capabilities = capabilityProbe.TryGetFresh();
    if (capabilities is null) capabilityProbe.EnsureFresh();
    return Results.Ok(new
    {
        name = agentOptions.Name,
        displayName = agentOptions.DisplayName,
        agentAvailable = true,
        inventory = HardwareInventory.Current() with { FontSets = null },
        tools = capabilities?.Select(tool => new
        {
            toolId = tool.ToolId,
            accelerationTypes = tool.AccelerationTypes,
            encoderChecks = (tool.EncoderChecks ?? []).Select(check => new { encoderId = check.EncoderId, status = check.Status }),
        }),
        addresses = WorkerLink.Addresses(),
        urls = Array.Empty<string>(),
    });
}).WithName("GetWorkerMachineSheet");

app.MapGet("/api/v1/worker/link", async (
    WorkerIdentityStore identityStore,
    WorkerEnrollmentService enrollment,
    CancellationToken cancellationToken) =>
    Results.Ok(await WorkerLink.ReadAsync(identityStore, enrollment, cancellationToken)))
    .WithName("GetWorkerLink");

app.MapPost("/api/v1/worker/link", async (
    WorkerLinkRequest request,
    WorkerIdentityStore identityStore,
    WorkerClusterDiscovery discovery,
    WorkerEnrollmentService enrollment,
    CancellationToken cancellationToken) =>
{
    var identity = await identityStore.GetAsync(cancellationToken);
    if (identity.IsEnrolled)
        return Results.Conflict(new { title = "Ce worker est déjà relié à un central." });
    var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName.Trim();
    if (displayName is { Length: > 128 })
        return Results.BadRequest(new { title = "Le nom du nœud ne peut pas dépasser 128 caractères." });
    if (!WorkerLink.TryNormalizeCentralUrl(request.ApiBaseUrl, out var requested))
        return Results.BadRequest(new { title = "L’adresse doit être une URL HTTP ou HTTPS, par exemple http://192.168.1.143:5080." });

    // Depuis un conteneur, 127.0.0.1 et l'IP du PC n'atteignent pas le port publié (pas de hairpin NAT).
    // Le central du même réseau Docker écoute alors sur le port interne 8080.
    var (status, baseUrl) = await WorkerLink.ReachCentralAsync(requested, cancellationToken);
    if (status is null || baseUrl is null)
        return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Ce central ne répond pas depuis ce worker.");
    if (status.SetupRequired)
        return Results.Conflict(new { title = "Ce central n’a pas encore de compte administrateur." });

    DiscoveredCluster cluster;
    try
    {
        cluster = await discovery.AddByAddressAsync(baseUrl.AbsoluteUri, cancellationToken);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { title = exception.Message });
    }
    catch (HttpRequestException exception)
    {
        return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: exception.Message);
    }

    var username = request.Username?.Trim();
    var password = request.Password ?? "";
    if (string.IsNullOrEmpty(username) || username.Length > 64 || password.Length is < 1 or > 256)
        return Results.BadRequest(new { title = "Indiquez le compte administrateur du central." });

    try
    {
        await enrollment.EnrollWithAdministratorAsync(cluster.Id, username, password, cancellationToken, displayName);
        return Results.Ok(new WorkerLinkStatus(true, cluster.Name, cluster.ApiBaseUrl, null, null));
    }
    catch (WorkerLinkRejectedException exception)
    {
        return Results.Json(new { title = exception.Message }, statusCode: exception.StatusCode);
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { title = exception.Message });
    }
    catch (HttpRequestException exception)
    {
        return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: exception.Message);
    }
}).WithName("CreateWorkerLink");

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
    NodeAgentOptions agentOptions,
    CancellationToken cancellationToken) =>
{
    if (agentOptions.FixedWorker)
        return Results.Conflict(new { success = false, message = "Ce worker est fixe : il ne devient pas serveur." });
    var response = await leadershipReceiver.EmergencyPromoteAsync(request?.Force ?? false, cancellationToken);
    return response.Success ? Results.Ok(response) : Results.Conflict(response);
}).WithName("EmergencyPromoteWorker");

if (publicUi)
{
    app.MapFallback(async (HttpContext context) =>
    {
        if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/panel"))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var index = Path.Combine(app.Environment.WebRootPath, "index.html");
        if (!File.Exists(index))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(index);
    });
}

await app.RunAsync();

public sealed record WorkerClusterAddress(string ApiBaseUrl);

public sealed record WorkerLinkRequest(string? ApiBaseUrl, string? DisplayName = null, string? Username = null, string? Password = null);

public sealed record WorkerLinkStatus(
    bool Enrolled,
    string? CentralName,
    string? CentralUrl,
    string? VerificationCode,
    DateTimeOffset? ExpiresAt);

file static class WorkerLink
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Noms du central dans <c>docker-compose.central.yml</c>. Port interne de l'image, pas le port publié.</summary>
    private static readonly Uri[] SameDockerNetwork =
    [
        new("http://server:8080/"),
        new("http://encoding-central:8080/"),
    ];

    public static bool TryNormalizeCentralUrl(string? raw, out Uri uri)
    {
        uri = null!;
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text)) return false;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(parsed.UserInfo))
            return false;
        uri = new Uri(parsed.GetLeftPart(UriPartial.Authority) + "/");
        return true;
    }

    public static async Task<(Encoding.Contracts.Auth.AuthStatusResponse? Status, Uri? BaseUrl)> ReachCentralAsync(
        Uri requested, CancellationToken cancellationToken)
    {
        var candidates = new List<Uri> { requested };
        foreach (var fallback in SameDockerNetwork)
        {
            if (!candidates.Any(candidate => string.Equals(candidate.AbsoluteUri, fallback.AbsoluteUri, StringComparison.OrdinalIgnoreCase)))
                candidates.Add(fallback);
        }

        var requestedUnreachable = false;
        foreach (var candidate in candidates)
        {
            var probe = await ProbeAuthAsync(candidate, cancellationToken);
            if (probe.Kind == CentralProbe.Ready)
                return (probe.Status, candidate);
            if (candidate == requested)
            {
                if (probe.Kind == CentralProbe.NotACentral)
                    return (null, null);
                requestedUnreachable = true;
                continue;
            }
            if (!requestedUnreachable) continue;
        }
        return (null, null);
    }

    private enum CentralProbe { Ready, Unreachable, NotACentral }

    private static async Task<(CentralProbe Kind, Encoding.Contracts.Auth.AuthStatusResponse? Status)> ProbeAuthAsync(
        Uri baseUrl, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            var status = await client.GetFromJsonAsync<Encoding.Contracts.Auth.AuthStatusResponse>(
                new Uri(baseUrl, "api/v1/auth/status"), Json, cancellationToken);
            return status is null ? (CentralProbe.NotACentral, null) : (CentralProbe.Ready, status);
        }
        catch (JsonException)
        {
            return (CentralProbe.NotACentral, null);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return (CentralProbe.Unreachable, null);
        }
    }

    public static IReadOnlyList<Encoding.Contracts.Auth.SetupNodeAddress> Addresses()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses.Select(unicast => (nic.Name, unicast.Address)))
                .Where(item => item.Address.AddressFamily is System.Net.Sockets.AddressFamily.InterNetwork or System.Net.Sockets.AddressFamily.InterNetworkV6)
                .DistinctBy(item => (item.Name, item.Address))
                .OrderBy(item => item.Name, StringComparer.Ordinal)
                .ThenBy(item => item.Address.AddressFamily)
                .ThenBy(item => item.Address.ToString(), StringComparer.Ordinal)
                .Select(item => new Encoding.Contracts.Auth.SetupNodeAddress(item.Name, item.Address.ToString()))
                .ToArray();
        }
        catch (Exception exception) when (exception is System.Net.NetworkInformation.NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
        {
            return [];
        }
    }

    public static async Task<WorkerLinkStatus> ReadAsync(
        WorkerIdentityStore identityStore,
        WorkerEnrollmentService enrollment,
        CancellationToken cancellationToken)
    {
        var identity = await identityStore.GetAsync(cancellationToken);
        if (identity.IsEnrolled)
            return new WorkerLinkStatus(true, identity.ClusterName, identity.ApiBaseUrl, null, null);
        string? code = null;
        if (identity.IsPairingPending && identity.NodeToken is not null)
        {
            var peer = await enrollment.GetLocalPeerIdentityAsync(cancellationToken);
            code = Encoding.Core.Nodes.NodeVerificationCode.FromToken(identity.Id, identity.NodeToken, peer?.PublicKey);
        }
        return new WorkerLinkStatus(
            false,
            identity.IsPairingPending ? identity.ClusterName : null,
            identity.IsPairingPending ? identity.ApiBaseUrl : null,
            code,
            identity.IsPairingPending ? identity.PairingExpiresAt : null);
    }
}

public sealed record WorkerEnrollmentRequest(Guid ClusterId, bool LeaveCurrent = false, string? DisplayName = null);
