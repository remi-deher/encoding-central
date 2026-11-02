using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Auth;
using Encoding.Api.Cluster;
using Encoding.Api.Executions;
using Encoding.Api.Flows;
using Encoding.Contracts.Executions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Operations;
using Encoding.Api.Nodes;
using Encoding.Api.Libraries;
using Encoding.Api.Plugins;
using Encoding.Contracts.Nodes;
using Encoding.Core.Nodes;
using Encoding.Contracts.Libraries;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Encoding.Api.Logging;
using Encoding.Api.Mcp;
using Encoding.Api.Tools;
using Encoding.Core.Logging;

if (args.FirstOrDefault() is McpCommand.Name or "--" + McpCommand.Name)
    Environment.Exit(await McpCommand.RunAsync(args[1..], Console.In, Console.Out, CancellationToken.None));
if (args.FirstOrDefault() == "--isolated-process")
{
    Environment.Exit(Encoding.Core.Runtime.ProcessSandbox.Run(args));
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.AddOperations();
var pluginKeyDirectory = ClusterPeerIdentityStore.ResolveKeyDirectory(builder.Configuration, builder.Environment.ContentRootPath);
Directory.CreateDirectory(pluginKeyDirectory);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(pluginKeyDirectory))
    .SetApplicationName("Encoding.PluginSettings");
var offlineAfterSeconds = builder.Configuration.GetValue("NodePresence:OfflineAfterSeconds", 30);
if (offlineAfterSeconds <= 0)
{
    throw new InvalidOperationException("NodePresence:OfflineAfterSeconds doit être supérieur à zéro.");
}
var localDatabase = DatabaseProvider.CreateRuntime(builder.Configuration);
builder.Services.AddSingleton<System.Data.Common.DbDataSource>(localDatabase);
builder.Services.AddSingleton(new NodePresenceOptions(TimeSpan.FromSeconds(offlineAfterSeconds)));
builder.Services.AddSingleton<NodeRepository>();
builder.Services.AddSingleton<INodeModeTransitionGuard,ClusterNodeModeGuard>();
builder.Services.AddSingleton<NodeDirectoryBrowserRepository>();
builder.Services.AddSingleton<NodeGroupRepository>();
builder.Services.AddSingleton<EnrollmentWindowRepository>();
builder.Services.AddSingleton<FirstLinkRepository>();
builder.Services.AddSingleton<LocalNodeRegistry>();
// Instances créées hors conteneur : le fournisseur de logs est résolu pendant l'initialisation de ILoggerFactory.
var systemLogRepository = new SystemLogRepository(localDatabase);
var systemLogSink = new SystemLogDbSink(systemLogRepository);
builder.Services.AddSingleton(systemLogRepository);
builder.Services.AddSingleton(systemLogSink);
builder.Services.AddSingleton<ISystemLogSink>(systemLogSink);
builder.Services.AddHostedService(sp => sp.GetRequiredService<SystemLogDbSink>());
var localNodeLogContext = new LocalNodeLogContext();
builder.Services.AddSingleton(localNodeLogContext);
builder.Services.AddSingleton<ISystemLogNodeContext>(localNodeLogContext);
builder.Services.AddSystemLogging();
builder.Services.AddApiProblemDetails();
builder.Services.AddClusterAuthentication(builder.Configuration);
builder.Services.AddSingleton<NetworkSettingsService>();
builder.Services.AddSingleton<ApiTokenRepository>();
builder.Services.AddSingleton<McpSessionManager>();
builder.Services.AddSingleton<Encoding.Api.Updates.ReleaseService>();
builder.Services.AddHttpClient("release-updates", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.MaxResponseContentBufferSize = 2 * 1024 * 1024;
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Encoding-UpdateChecker/1.0");
});
builder.Services.AddSingleton<ExecutionStreamBroadcaster>();
builder.Services.AddSingleton<ExecutionRepository>();
builder.Services.AddSingleton<FlowComponentRegistry>();
builder.Services.AddSingleton<Encoding.Api.Plugins.ClusterExtensionActivationState>();
builder.Services.AddHostedService(services => services.GetRequiredService<Encoding.Api.Plugins.ClusterExtensionActivationState>());
builder.Services.AddSingleton<FlowDefinitionValidator>();
builder.Services.AddSingleton<FlowCatalogPromptBuilder>();
builder.Services.AddSingleton<FlowAiGeneratorService>();
builder.Services.AddSingleton<FlowSimulator>();
builder.Services.AddSingleton<FlowMetadataImporter>();
builder.Services.AddSingleton<FlowRepository>();
builder.Services.AddSingleton<FlowArchiveService>();
builder.Services.AddSingleton<FlowScriptRepository>();
builder.Services.AddSingleton<FlowTemplateRepository>();
builder.Services.AddSingleton<FlowComponentPackRepository>();
builder.Services.AddSingleton<EncodingProfileRepository>();
builder.Services.AddSingleton<Encoding.Api.Resources.ResourceBlobStore>();
builder.Services.AddSingleton<Encoding.Api.Resources.ResourceRepository>();
builder.Services.AddSingleton<Encoding.Api.Resources.ResourceBlobFetcher>();
builder.Services.AddHostedService<Encoding.Api.Resources.ResourcePrefetchService>();
builder.Services.AddSingleton<PluginConfigurationRepository>();
builder.Services.AddSingleton<WebhookAuthThrottle>();
builder.Services.AddSingleton<ToolStoreService>();
builder.Services.AddSingleton<PluginStoreService>();
builder.Services.AddSingleton<PluginRepositoryService>();
builder.Services.AddHttpClient(PluginRepositoryService.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Encoding-PluginStore/1.0");
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 3 });
builder.Services.AddSingleton<PluginConnectionTester>();
builder.Services.AddSingleton<Encoding.Api.Plugins.Plex.PlexApiClient>();
builder.Services.AddSingleton<Encoding.Api.Plugins.Plex.PlexSetupSession>();
builder.Services.AddSingleton<Encoding.Api.Plugins.Plex.PlexSetupService>();
builder.Services.AddSingleton<McpLibrariesHandler>();
builder.Services.AddSingleton<McpAuthoringHandler>();
builder.Services.AddSingleton<McpExecutionsHandler>();
builder.Services.AddSingleton<McpPluginsHandler>();
builder.Services.AddSingleton<IMcpDomainHandler>(provider => provider.GetRequiredService<McpLibrariesHandler>());
builder.Services.AddSingleton<IMcpDomainHandler>(provider => provider.GetRequiredService<McpAuthoringHandler>());
builder.Services.AddSingleton<IMcpDomainHandler>(provider => provider.GetRequiredService<McpExecutionsHandler>());
builder.Services.AddSingleton<IMcpDomainHandler>(provider => provider.GetRequiredService<McpPluginsHandler>());
builder.Services.AddSingleton<McpToolRegistry>();
builder.Services.AddSingleton<McpService>();
builder.Services.AddSingleton<IntegrationTriggerRepository>();
builder.Services.AddSingleton<IntegrationTriggerService>();
builder.Services.AddSingleton<AiCliRuntime>();
builder.Services.AddSingleton<AiQuotaService>();
builder.Services.AddSingleton<AiModelCatalogService>();
builder.Services.AddSingleton<AiClusterStore>();
builder.Services.AddSingleton<AiClusterTransport>();
builder.Services.AddSingleton<AiClusterService>();
builder.Services.AddSingleton<AiAssistantSupportService>();
builder.Services.AddSingleton<AiAssistantConversationsService>();
builder.Services.AddSingleton<AiAssistantActionsService>();
builder.Services.AddSingleton<AiAssistantSendService>();
builder.Services.AddSingleton<AiAssistantService>();
builder.Services.AddHostedService<AiAssistantRetentionService>();
builder.Services.AddHostedService<AiCatalogRefreshService>();
builder.Services.AddSingleton<AiCredentialStore>();
builder.Services.AddSingleton<AiCredentialSyncService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<AiCredentialSyncService>());
builder.Services.AddHttpClient("AiCluster", client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<DeviceCodeAuthService>();
builder.Services.AddSingleton<AiSettingsService>();
builder.Services.AddSingleton<AiClientFactory>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<IntegrationTriggerService>());
builder.Services.AddTransient<LocalAgentAuthenticationHandler>();
builder.Services.AddHttpClient(Encoding.Api.Nodes.LocalNodeEndpoints.HttpClientName, (services, client) =>
{
    client.BaseAddress = new Uri(Encoding.Api.Nodes.LocalNodeEndpoints.AgentBaseUrl(services.GetRequiredService<IConfiguration>()));
    client.Timeout = TimeSpan.FromSeconds(30);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
    .AddHttpMessageHandler<LocalAgentAuthenticationHandler>();
builder.Services.AddHttpClient("PluginConnectionTest", client => client.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient(Encoding.Api.Plugins.Plex.PlexApiClient.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient("MetadataSources", client => client.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<AiMetadataEnrichmentService>();
builder.Services.AddSingleton<RuntimeEligibility>();
builder.Services.AddSingleton<ClusterSourceWriteBarrier>();
builder.Services.AddSingleton<TaskOwnershipRepository>();
builder.Services.AddSingleton<TaskCheckpointRepository>();
builder.Services.AddSingleton<TaskCoordinationProjection>();
builder.Services.AddSingleton<ClusterTaskTransport>();
builder.Services.AddSingleton<ClusterTaskCoordinator>();
builder.Services.AddSingleton<ClusterTaskPeerHandler>();
builder.Services.AddSingleton<ClusterTaskMaintenance>();
builder.Services.AddHostedService(services=>services.GetRequiredService<ClusterTaskMaintenance>());
builder.Services.AddHttpClient("ClusterTaskTransfer",client=>client.Timeout=TimeSpan.FromMinutes(30)).ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler{AllowAutoRedirect=false});
builder.Services.AddHostedService<TaskOwnershipService>();
builder.Services.AddSingleton<PendingSourceWriteRepository>();
builder.Services.AddSingleton<FlowPlacementPlanner>();
builder.Services.AddSingleton<RuntimeRepository>();
builder.Services.AddSingleton<DiskLockRepository>();
builder.Services.AddSingleton<Encoding.Api.Plugins.PublicationGuardProvider>();
builder.Services.AddSingleton<RuntimeArtifactStore>();
builder.Services.AddSingleton<RuntimeTransferRepository>();
builder.Services.AddSingleton<SourceReplacementRepository>();
builder.Services.AddSingleton<ExecutionWaitRepository>();
builder.Services.AddHostedService<RuntimeRecoveryService>();
builder.Services.AddHostedService<ExecutionWaitService>();
builder.Services.AddHostedService<FlowBackupService>();
builder.Services.AddSingleton<LibraryRepository>();
builder.Services.AddSingleton<LibraryWorkflowRepository>();
builder.Services.AddSingleton<LibraryActivityRepository>();
builder.Services.AddSingleton<LibraryProcessingRepository>();
builder.Services.AddHostedService<LibraryMonitoringService>();
builder.Services.AddSingleton<LibraryNodeAccessRepository>();
builder.Services.AddSingleton<LeadershipRepository>();
builder.Services.AddSingleton<LeadershipHandoffService>();
builder.Services.AddHostedService<LeadershipPeriodicBackupWorker>();
builder.Services.AddSingleton<ClusterIdentityRepository>();
builder.Services.AddSingleton<ClusterPeerIdentityStore>();
builder.Services.AddSingleton<ClusterDataKeyStore>();
builder.Services.AddSingleton<ClusterPeerAdmissionRepository>();
builder.Services.AddSingleton<ClusterJoinCollisionRepository>();
builder.Services.AddSingleton<ClusterReconciliationRepository>();
builder.Services.AddSingleton<ClusterEntityReconciliation>();
builder.Services.AddNodeModeControl(builder.Configuration[NodeModeEndpoints.ControlUrlSetting]);
builder.Services.AddHostedService<ClusterDiscoveryPublisher>();
builder.Services.AddSingleton<RuntimeExecutionCoordinator>();
builder.Services.AddSingleton<NodeManagementService>();
builder.Services.AddSingleton<FlowCoordinatorService>();
builder.Services.AddHttpClient("ClusterReconciliation", client => client.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<IClusterTaskPeerClient,ClusterTaskPeerClient>();
builder.Services.AddSingleton<ClusterReconciliationService>();
builder.Services.AddHostedService<ClusterReconciliationService>(services => services.GetRequiredService<ClusterReconciliationService>());
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

var app = builder.Build();
// Fail startup if the bundled installed catalog is invalid or missing.
var flowRegistry = app.Services.GetRequiredService<FlowComponentRegistry>();
// Store descriptors and their tool dependencies must be active before default flows are validated or seeded.
app.Services.GetRequiredService<ToolStoreService>().LoadInstalled();
app.Services.GetRequiredService<PluginStoreService>().LoadInstalled();
await DatabaseMigrator.ApplyAsync(localDatabase, app.Environment.ContentRootPath, app.Lifetime.ApplicationStopping, app.Logger);
await StandardFlowTemplateSeeder.EnsureAsync(
    localDatabase,
    flowRegistry,
    app.Services.GetRequiredService<FlowDefinitionValidator>(),
    app.Lifetime.ApplicationStopping,
    app.Logger);
await app.Services.GetRequiredService<Encoding.Api.Plugins.ClusterExtensionActivationState>()
    .InitializeAsync(app.Lifetime.ApplicationStopping);
await app.Services.GetRequiredService<TaskOwnershipRepository>().InitializeAsync(app.Lifetime.ApplicationStopping);
app.UseRequestObservability();
app.UseTrustedProxies();
app.UseSecurityHeaders();

app.UseDefaultFiles();
app.UseStaticFiles();
if (!app.Services.GetRequiredService<Encoding.Api.Auth.AuthOptions>().Enabled)
    app.Logger.LogWarning("Auth:Enabled=false : toute requête est traitée comme administrateur, MCP et API de gestion compris. À réserver au développement.");
app.UseMiddleware<MasterRedirectMiddleware>();
app.UseClusterAuthentication();
app.MapAuthEndpoints();
app.MapApiTokenEndpoints();
app.MapNetworkSettingsEndpoints();
app.MapEnrollmentWindowEndpoints();
app.MapFlowEndpoints();
app.MapFlowAiEndpoints();
app.MapFlowScriptEndpoints();
app.MapFlowTemplateEndpoints();
app.MapFlowComponentPackEndpoints();
app.MapEncodingProfileEndpoints();
Encoding.Api.Resources.ResourceEndpoints.MapResourceEndpoints(app);
app.MapFlowSimulationEndpoints();
app.MapPluginConfigurationEndpoints();
Encoding.Api.Plugins.Plex.PlexSetupEndpoints.MapPlexSetupEndpoints(app);
app.MapPluginStoreEndpoints();
app.MapArrWebhookEndpoints();
app.MapRuntimeEndpoints();
app.MapSourceReplacementEndpoints();

app.MapOperationsEndpoints();
app.MapSystemLogEndpoints();

app.MapLibraryWorkflowEndpoints();
app.MapLibraryActivityEndpoints();
app.MapLibraryEndpoints();
app.MapClusterEndpoints();
app.MapLeadershipEndpoints();
app.MapClusterReconciliationEndpoints();
app.MapClusterTaskEndpoints();
app.MapNodeEndpoints();
app.MapNodeGroupEndpoints();
app.MapLocalNodeEndpoints();
app.MapNodeMode("/api/v1/node/mode");
app.MapExecutionEndpoints();
app.MapTaskOwnershipEndpoints();
app.MapClusterTaskCoordinationEndpoints();
app.MapGet("/api/v1/system/version", (Encoding.Api.Updates.ReleaseService releases) =>
    Results.Ok(new { version = releases.Installed.Version, commit = releases.Installed.Commit }));
app.MapGet("/api/v1/system/updates", (Encoding.Api.Updates.ReleaseService releases, CancellationToken ct) =>
    releases.GetUpdatesAsync(ct));
app.MapMcpEndpoints();
app.MapBusinessOperationEndpoints();
app.MapAiToolEndpoints();
app.MapAiAssistantEndpoints();
app.MapOpenApiEndpoints();

app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

public partial class Program { }
