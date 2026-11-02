using Encoding.Api.Infrastructure.Data;
using System.Security.Cryptography;
using System.Text.Json;
using System.Net.Http.Json;
using Encoding.Api.Executions;
using Encoding.Api.Flows;
using Encoding.Api.Infrastructure;
using Encoding.Api.Libraries;
using Encoding.Api.Nodes;
using Encoding.Api.Plugins;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Flows.Conditions;
using Encoding.Contracts.Nodes;
using Encoding.Contracts.Plugins;
using Encoding.NodeAgent;
using Encoding.NodeAgent.Runtime;
using Encoding.NodeAgent.Runtime.Media;
using Encoding.NodeAgent.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Encoding.Runtime.Tests;

internal static partial class Harness
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value, Json);
    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "backend", "migrations"))) return Path.Combine(directory.FullName, "backend");
        throw new DirectoryNotFoundException("The backend migrations directory was not found from the test output.");
    }

    internal static (FlowComponentRegistry Registry, FlowDefinitionValidator Validator, RuntimeEligibility Eligibility) Services()
    {
        var registry = StoreCatalogFixture.Create();
        var validator = new FlowDefinitionValidator(registry);
        return (registry, validator, new RuntimeEligibility(registry, validator));
    }

    internal static void RelayContracts()
    {
        var (registry, validator, eligibility) = Services();
        var destination = Guid.NewGuid();
        var relay = Node(registry, "relay", "nodes-handoff", new() { ["mode"] = J("node"), ["targetNodeId"] = J(destination) });
        var flow = Chain(Node(registry, "entry", "base-input-file"), relay,
            Node(registry, "encode", "base-transcode-video"), Node(registry, "end", "base-complete-flow"));
        Check(validator.Validate(flow).IsValid, "A valid relay flow must publish.");
        Check(RuntimeHandoffPolicy.Segment(flow, "entry").Select(node => node.Id).SequenceEqual(["entry", "relay"]), "A relay bounds the current segment.");
        Check(eligibility.CanRun(flow, Capabilities(false), Guid.NewGuid()), "The first segment must not require the next worker's encoder.");
        Check(!eligibility.CanRun(flow, Capabilities(false), destination, "encode", true), "The next segment requires its encoder.");
        Check(eligibility.CanRun(flow, Capabilities(true), destination, "encode", true), "A compatible receiver can resume.");
        Check(!eligibility.CanRun(flow, Capabilities(false).Select(cap => cap.ToolId == "plugin/nodes" ? cap with { Version = "1.0.0" } : cap).ToArray(), Guid.NewGuid()), "An old agent cannot execute the new relay.");
        var legacy = Node(registry, "constraint", "nodes-run-on-node", new() { ["targetNodeId"] = J(destination) });
        Check(registry.Resolve(legacy.Component with { PluginVersion = "1.0.0" }) is not null, "Legacy node constraints remain resolvable.");
        Check(!new RuntimeHandoffSelector("eligible").Accepts(destination, destination), "A relay excludes the previous worker.");
        var inherited = relay with { Configuration = new Dictionary<string, JsonElement> { ["mode"] = J("inherit") } };
        Check(RuntimeHandoffPolicy.Resolve(inherited, new Dictionary<string, JsonElement> { ["mode"] = J("node"), ["targetNodeId"] = J(destination) }).TargetNodeId == destination, "Plugin defaults are inherited.");
        Check(RuntimeHandoffPolicy.Resolve(relay with { Configuration = new Dictionary<string, JsonElement> { ["mode"] = J("eligible") } }, new Dictionary<string, JsonElement> { ["mode"] = J("node"), ["targetNodeId"] = J(destination) }).Mode == "eligible", "The block overrides defaults.");
        Check(!validator.Validate(Chain(Node(registry, "entry", "base-input-file"), relay)).IsValid, "A relay requires a connected continuation.");
        var branch = flow with { Nodes = [.. flow.Nodes, Node(registry, "other", "base-variable", new() { ["name"] = J("x"), ["value"] = J("1") })], Connections = [.. flow.Connections, new("branch", "entry", "error", "other")] };
        Check(RuntimeHandoffPolicy.Segment(branch, "entry").Any(node => node.Id == "other"), "All possible branches within the segment are considered.");
        var directBlock = Node(registry, "directAccess", "nodes-require-direct-access");
        var directFlow = Chain(Node(registry, "entry", "base-input-file"), directBlock, relay,
            Node(registry, "encode", "base-transcode-video"), Node(registry, "end", "base-complete-flow"));
        Check(validator.Validate(directFlow).IsValid, "Direct access flow must be valid.");
        Check(eligibility.RequiresDirectAccess(directFlow), "The first segment requires direct access.");
        Check(!eligibility.RequiresDirectAccess(directFlow, "encode", true), "The segment after handoff does not require direct access.");
        Check(!eligibility.PrefersDirectAccess(directFlow), "Default mode is strict, not prefer.");
        var preferBlock = Node(registry, "preferAccess", "nodes-require-direct-access", new() { ["mode"] = J("prefer") });
        var preferFlow = Chain(Node(registry, "entry", "base-input-file"), preferBlock, relay,
            Node(registry, "encode", "base-transcode-video"), Node(registry, "end", "base-complete-flow"));
        Check(!eligibility.RequiresDirectAccess(preferFlow), "Prefer mode does not strictly require direct access.");
        Check(eligibility.PrefersDirectAccess(preferFlow), "Prefer mode sets PrefersDirectAccess.");
    }

    /// <summary>« Exiger une capacité » avec un type d'encodage : matériel exclut le CPU, CPU exclut les accélérateurs.</summary>
    internal static void EncoderKindContracts()
    {
        var (registry, validator, eligibility) = Services();
        NodeToolCapability[] Worker(params string[] encoders) => [.. Capabilities(false), new NodeToolCapability("ffmpeg", "7.0", ["video"], ["cpu"], encoders)];
        FlowDefinition Flow(string? kind, string? encoder = "hevc")
        {
            var values = new Dictionary<string, JsonElement> { ["toolId"] = J("ffmpeg") };
            if (encoder is not null) values["requiredEncoder"] = J(encoder);
            if (kind is not null) values["encoderKind"] = J(kind);
            return new FlowDefinition(1, Guid.NewGuid(), 1, "Encoder kind fixture", "video",
                [Node(registry, "entry", "base-input-file"), Node(registry, "need", "nodes-require-capability", values), Node(registry, "end", "base-complete-flow")],
                [new("a", "entry", "success", "need"), new("b", "need", "success", "end")]);
        }
        var cpuOnly = Worker("libx265"); var gpu = Worker("libx265", "hevc_qsv"); var worker = Guid.NewGuid();
        Check(validator.Validate(Flow("hardware")).IsValid, "The encoder kind is a valid setting.");
        Check(!eligibility.CanRun(Flow("hardware"), cpuOnly, worker), "Hardware: a CPU-only encoder does not qualify even though it encodes HEVC.");
        Check(eligibility.CanRun(Flow("hardware"), gpu, worker), "Hardware: a worker with a hardware HEVC encoder qualifies.");
        Check(!eligibility.CanRun(Flow("hardware", "h264"), gpu, worker), "Hardware: the codec still has to match.");
        Check(eligibility.CanRun(Flow("hardware", null), gpu, worker) && !eligibility.CanRun(Flow("hardware", null), cpuOnly, worker), "Hardware without a codec accepts any hardware encoder.");
        Check(eligibility.CanRun(Flow("cpu"), cpuOnly, worker) && !eligibility.CanRun(Flow("cpu"), Worker("hevc_qsv"), worker), "CPU: only software encoders qualify.");
        Check(eligibility.CanRun(Flow("any"), cpuOnly, worker) && eligibility.CanRun(Flow(null), cpuOnly, worker), "Any / unset keeps the previous behaviour.");
        Check(!eligibility.CanRun(Flow("hardware"), gpu.Select(cap => cap.ToolId == "plugin/nodes" ? cap with { Version = "1.6.0" } : cap).ToArray(), worker),
            "An agent announcing nodes 1.6.0 does not receive a flow that uses the new setting.");
    }

    /// <summary>« Selon le profil d'encodage du flow » : le relais exige un encodeur utilisable du profil du bloc vidéo.</summary>
    internal static void ProfileEncoderContracts()
    {
        var (registry, validator, eligibility) = Services();
        NodeToolCapability Ffmpeg(params string[] usable) => new("ffmpeg", "7.0", ["video"], ["cpu", "qsv", "vaapi"], usable,
            EncoderChecks: [.. usable.Select(id => new NodeEncoderCapability(id, "usable"))]);
        NodeToolCapability[] Worker(params string[] usable) => [.. Capabilities(false), Ffmpeg(usable)];
        FlowDefinition Flow(bool retryWithCpu = false, params EncodingProfileTarget[] targets)
        {
            var profile = new EncodingProfileSnapshot(Guid.NewGuid(), 1, "Matériel", "", targets);
            var video = Node(registry, "encode", "video-transcode", new() { ["encoderMode"] = J("automatic"), ["videoCodec"] = J("hevc"), ["retryWithCpu"] = J(retryWithCpu), ["encodingProfile"] = JsonSerializer.SerializeToElement(profile, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
            return new FlowDefinition(1, Guid.NewGuid(), 1, "Profile fixture", "video",
                [Node(registry, "entry", "base-input-file"), Node(registry, "need", "nodes-require-capability", new() { ["toolId"] = J("ffmpeg"), ["encoderKind"] = J("profile") }), video, Node(registry, "end", "base-complete-flow")],
                [new("a", "entry", "success", "need"), new("b", "need", "success", "encode"), new("c", "encode", "success", "end")]);
        }
        EncodingProfileTarget[] hardware = [new("hevc_qsv", 25, "medium", 0), new("hevc_vaapi", 23, "", 1)];
        var worker = Guid.NewGuid();
        var check = validator.Validate(Flow(false, hardware)); Check(check.IsValid, "The profile choice is a valid setting: " + string.Join("; ", check.Issues.Select(i => i.Path + " " + i.Message)));
        Check(eligibility.CanRun(Flow(false, hardware), Worker("hevc_qsv"), worker), "A worker with the first target of the profile qualifies.");
        Check(eligibility.CanRun(Flow(false, hardware), Worker("hevc_vaapi"), worker), "Any usable target of the profile is enough.");
        Check(!eligibility.CanRun(Flow(false, hardware), Worker("libx265"), worker), "A CPU-only worker is refused when the profile has hardware targets only.");
        Check(eligibility.CanRun(Flow(true, hardware), Worker("libx265"), worker), "The CPU fallback of the video block is honoured.");
        Check(!eligibility.CanRun(Flow(false, [new("hevc_nvenc", 25, "medium", 0)]), Worker("hevc_qsv"), worker), "A profile change moves the requirement.");
        Check(!eligibility.CanRun(Flow(false, hardware), Worker("hevc_qsv").Select(cap => cap.ToolId == "plugin/nodes" ? cap with { Version = "1.6.0" } : cap).ToArray(), worker),
            "An agent announcing nodes 1.6.0 does not receive a flow that uses the profile choice.");
    }

    internal static IReadOnlyList<NodeToolCapability> Capabilities(bool encode) => [
        new("plugin/base", "1.9.0", ["general"], []),
        new("runtime/source-write-guard-v1", "1.0.0", ["general"], []),
        new("plugin/probe", "1.0.0", ["general"], []),
        new("mkvtoolnix", "92.0", ["video"], []),
        new("plugin/video", "1.1.0", ["general"], []),
        new("plugin/audio", "1.0.0", ["general"], []),
        new("plugin/subtitles", "1.0.0", ["general"], []),
        new("plugin/nodes", "1.8.0", ["general"], []),
        new("plugin/conditions", "1.2.0", ["general"], []),
        new("plugin/plex", "1.1.0", ["general"], ["process"]),
        new("ffprobe", "7.0", ["video"], []),
        .. encode ? new NodeToolCapability[] { new("ffmpeg", "7.0", ["video"], ["cpu"], ["libx264", "libx265", "aac"]) } : []];

    internal static FlowNodeDefinition Node(FlowComponentRegistry registry, string id, string componentId, Dictionary<string, JsonElement>? values = null)
    {
        var reference = registry.FindReference(componentId) ?? throw new InvalidOperationException($"Composant {componentId} introuvable.");
        var plugin = registry.Plugins.Single(plugin => plugin.Id == reference.PluginId);
        var component = plugin.Components.Single(component => component.Id == reference.ComponentId);
        var configuration = component.ConfigurationSchema.Fields.Where(field => field.DefaultValue is not null).ToDictionary(field => field.Id, field => field.DefaultValue!.Value);
        foreach (var pair in values ?? []) configuration[pair.Key] = pair.Value;
        return new(id, null, new(plugin.Id, plugin.Version, component.Id, component.Version, component.ConfigurationSchema.Version), configuration);
    }
    internal static FlowDefinition Chain(params FlowNodeDefinition[] nodes) => new(1, Guid.NewGuid(), 1, "Relay fixture", "video", nodes,
        nodes.Zip(nodes.Skip(1), (source, target) => new FlowConnectionDefinition(source.Id + "-" + target.Id, source.Id, "success", target.Id)).ToArray());

    private sealed class FixedProbe(params string[] usable) : IToolCapabilityProbe
    {
        public Task<IReadOnlyList<NodeToolCapability>> DiscoverAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<NodeToolCapability>>(
            [.. Capabilities(false), new NodeToolCapability("ffmpeg", "7.0", ["video"], ["cpu", "qsv"], usable, [.. usable.Select(id => new NodeEncoderCapability(id, "usable"))])]);
    }

    private static FlowDefinition HardwareProfileFlow(FlowComponentRegistry registry, bool requireEncoder)
    {
        var profile = new EncodingProfileSnapshot(Guid.NewGuid(), 1, "Matériel", "", [new("hevc_qsv", 25, "medium", 0)]);
        var video = Node(registry, "encode", "video-transcode", new() { ["encoderMode"] = J("automatic"), ["videoCodec"] = J("hevc"), ["encodingProfile"] = JsonSerializer.SerializeToElement(profile, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
        return new FlowDefinition(1, Guid.NewGuid(), 1, "Library access with encoder", "video",
            [Node(registry, "entry", "base-input-file"), Node(registry, "access", "nodes-library-access", new() { ["requireProfileEncoder"] = J(requireEncoder) }), video,
             Node(registry, "relay", "nodes-handoff"), Node(registry, "end", "base-complete-flow")],
            [new("a", "entry", "success", "access"), new("b", "access", "yes", "encode"), new("c", "encode", "success", "end"), new("d", "access", "no", "relay"), new("e", "relay", "success", "end")]);
    }

    /// <summary>« Bibliothèque sur ce nœud » avec « et encodeur du profil » : un nœud d'accès sans encodeur n'est pas écarté, il répond « non ».</summary>
    internal static void LibraryAccessWithEncoderContracts()
    {
        var (registry, validator, eligibility) = Services();
        var flow = HardwareProfileFlow(registry, true); var plain = HardwareProfileFlow(registry, false);
        var cpuOnly = Capabilities(false).Concat([new NodeToolCapability("ffmpeg", "7.0", ["video"], ["cpu"], ["libx265"])]).ToArray();
        var gpu = Capabilities(false).Concat([new NodeToolCapability("ffmpeg", "7.0", ["video"], ["cpu", "qsv"], ["hevc_qsv"], [new("hevc_qsv", "usable")])]).ToArray();
        var worker = Guid.NewGuid();
        var check = validator.Validate(flow); Check(check.IsValid, "The option is a valid setting: " + string.Join("; ", check.Issues.Select(i => i.Path + " " + i.Message)));
        Check(eligibility.CanRun(flow, gpu, worker), "A node with the profile encoder takes the flow.");
        Check(eligibility.CanRun(flow, cpuOnly, worker), "A node without the profile encoder still takes the flow: its « Oui » branch is not asked of it.");
        Check(!eligibility.CanRun(plain, cpuOnly, worker), "Without the option, every branch is still checked.");
        Check(!eligibility.CanRun(flow, gpu.Select(cap => cap.ToolId == "plugin/nodes" ? cap with { Version = "1.6.0" } : cap).ToArray(), worker), "An agent announcing nodes 1.6.0 does not receive a flow that uses the option.");
    }

    private sealed class Probe : IToolCapabilityProbe
    {
        public Task<IReadOnlyList<NodeToolCapability>> DiscoverAsync(CancellationToken ct) => Task.FromResult(Capabilities(true));
    }

    internal sealed partial class Fixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "encoding-handoff-" + Guid.NewGuid().ToString("N"));
        private readonly string connection;
        private readonly FlowComponentRegistry registry;
        private readonly FlowDefinitionValidator validator;
        private readonly RuntimeEligibility eligibility;
        private TestDatabase testDb = null!;
        private System.Data.Common.DbDataSource db = null!;
        private WebApplication app = null!;
        private RuntimeRepository runtime = null!;
        private RuntimeArtifactStore artifacts = null!;
        private readonly LocalPublicationArtifactStore localPublicationArtifacts = new();
        private PluginConfigurationRepository configurations = null!;
        private ExecutionRepository executions = null!;
        private readonly bool sourceWriteGuards;
        private ClusterPeerIdentityStore? sourceWriteIdentity;
        private ClusterReconciliationHealth? sourceWriteHealth;
        private ClusterSourceWriteBarrier? sourceWriteBarrier;
        private int rejectNextCheckpoint;
        private int rejectNextFinish;
        private readonly Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        private const string Token = "fixture-node-token-is-long-enough-for-authentication";
        private Fixture(string connection, FlowComponentRegistry registry, FlowDefinitionValidator validator, RuntimeEligibility eligibility,bool sourceWriteGuards=false)
        { this.connection=connection; this.registry=registry; this.validator=validator; this.eligibility=eligibility;this.sourceWriteGuards=sourceWriteGuards; }

        public static async Task<Fixture> CreateAsync(string connection, FlowComponentRegistry registry, FlowDefinitionValidator validator, RuntimeEligibility eligibility,bool sourceWriteGuards=false)
        {
            var fixture=new Fixture(connection,registry,validator,eligibility,sourceWriteGuards);
            try { await fixture.InitializeAsync(); return fixture; }
            catch { await fixture.DisposeAsync(); throw; }
        }
        private async Task InitializeAsync()
        {
            Directory.CreateDirectory(directory);
            testDb=await TestDatabase.CreateAsync(connection,"handoff"); db=testDb.Db;
            await DatabaseMigrator.ApplyAsync(db,RepositoryRoot(),CancellationToken.None);
            var extensionActivation = new ClusterExtensionActivationState(
                db,
                new ClusterReconciliationRepository(db),
                registry,
                NullLogger<ClusterExtensionActivationState>.Instance);
            await extensionActivation.InitializeAsync(CancellationToken.None);
            var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Runtime:ArtifactDirectory"]=Path.Combine(directory,"durable") }).Build();
            configurations=new(db,DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directory,"keys"))));
            TaskOwnershipRepository? ownership=null;
            if(sourceWriteGuards)
            {
                var keyConfig=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["PluginSettings:KeyDirectory"]=Path.Combine(directory,"source-write-keys")}).Build();
                sourceWriteIdentity=new(keyConfig,new EphemeralDataProtectionProvider());sourceWriteHealth=new();sourceWriteHealth.Enable();sourceWriteBarrier=new(db,sourceWriteHealth,sourceWriteIdentity);
                ownership=new(db,sourceWriteIdentity,sourceWriteHealth);await ownership.InitializeAsync(default);
            }
            runtime=new(db,eligibility,registry,new NodePresenceOptions(TimeSpan.FromHours(1)),configurations,new NodeGroupRepository(db),sourceWriteBarrier,ownership);
            artifacts=new(db,config); executions=new(db,ownership:ownership);
            foreach(var id in new[]{a,b,c})
            {
                await using var command=db.CreateCommand("INSERT INTO cluster_nodes(id,name,roles,max_concurrent_tasks,capabilities,last_seen_at,node_token_hash) VALUES(@id,@name,ARRAY['worker'],100,@caps,clock_timestamp(),@token)");
                command.Parameters.AddWithValue("id",id); command.Parameters.AddWithValue("name",id.ToString());
                command.Parameters.AddWithValue("caps",SqlKind.Json,JsonSerializer.Serialize(Capabilities(true),Json));
                command.Parameters.AddWithValue("token",SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Token))); await command.ExecuteNonQueryAsync();
            }
            var presenceOptions = new NodePresenceOptions(TimeSpan.FromHours(1));
            var flowRepo = new FlowRepository(db);
            var workflowRepo = new LibraryWorkflowRepository(db, validator, presenceOptions,ownership:ownership);
            var builder=WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.IncludeScopes = false; });
            builder.Logging.SetMinimumLevel(LogLevel.Information);
            builder.Services.AddApiProblemDetails();
            builder.Services.ConfigureHttpJsonOptions(options => { options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase; });
            builder.Services.AddSingleton<System.Data.Common.DbDataSource>(db); builder.Services.AddSingleton(runtime); builder.Services.AddSingleton(artifacts);
            builder.Services.AddSingleton<PendingSourceWriteRepository>(_=>sourceWriteBarrier is {} barrier?new(db,barrier):throw new NotSupportedException("Pas de remplacement différé dans ce scénario."));
            builder.Services.AddSingleton(new ExecutionWaitRepository(db, presenceOptions)); builder.Services.AddSingleton(new RuntimeTransferRepository(db,sourceWrites:sourceWriteBarrier,ownership:ownership)); builder.Services.AddSingleton(new DiskLockRepository(db)); builder.Services.AddSingleton(configurations); builder.Services.AddSingleton<Encoding.Api.Plugins.PublicationGuardProvider>(); builder.Services.AddSingleton(new SourceReplacementRepository(db,sourceWriteBarrier)); builder.Services.AddSingleton(config);
            builder.Services.AddSingleton(new LibraryProcessingRepository(db)); builder.Services.AddSingleton(workflowRepo);
            builder.Services.AddSingleton(validator); builder.Services.AddSingleton(presenceOptions);
            builder.Services.AddSingleton(extensionActivation);
            builder.Services.AddSingleton(eligibility); builder.Services.AddSingleton(new NodeRepository(db));
            builder.Services.AddSingleton(new NodeDirectoryBrowserRepository(db));
            builder.Services.AddSingleton(registry); builder.Services.AddSingleton(flowRepo); builder.Services.AddSingleton(new FlowArchiveService(flowRepo, validator));
            builder.Services.AddSingleton(new FlowTemplateRepository(db, registry));
            // Les routes des ressources du runtime ont besoin de ces services déclarés ; ces scénarios ne les appellent pas.
            builder.Services.AddSingleton(_ => new Encoding.Api.Resources.ResourceBlobStore(config));
            builder.Services.AddSingleton(provider => new Encoding.Api.Resources.ResourceRepository(db, provider.GetRequiredService<Encoding.Api.Resources.ResourceBlobStore>()));
            builder.Services.AddSingleton<Encoding.Api.Resources.ResourceBlobFetcher>(_ => throw new NotSupportedException("Récupération par un pair non utilisée dans ce scénario."));
            builder.Services.AddSingleton<Encoding.Api.Executions.RuntimeExecutionCoordinator>();
            builder.Services.AddSingleton<Encoding.Api.Nodes.NodeManagementService>();
            builder.Services.AddSingleton<Encoding.Api.Flows.FlowCoordinatorService>();
            app=builder.Build();
            app.Use(async (context,next) =>
            {
                if(context.Request.Method=="GET" && context.Request.Path.Value?.Contains("/dav/",StringComparison.Ordinal)==true)
                    Interlocked.Increment(ref artifactDownloads);
                if(context.Request.Path.Value?.EndsWith("/steps/complete",StringComparison.Ordinal)==true
                    && Interlocked.Exchange(ref rejectNextCheckpoint,0)==1)
                {
                    context.Response.StatusCode=StatusCodes.Status409Conflict;
                    return;
                }
                if(context.Request.Path.Value?.EndsWith("/finish",StringComparison.Ordinal)==true
                    && Interlocked.Exchange(ref rejectNextFinish,0)==1)
                {
                    context.Response.StatusCode=StatusCodes.Status409Conflict;
                    return;
                }
                await next();
            });
            app.MapRuntimeEndpoints(); app.MapFlowEndpoints();
            app.MapGet("/api/v1/executions/{id:guid}",async(Guid id)=>await executions.GetAsync(id,CancellationToken.None));
            await app.StartAsync();
        }
        private RuntimeClient Client(Guid node) => new(new WorkerIdentityState(node,"fixture","fixture",app.Urls.Single()+"/",Token,true));
        public async Task FlowSavingAsync()
        {
            var repository = new FlowRepository(db);
            var flow = Chain(Node(registry,"entry","base-input-file"),Node(registry,"end","base-complete-flow"));
            var first = await repository.SaveAsync(new(0,flow),CancellationToken.None);
            Check(first?.Definition.Version==1,"The first save publishes v1.");
            var same = flow with { Version=2 };
            var repeated = await Task.WhenAll(Enumerable.Range(0,4).Select(_=>repository.SaveAsync(new(1,same),CancellationToken.None)));
            Check(repeated.All(result=>result?.Definition.Version==1),"Concurrent unchanged saves acknowledge v1 without incrementing.");
            Check((await repository.VersionsAsync(flow.Id,CancellationToken.None)).Count==1,"Only one immutable version exists after unchanged saves.");
            await using(var count=db.CreateCommand("SELECT count(*) FROM flow_backup_jobs WHERE flow_id=@id"))
            { count.Parameters.AddWithValue("id",flow.Id); Check((long)(await count.ExecuteScalarAsync())! == 1,"Unchanged saves create no extra backup jobs."); }
            var draft = await repository.SaveDraftAsync(new(0,same),CancellationToken.None);
            Check(await repository.SaveAsync(new(1,same,draft!.DraftRevision+1),CancellationToken.None) is null,"An unchanged save still enforces draft concurrency.");
            Check((await repository.SaveAsync(new(1,same,draft.DraftRevision),CancellationToken.None))?.Definition.Version==1
                && await repository.DraftAsync(flow.Id,CancellationToken.None) is null,"An unchanged matching draft is finalized without a new publication.");
            var changed = same with { Name="Modified flow" };
            Check((await repository.SaveAsync(new(1,changed),CancellationToken.None))?.Definition.Version==2,"A meaningful edit creates exactly one new version.");
            Check(await repository.SaveAsync(new(1,changed with { Version=3 },null),CancellationToken.None) is null,"A stale editor cannot overwrite a newer version.");
            using var client=new HttpClient { BaseAddress=new Uri(app.Urls.Single()) };
            client.DefaultRequestHeaders.Add("X-Encoding-Csrf", "1");
            using var response=await client.PutAsJsonAsync($"/api/v1/flows/{flow.Id}",new SaveFlowRequest(2,changed with { Version=3 }));
            var flowResponseContent = await response.Content.ReadAsStringAsync();
            var flowResponse = response.IsSuccessStatusCode ? JsonSerializer.Deserialize<FlowVersionResponse>(flowResponseContent, Json) : null;
            Check(response.IsSuccessStatusCode && flowResponse?.Definition.Version==2,$"The API returns the exact existing version for an unchanged request. HTTP {(int)response.StatusCode}: {flowResponseContent}");
            var importId=Guid.NewGuid(); var imported=flow with { Id=importId };
            Check(await repository.SaveManyAsync([new(0,imported),new(1,imported with {Version=2})],false,CancellationToken.None)
                && (await repository.VersionsAsync(importId,CancellationToken.None)).Count==2,"Archive imports preserve historical versions even with identical contents.");
        }

        public async Task NodeGroupsAndLabelsAsync()
        {
            var groups = new NodeGroupRepository(db);
            await using (var inventory = db.CreateCommand("UPDATE cluster_nodes SET inventory=@inventory WHERE id=@id"))
            {
                inventory.Parameters.AddWithValue("id", a);
                inventory.Parameters.AddWithValue("inventory", SqlKind.Json, JsonSerializer.Serialize(new NodeInventory("Linux", "X64", 8, 32768), Json));
                await inventory.ExecuteNonQueryAsync();
            }
            var manual = new SaveNodeGroupRequest("Production", "Workers média", NodeGroupMode.Manual, new(),
                new Dictionary<string,string?> { ["zone"]="group", ["gpu"]="nvidia" }, [a,b]);
            Check(await groups.SaveAsync(null, manual, CancellationToken.None), "A manual group is saved.");
            var automatic = new SaveNodeGroupRequest("Workers RAM", "Au moins 8 Go", NodeGroupMode.Automatic,
                new NodeGroupCriteria([NodeRole.Worker], MinimumMemoryMb: 8192), new Dictionary<string,string?> { ["zone"]="auto", ["memory"]="large" }, []);
            Check(await groups.SaveAsync(null, automatic, CancellationToken.None), "An automatic group is saved.");
            var identityRepository = new NodeGroupRepository(db);
            Check(await identityRepository.SaveNodeIdentityAsync(a, new("Transcode A", new Dictionary<string,string?> { ["zone"]="local" }), CancellationToken.None), "Node labels are saved.");
            var identity = await identityRepository.GetNodeIdentityAsync(a, CancellationToken.None);
            Check(identity is not null && identity.GroupIds.Count == 2, "Manual and automatic membership are both resolved.");
            Check(identity!.EffectiveLabels["zone"] == "local" && identity.EffectiveLabels["gpu"] == "nvidia" && identity.EffectiveLabels["memory"] == "large", "Node labels override group labels and inherit the remaining group labels.");
            Check(Encoding.Contracts.Nodes.NodeLabelSelector.Matches("gpu=nvidia,memory=large", identity.EffectiveLabels), "Combined group and label selectors match effective labels.");
            var node = await new NodeRepository(db).GetAsync(a, TimeSpan.FromHours(1), CancellationToken.None);
            Check(node?.DisplayName == "Transcode A", "The friendly name stays separate from the heartbeat name.");

            var production = (await groups.ListAsync(CancellationToken.None)).Single(g => g.Name == "Production");
            Check(production.NodeIds.Contains(b), "A manual group lists its active members.");
            await using (var retire = db.CreateCommand("UPDATE cluster_nodes SET deleted_at=clock_timestamp() WHERE id=@id"))
            { retire.Parameters.AddWithValue("id", b); await retire.ExecuteNonQueryAsync(); }
            try
            {
                production = (await groups.ListAsync(CancellationToken.None)).Single(g => g.Name == "Production");
                Check(!production.NodeIds.Contains(b) && production.NodeIds.Contains(a), "A removed node no longer appears as a group member.");
            }
            finally
            {
                await using var restore = db.CreateCommand("UPDATE cluster_nodes SET deleted_at=NULL WHERE id=@id");
                restore.Parameters.AddWithValue("id", b); await restore.ExecuteNonQueryAsync();
            }

            var flowId = Guid.NewGuid();
            await using (var reference = db.CreateCommand("""
                INSERT INTO flows(id,current_version) VALUES(@flow,1);
                INSERT INTO flow_versions(flow_id,version,definition) VALUES(@flow,1,@definition)
                """))
            {
                reference.Parameters.AddWithValue("flow", flowId);
                reference.Parameters.AddWithValue("definition", SqlKind.Json, "{\"nodes\":[{\"configuration\":{\"targetGroupId\":\"" + production.Id + "\"}}]}");
                await reference.ExecuteNonQueryAsync();
            }
            Check(await groups.DeleteAsync(production.Id, CancellationToken.None) == "in-use", "A group targeted by a flow version cannot be deleted.");
            Check(await groups.GetAsync(production.Id, CancellationToken.None) is not null, "A refused deletion keeps the group.");
            var unused = (await groups.ListAsync(CancellationToken.None)).Single(g => g.Name == "Workers RAM");
            Check(await groups.DeleteAsync(unused.Id, CancellationToken.None) == "deleted", "An unused group is deleted.");
            Check(await groups.DeleteAsync(unused.Id, CancellationToken.None) == "missing", "Deleting a missing group reports it.");
        }
        private async Task<(Guid Id,RuntimeLease Lease,RuntimeContext Context)> EnqueueAsync(FlowDefinition flow,byte[]? content=null,bool directRead=false,bool clearUnassignedRunning=true)
        {
            // Scenarios share one schema and xUnit does not fix their order: executions another scenario left claimable must not be claimed here.
            var cleanup=clearUnassignedRunning?"""
                UPDATE executions SET state='cancelled'
                WHERE state='queued' OR state='running' AND id IN (
                  SELECT execution_id FROM execution_work
                  WHERE worker_node_id IS NULL AND lease_until IS NULL AND available_at<=clock_timestamp()
                )
                """:"UPDATE executions SET state='cancelled' WHERE state='queued'";
            await using(var leftovers=db.CreateCommand(cleanup)) await leftovers.ExecuteNonQueryAsync();
            Interlocked.Exchange(ref rejectNextCheckpoint, 0);
            Interlocked.Exchange(ref rejectNextFinish, 0);
            Interlocked.Exchange(ref artifactDownloads, 0);
            await ResetNodesAsync();
            var library=Guid.NewGuid(); var id=Guid.NewGuid();
            await using var command=db.CreateCommand("""
                INSERT INTO libraries(id,name) VALUES(@library,@name);
                INSERT INTO library_node_accesses(library_id,node_id,mode,local_path,can_read,can_write)
                  VALUES(@library,@a,'direct',@root,true,true);
                INSERT INTO library_node_accesses(library_id,node_id,mode,can_read,can_write,transfer_source_node_id)
                  VALUES(@library,@b,'transfer',true,true,@a),(@library,@c,'transfer',true,true,@a);
                """);
            command.Parameters.AddWithValue("library",library); command.Parameters.AddWithValue("name",library.ToString());
            command.Parameters.AddWithValue("root",directory); command.Parameters.AddWithValue("a",a); command.Parameters.AddWithValue("b",b); command.Parameters.AddWithValue("c",c);
            await command.ExecuteNonQueryAsync();
            if(directRead)
            {
                await using var capable=db.CreateCommand("UPDATE cluster_nodes SET capabilities=@caps WHERE id=@id");
                capable.Parameters.AddWithValue("id",a);
                capable.Parameters.AddWithValue("caps",SqlKind.Json,JsonSerializer.Serialize(Capabilities(true)
                    .Select(capability=>capability.ToolId=="ffmpeg"?capability with { Encoders=[..capability.Encoders??[],"ac3"] }:capability)
                    .Append(new NodeToolCapability(RuntimeSourceObservation.Capability,"1.0.0",["general"],[])),Json));
                await capable.ExecuteNonQueryAsync();
            }
            if (MediaIntegrityPolicy.IsExplicit(flow))
            {
                await using var latest = db.CreateCommand("UPDATE cluster_nodes SET capabilities=@caps WHERE id IN (@a,@b,@c)");
                latest.Parameters.AddWithValue("a",a); latest.Parameters.AddWithValue("b",b); latest.Parameters.AddWithValue("c",c);
                latest.Parameters.AddWithValue("caps",SqlKind.Json,JsonSerializer.Serialize(Capabilities(true)
                    .Select(cap => cap.ToolId == "plugin/probe" ? cap with { Version = "1.1.0" } : cap)
                    .Append(new NodeToolCapability(MediaIntegrityPolicy.Capability,"1.0.0",["general"],[])),Json));
                await latest.ExecuteNonQueryAsync();
            }
            Check(validator.Validate(flow).IsValid,JsonSerializer.Serialize(validator.Validate(flow),Json));
            Check(await runtime.EnqueueAsync(new(id,library,"source.mkv",flow.Id,flow.Version),flow,CancellationToken.None),"Enqueue fixture.");
            var lease=await runtime.ClaimAsync(a,CancellationToken.None)??throw new InvalidOperationException("The initial worker must claim.");
            if(directRead)
            {
                // Only this lease exercises the new capability; do not leak it into unrelated shared-fixture scenarios.
                await using var reset=db.CreateCommand("UPDATE cluster_nodes SET capabilities=@caps WHERE id=@id");
                reset.Parameters.AddWithValue("id",a); reset.Parameters.AddWithValue("caps",SqlKind.Json,JsonSerializer.Serialize(Capabilities(true),Json));
                await reset.ExecuteNonQueryAsync();
            }
            Check(lease.ExecutionId==id,"The expected execution was claimed.");
            content??=System.Text.Encoding.UTF8.GetBytes("fixture-original-media");
            var digest=Convert.ToHexStringLower(SHA256.HashData(content));
            if(directRead)
            {
                await File.WriteAllBytesAsync(Path.Combine(directory,"source.mkv"),content);
                return(id,lease,new("",".mkv",new Dictionary<string,JsonElement>(),null,null,[],DirectSource:new(content.Length,File.GetLastWriteTimeUtc(Path.Combine(directory,"source.mkv")).Ticks)));
            }
            Check(await artifacts.PutAsync(id,a,lease.Epoch,digest,new MemoryStream(content),true,CancellationToken.None),"Register original source.");
            return(id,lease,new(digest,".mkv",new Dictionary<string,JsonElement>(),null,null,[]));
        }
        private async Task<RuntimeCheckpoint> CompleteAsync(Guid id,Guid actor,RuntimeLease lease,string nodeId,RuntimeContext context)
        {
            var started=await runtime.StartStepAsync(id,actor,new(lease.Epoch,nodeId),CancellationToken.None)??throw new InvalidOperationException("Start expected step.");
            var node=lease.Flow.Nodes.Single(node=>node.Id==nodeId);
            var terminal=node.Component.ComponentId=="base-complete-flow";
            var output=terminal?null:"success";
            var next=lease.Flow.Connections.SingleOrDefault(edge=>edge.SourceNodeId==nodeId && edge.SourceOutputId==output)?.TargetNodeId;
            var selector=RuntimeHandoffPolicy.IsHandoff(node)?RuntimeHandoffPolicy.Resolve(node,lease.PluginConfigurations?.GetValueOrDefault("nodes")):null;
            var checkpoint=new RuntimeCheckpoint(nodeId,started.InvocationId,output,next,context,terminal?"unchanged":null,selector);
            var status=await runtime.RenewAsync(id,actor,lease.Epoch,CancellationToken.None)??throw new InvalidOperationException("Renew before checkpoint.");
            Check(await runtime.CompleteStepAsync(id,actor,new(lease.Epoch,Guid.NewGuid(),status.Revision,checkpoint),CancellationToken.None),"Checkpoint must commit.");
            return checkpoint;
        }
        private FlowDefinition RelayFlow(string mode="node",bool converted=false) => Chain([
            Node(registry,"entry","base-input-file"),
            Node(registry,"variable","base-variable",new(){["name"]=J("selected"),["value"]=J("42")}),
            .. converted?new[]{Node(registry,"convert","base-remux-media")}:Array.Empty<FlowNodeDefinition>(),
            Node(registry,"relay","nodes-handoff",new(){["mode"]=J(mode),["targetNodeId"]=J(b)}),
            Node(registry,"end","base-complete-flow")]);

        public async Task BackendRelayAsync(bool converted)
        {
            var fixture=await EnqueueAsync(RelayFlow(converted:converted)); var(id,lease,context)=fixture;
            await CompleteAsync(id,a,lease,"entry",context);
            context=context with { Variables=new Dictionary<string,JsonElement>{["selected"]=J(42)},BeforeMetadata=J(new{source="before"}),CurrentMetadata=J(new{source="before"}) };
            await CompleteAsync(id,a,lease,"variable",context);
            if(converted)
            {
                var candidate=System.Text.Encoding.UTF8.GetBytes("converted-media-fixture"); var sha=Convert.ToHexStringLower(SHA256.HashData(candidate));
                Check(await artifacts.PutAsync(id,a,lease.Epoch,sha,new MemoryStream(candidate),false,CancellationToken.None),"Register transformed artifact.");
                context=context with { CurrentSha256=sha,CurrentMetadata=J(new{source="after"}) };
                var report=System.Text.Encoding.UTF8.GetBytes("annex-report-fixture");var reportSha=Convert.ToHexStringLower(SHA256.HashData(report));
                Check(await artifacts.PutAsync(id,a,lease.Epoch,reportSha,new MemoryStream(report),false,CancellationToken.None),"Register an additional artifact.");
                context=context with { Variables=new Dictionary<string,JsonElement>(context.Variables){["artifact.export.report"]=J(new{sha256=reportSha,name="report.json"})} };
                await CompleteAsync(id,a,lease,"convert",context);
            }
            var cp=await CompleteAsync(id,a,lease,"relay",context);
            Check(await runtime.RenewAsync(id,a,lease.Epoch,CancellationToken.None) is null,"The previous lease is revoked.");
            Check(!await artifacts.AuthorizeAsync(id,a,lease.Epoch,CancellationToken.None),"An obsolete epoch cannot access artifacts.");
            Check(await runtime.StartStepAsync(id,a,new(lease.Epoch,"end"),CancellationToken.None) is null,"The previous worker cannot continue.");
            Check(await runtime.CompleteStepAsync(id,a,new(lease.Epoch,Guid.NewGuid(),lease.Revision,cp),CancellationToken.None),"An identical lost-response retry is acknowledged.");
            Check(!await runtime.CompleteStepAsync(id,a,new(lease.Epoch,Guid.NewGuid(),lease.Revision,cp with { NextNodeId="entry" }),CancellationToken.None),"A modified duplicate is rejected.");
            Check(await runtime.ClaimAsync(a,CancellationToken.None) is null,"The previous worker cannot reclaim its relay.");
            Check(await runtime.ClaimAsync(c,CancellationToken.None) is null,"A different worker cannot take a precise target's relay.");
            if(converted)
            {
                await using var direct=db.CreateCommand("UPDATE library_node_accesses SET mode='direct',local_path='/another/mount',transfer_source_node_id=NULL WHERE node_id=@node AND library_id=(SELECT library_id FROM execution_work WHERE execution_id=@id)");
                direct.Parameters.AddWithValue("id",id);direct.Parameters.AddWithValue("node",b);await direct.ExecuteNonQueryAsync();
            }
            await using(var pending=db.CreateCommand("SELECT lease_until='infinity'::timestamptz FROM media_reservations WHERE execution_id=@id"))
            { pending.Parameters.AddWithValue("id",id);Check((await pending.ScalarBoolAsync()),"The media reservation survives waiting."); }
            var received=await runtime.ClaimAsync(b,CancellationToken.None)??throw new InvalidOperationException("The receiver must claim.");
            Check(received.ExecutionId==id && received.Attempt==lease.Attempt && received.Epoch>lease.Epoch,"The same execution resumes with a new epoch.");
            Check(received.SourceNodeId==a,"The source node is preserved.");
            Check(received.Checkpoint?.NextNodeId=="end" && received.Checkpoint.Context.Variables["selected"].GetInt32()==42,"The context and resume point survive.");
            Check(received.Checkpoint!.Context.CurrentSha256==context.CurrentSha256,"The correct source or transformed file is referenced.");
            if(converted) Check(received.Checkpoint.Context.Variables.ContainsKey("artifact.export.report"),"Additional artifact references survive changing workers.");
            using(var client=Client(b)) await client.DownloadAsync(id,received.Epoch,context.CurrentSha256,Path.Combine(directory,id+".download"),CancellationToken.None);
            await CompleteAsync(id,b,received,"end",context);
            var status=await runtime.RenewAsync(id,b,received.Epoch,CancellationToken.None);
            Check(await new RuntimeTransferRepository(db).FinishAsync(id,b,new(received.Epoch,Guid.NewGuid(),status!.Revision,"succeeded","Finished relay fixture",null,null,context.BeforeMetadata,context.CurrentMetadata),CancellationToken.None),"The receiver finishes the same execution.");
        }
        public async Task DirectAccessConstraintAsync()
        {
            var directBlock = Node(registry, "directAccess", "nodes-require-direct-access");
            var relay = Node(registry, "relay", "nodes-handoff", new() { ["mode"] = J("node"), ["targetNodeId"] = J(b) });
            var flow = Chain(Node(registry, "entry", "base-input-file"), directBlock, relay,
                Node(registry, "end", "base-complete-flow"));
            var fixture = await EnqueueAsync(flow);
            var (id, lease, context) = fixture;
            Check(lease.ExecutionId == id, "Worker A (direct access) claimed the first segment.");
            await CompleteAsync(id, a, lease, "entry", context);
            await CompleteAsync(id, a, lease, "directAccess", context);
            await CompleteAsync(id, a, lease, "relay", context);
            var received = await runtime.ClaimAsync(b, CancellationToken.None);
            Check(received is not null && received.ExecutionId == id, "Worker B (transfer mode) claimed the second segment after handoff.");
        }
        public async Task DirectAccessRejectsTransferWorkerAsync()
        {
            await using(var leftovers=db.CreateCommand("UPDATE executions SET state='cancelled' WHERE state='queued'")) await leftovers.ExecuteNonQueryAsync();
            await ResetNodesAsync();
            var library = Guid.NewGuid(); var id = Guid.NewGuid();
            await using var command = db.CreateCommand("""
                INSERT INTO libraries(id,name) VALUES(@library,@name);
                INSERT INTO library_node_accesses(library_id,node_id,mode,local_path,can_read,can_write)
                  VALUES(@library,@a,'direct',@root,true,true);
                INSERT INTO library_node_accesses(library_id,node_id,mode,can_read,can_write,transfer_source_node_id)
                  VALUES(@library,@b,'transfer',true,true,@a);
                """);
            command.Parameters.AddWithValue("library", library); command.Parameters.AddWithValue("name", library.ToString());
            command.Parameters.AddWithValue("root", directory); command.Parameters.AddWithValue("a", a); command.Parameters.AddWithValue("b", b);
            await command.ExecuteNonQueryAsync();
            var directBlock = Node(registry, "directAccess", "nodes-require-direct-access");
            var flow = Chain(Node(registry, "entry", "base-input-file"), directBlock, Node(registry, "end", "base-complete-flow"));
            Check(await runtime.EnqueueAsync(new(id, library, "source.mkv", flow.Id, flow.Version), flow, CancellationToken.None), "Enqueue fixture.");
            var bClaim = await runtime.ClaimAsync(b, CancellationToken.None);
            Check(bClaim is null, "Worker B (transfer) must NOT be able to claim a segment requiring direct access.");
            var aClaim = await runtime.ClaimAsync(a, CancellationToken.None);
            Check(aClaim is not null && aClaim.ExecutionId == id, "Worker A (direct access) claims the segment requiring direct access.");
        }
        /// <summary>ADR 0024 : sans bloc de placement, le worker à accès direct fait le travail ; un worker par transfert n'intervient que si aucun worker direct n'en est capable.</summary>
        public async Task DefaultPlacementPrefersCapableDirectWorkerAsync()
        {
            await using(var leftovers=db.CreateCommand("UPDATE executions SET state='cancelled' WHERE state='queued'")) await leftovers.ExecuteNonQueryAsync();
            await ResetNodesAsync();
            var library = Guid.NewGuid(); var id = Guid.NewGuid();
            await using(var command = db.CreateCommand("""
                INSERT INTO libraries(id,name) VALUES(@library,@name);
                INSERT INTO library_node_accesses(library_id,node_id,mode,local_path,can_read,can_write)
                  VALUES(@library,@a,'direct',@root,true,true);
                INSERT INTO library_node_accesses(library_id,node_id,mode,can_read,can_write,transfer_source_node_id)
                  VALUES(@library,@b,'transfer',true,true,@a);
                """))
            {
                command.Parameters.AddWithValue("library", library); command.Parameters.AddWithValue("name", library.ToString());
                command.Parameters.AddWithValue("root", directory); command.Parameters.AddWithValue("a", a); command.Parameters.AddWithValue("b", b);
                await command.ExecuteNonQueryAsync();
            }
            var flow = Chain(Node(registry, "entry", "base-input-file"), Node(registry, "end", "base-complete-flow"));
            Check(await runtime.EnqueueAsync(new(id, library, "source.mkv", flow.Id, flow.Version), flow, CancellationToken.None), "Enqueue fixture.");
            Check(await runtime.ClaimAsync(b, CancellationToken.None) is null, "Le worker par transfert laisse la main au worker direct capable.");
            // Le worker direct perd ses capacités : le worker par transfert prend alors le relais, sans attente indéfinie.
            await using(var strip = db.CreateCommand("UPDATE cluster_nodes SET capabilities='[]'::jsonb WHERE id=@a")) { strip.Parameters.AddWithValue("a", a); await strip.ExecuteNonQueryAsync(); }
            var claim = await runtime.ClaimAsync(b, CancellationToken.None);
            await ResetNodesAsync();
            await using(var restore = db.CreateCommand("UPDATE cluster_nodes SET capabilities=@caps WHERE id=@a"))
            { restore.Parameters.AddWithValue("a", a); restore.Parameters.AddWithValue("caps", SqlKind.Json, JsonSerializer.Serialize(Capabilities(true), Json)); await restore.ExecuteNonQueryAsync(); }
            Check(claim is not null && claim.ExecutionId == id, "Sans worker direct capable, le worker par transfert réclame.");
        }
        public async Task ConcurrentRelayAsync()
        {
            var(id,lease,context)=await EnqueueAsync(RelayFlow("eligible"));
            await CompleteAsync(id,a,lease,"entry",context); await CompleteAsync(id,a,lease,"variable",context); await CompleteAsync(id,a,lease,"relay",context);
            var claims=await Task.WhenAll(runtime.ClaimAsync(b,CancellationToken.None),runtime.ClaimAsync(c,CancellationToken.None));
            Check(claims.Count(claim=>claim?.ExecutionId==id)==1,"Only one receiver can claim the relay concurrently.");
            Check(await executions.CancelAsync(id,CancellationToken.None),"Cancel concurrent fixture.");
            await ExpireAsync(id);
            await RecoverCancellationAsync(id);
        }
        public async Task IntegrityAndAvailabilityAsync()
        {
            var(id,lease,context)=await EnqueueAsync(RelayFlow());
            await CompleteAsync(id,a,lease,"entry",context);await CompleteAsync(id,a,lease,"variable",context);
            var started=await runtime.StartStepAsync(id,a,new(lease.Epoch,"relay"),CancellationToken.None);
            var status=await runtime.RenewAsync(id,a,lease.Epoch,CancellationToken.None);
            var missing=new string('a',64);
            var cp=new RuntimeCheckpoint("relay",started!.InvocationId,"success","end",context with { CurrentSha256=missing },null,new("node",b));
            Check(!await runtime.CompleteStepAsync(id,a,new(lease.Epoch,Guid.NewGuid(),status!.Revision,cp),CancellationToken.None),"A relay cannot commit an artifact that is not durable.");
            var corruptRejected=false;
            try { await artifacts.PutAsync(id,a,lease.Epoch,missing,new MemoryStream([1,2,3]),false,CancellationToken.None); }
            catch(InvalidDataException) { corruptRejected=true; }
            Check(corruptRejected,"A corrupt transfer must be rejected before registration.");
            await CompleteAsync(id,a,lease,"relay",context);
            await using(var offline=db.CreateCommand("UPDATE cluster_nodes SET last_seen_at=clock_timestamp()-interval '2 hours' WHERE id=@id"))
            { offline.Parameters.AddWithValue("id",b);await offline.ExecuteNonQueryAsync(); }
            Check(await runtime.ClaimAsync(b,CancellationToken.None) is null,"An offline target cannot claim.");
            await using(var busy=db.CreateCommand("UPDATE cluster_nodes SET last_seen_at=clock_timestamp(),max_concurrent_tasks=0 WHERE id=@id"))
            { busy.Parameters.AddWithValue("id",b);await busy.ExecuteNonQueryAsync(); }
            Check(await runtime.ClaimAsync(b,CancellationToken.None) is null,"Capacity limits still apply to a relay.");
            await using(var unsupported=db.CreateCommand("UPDATE cluster_nodes SET max_concurrent_tasks=10 WHERE id=@node; UPDATE library_node_accesses SET is_enabled=false WHERE node_id=@node AND library_id=(SELECT library_id FROM execution_work WHERE execution_id=@execution)"))
            { unsupported.Parameters.AddWithValue("node",b);unsupported.Parameters.AddWithValue("execution",id);await unsupported.ExecuteNonQueryAsync(); }
            Check(await runtime.ClaimAsync(b,CancellationToken.None) is null,"The receiver needs an enabled library access.");
            Check(await new NodeRepository(db).DeleteAsync(a,CancellationToken.None)=="busy","The original source cannot be removed during a waiting relay.");
            Check(await executions.CancelAsync(id,CancellationToken.None),"Cancel integrity fixture.");await RecoverCancellationAsync(id);
        }
        public async Task FrozenConfigurationAsync()
        {
            var plugin=registry.Plugins.Single(plugin=>plugin.Id=="nodes");
            var saved=await configurations.SaveGlobalAsync(plugin,new(0,new Dictionary<string,JsonElement>{["mode"]=J("node"),["targetNodeId"]=J(b)}),CancellationToken.None);
            Check(saved is not null,"Save defaults before the initial assignment.");
            var flow=RelayFlow();
            flow=flow with { Nodes=flow.Nodes.Select(node=>RuntimeHandoffPolicy.IsHandoff(node)?node with { Configuration=new Dictionary<string,JsonElement>{["mode"]=J("inherit")} }:node).ToArray() };
            var(id,lease,context)=await EnqueueAsync(flow);
            Check(await configurations.SaveGlobalAsync(plugin,new(saved!.Revision,new Dictionary<string,JsonElement>{["targetNodeId"]=J(c)}),CancellationToken.None) is not null,"Update global defaults while the flow is running.");
            await CompleteAsync(id,a,lease,"entry",context);await CompleteAsync(id,a,lease,"variable",context);
            var started=await runtime.StartStepAsync(id,a,new(lease.Epoch,"relay"),CancellationToken.None);
            var status=await runtime.RenewAsync(id,a,lease.Epoch,CancellationToken.None);
            var forged=new RuntimeCheckpoint("relay",started!.InvocationId,"success","end",context,null,new("node",c));
            Check(!await runtime.CompleteStepAsync(id,a,new(lease.Epoch,Guid.NewGuid(),status!.Revision,forged),CancellationToken.None),"The worker cannot replace the frozen target.");
            await CompleteAsync(id,a,lease,"relay",context);
            Check(await runtime.ClaimAsync(c,CancellationToken.None) is null,"A global edit must not change a running execution's target.");
            var received=await runtime.ClaimAsync(b,CancellationToken.None)??throw new InvalidOperationException("The original frozen target must receive the relay.");
            Check(received.PluginConfigurations!["nodes"]["targetNodeId"].GetGuid()==b,"The receiver retains the original configuration snapshot.");
            Check(await executions.CancelAsync(id,CancellationToken.None),"Cancel frozen fixture.");await ExpireAsync(id);await RecoverCancellationAsync(id);
            var staleGlobal=await configurations.SaveGlobalAsync(plugin,new(0,new Dictionary<string,JsonElement>{["targetNodeId"]=J(c)}),CancellationToken.None);
            Check(staleGlobal?.Revision==saved.Revision+2 && staleGlobal.Values["targetNodeId"].GetGuid()==c,"A stale global configuration save wins when it commits last.");
            var overrideFlow=Guid.NewGuid();
            var overrideSaved=await configurations.SaveFlowAsync(overrideFlow,plugin,new(0,new Dictionary<string,JsonElement>{["mode"]=J("eligible")}),CancellationToken.None);
            Check(overrideSaved is not null,"Create a flow-specific plugin override.");
            var overrideUpdated=await configurations.SaveFlowAsync(overrideFlow,plugin,new(overrideSaved!.Revision,new Dictionary<string,JsonElement>{["mode"]=J("node"),["targetNodeId"]=J(b)}),CancellationToken.None);
            Check(overrideUpdated?.Revision==overrideSaved.Revision+1,"An existing flow override can be updated.");
            var staleOverride=await configurations.SaveFlowAsync(overrideFlow,plugin,new(overrideSaved.Revision,new Dictionary<string,JsonElement>{["targetNodeId"]=J(c)}),CancellationToken.None);
            Check(staleOverride?.Revision==overrideUpdated!.Revision+1,"A stale editor save is accepted and becomes the latest committed configuration.");
            await using var connection=await db.OpenConnectionAsync();await using var transaction=await connection.BeginTransactionAsync();
            var effective=await configurations.ResolveEffectiveAsync(overrideFlow,["nodes"],connection,transaction,CancellationToken.None);
            Check(effective["nodes"]["targetNodeId"].GetGuid()==c,"The last committed flow override takes precedence over global settings.");
        }
        private async Task ExpireAsync(Guid id)
        {
            await using var command=db.CreateCommand("UPDATE execution_work SET lease_until=clock_timestamp()-interval '1 second' WHERE execution_id=@id; UPDATE media_reservations SET lease_until=clock_timestamp()-interval '1 second' WHERE execution_id=@id");
            command.Parameters.AddWithValue("id",id);await command.ExecuteNonQueryAsync();
        }
        private async Task RecoverCancellationAsync(Guid id)
        {
            var service=new RuntimeRecoveryService(db,NullLogger<RuntimeRecoveryService>.Instance);
            await service.ProcessOnceAsync(CancellationToken.None);
            Check((await executions.GetAsync(id,CancellationToken.None))!.State=="cancelled","Cancellation recovery must finish.");
            await using var command=db.CreateCommand("SELECT count(*) FROM media_reservations WHERE execution_id=@id");command.Parameters.AddWithValue("id",id);
            Check((long)(await command.ExecuteScalarAsync())! ==0,"Cancellation releases the reservation.");
        }
        private RuntimeFlowRunner Runner(string name,out NodeAgentOptions options,IToolCapabilityProbe? capabilities=null)
        {
            options=NodeAgentOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Node:DataDirectory"]=Path.Combine(directory,name) }).Build());
            var tools=new ProcessToolRunner();var scanner=new MediaScanner(tools);
            var probe=capabilities??new Probe();
            return new(options,scanner,new MediaActions(tools,scanner,probe),tools,new PluginProcessHost(tools,new ConfigurationBuilder().Build(),options),new PublicationGuardRunner(new NoGuardInvoker(),NullLogger<PublicationGuardRunner>.Instance),probe,localPublicationArtifacts,NullLogger<RuntimeFlowRunner>.Instance);
        }
        public async Task ConflictDuringCheckpointFailsExecutionAsync()
        {
            var flow=Chain(Node(registry,"entry","base-input-file"),Node(registry,"end","base-complete-flow"));
            var(id,lease,_)=await EnqueueAsync(flow);
            Interlocked.Exchange(ref rejectNextCheckpoint,1);
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var client=Client(a);
            await Runner("conflict-checkpoint",out _).RunAsync(lease,client,deadline.Token);

            var result=await executions.GetAsync(id,CancellationToken.None)??throw new InvalidOperationException("The execution must remain observable.");
            Check(result.State=="failed",$"A rejected checkpoint with a valid lease must finish as failed, not wait for lease expiry and retry. State: {result.State}, Detail: {result.Detail}");
            Check(result.Detail?.Contains("refusé",StringComparison.OrdinalIgnoreCase)==true,"The failed execution records the rejected cluster operation.");
            await using(var reservation=db.CreateCommand("SELECT count(*) FROM media_reservations WHERE execution_id=@id"))
            {
                reservation.Parameters.AddWithValue("id",id);
                Check((long)(await reservation.ExecuteScalarAsync())! ==0,"A terminal contract rejection releases the media reservation.");
            }
            Check(await runtime.ClaimAsync(b,CancellationToken.None) is null,"Another worker cannot retry the rejected execution.");
        }
        public async Task ConflictDuringFinishFailsExecutionAsync()
        {
            var tools=new ProcessToolRunner();
            var source=Path.Combine(directory,"conflict-source.mkv");
            await tools.CheckedAsync("ffmpeg",["-y","-nostdin","-v","error","-f","lavfi","-i","testsrc2=size=64x64:rate=5","-t","1","-c:v","libx264",source],directory,CancellationToken.None);
            var librarySource=Path.Combine(directory,"source.mkv");
            File.Copy(source,librarySource,true);
            byte[] originalSha;
            await using(var original=File.OpenRead(librarySource)) originalSha=await System.Security.Cryptography.SHA256.HashDataAsync(original);
            var flow=Chain(Node(registry,"entry","base-input-file"),Node(registry,"end","base-complete-flow"));
            var(id,lease,_)=await EnqueueAsync(flow,await File.ReadAllBytesAsync(source));
            Interlocked.Exchange(ref rejectNextFinish,1);
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var client=Client(a);
            await Runner("conflict-finish",out _).RunAsync(lease,client,deadline.Token);

            var result=await executions.GetAsync(id,CancellationToken.None)??throw new InvalidOperationException("The execution must remain observable.");
            Check(result.State=="failed",$"A rejected final completion with a valid lease must finish as failed, not wait for lease expiry and retry. State: {result.State}, Detail: {result.Detail}");
            Check(result.Detail?.Contains("refusé",StringComparison.OrdinalIgnoreCase)==true,"The failed execution records the rejected cluster operation.");
            await using(var reservation=db.CreateCommand("SELECT count(*) FROM media_reservations WHERE execution_id=@id"))
            {
                reservation.Parameters.AddWithValue("id",id);
                Check((long)(await reservation.ExecuteScalarAsync())! ==0,"A terminal completion rejection releases the media reservation.");
            }
            await using(var preserved=File.OpenRead(librarySource))
                Check(Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(preserved))==Convert.ToHexStringLower(originalSha),"A rejected completion leaves the library source unchanged.");
            Check(await runtime.ClaimAsync(b,CancellationToken.None) is null,"Another worker cannot retry the rejected execution.");
        }
        public async Task LibraryAccessBlockBranchesOnDirectAccessAsync()
        {
            var advertised=Capabilities(true).Select(capability=>capability.ToolId=="ffmpeg"
                ? capability with { AccelerationTypes=[..capability.AccelerationTypes,"qsv"],Encoders=[..capability.Encoders??[],"hevc_qsv"],
                    EncoderChecks=[..capability.EncoderChecks??[],new NodeEncoderCapability("hevc_qsv","usable")] }
                : capability).ToArray();
            await using(var announce=db.CreateCommand("UPDATE cluster_nodes SET capabilities=@caps"))
            { announce.Parameters.AddWithValue("caps",SqlKind.Json,JsonSerializer.Serialize(advertised,Json)); await announce.ExecuteNonQueryAsync(); }
            foreach(var direct in new[]{true,false})
            {
                var flow=new FlowDefinition(1,Guid.NewGuid(),1,"Library access","video",
                    [Node(registry,"entry","base-input-file"),Node(registry,"access","nodes-library-access"),
                     Node(registry,"local","base-complete-flow",new(){["result"]=J("success")}),
                     Node(registry,"elsewhere","base-complete-flow",new(){["result"]=J("failed"),["message"]=J("Fichier sans accès direct sur ce nœud.")})],
                    [new("a","entry","success","access"),new("b","access","yes","local"),new("c","access","no","elsewhere")]);
                var(id,lease,_)=await EnqueueAsync(flow);
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20)); using var client=Client(a);
                // Accès par transfert : le worker reçoit une copie, sans chemin local de la bibliothèque.
                await Runner("library-access-"+direct,out _).RunAsync(direct?lease:lease with { SourceRoot=null },client,deadline.Token);
                // Le média fictif échoue ensuite au scan : seul le checkpoint du bloc, écrit avant, compte ici.
                Check(await ScalarAsync<string>("SELECT checkpoint->>'outputId' FROM execution_steps WHERE execution_id=@id AND node_id='access' AND completed_at IS NOT NULL",("id",id))==(direct?"yes":"no"),
                    direct?"A node that mounts the library follows « Oui ».":"A node without direct access follows « Non ».");
            }
            foreach(var capable in new[]{true,false})
            {
                var gated=HardwareProfileFlow(registry,true);
                var(id,lease,_)=await EnqueueAsync(gated);
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20)); using var client=Client(a);
                await Runner("library-access-encoder-"+capable,out _,capable?new FixedProbe("hevc_qsv"):new FixedProbe("libx265")).RunAsync(lease,client,deadline.Token);
                Check(await ScalarAsync<string>("SELECT checkpoint->>'outputId' FROM execution_steps WHERE execution_id=@id AND node_id='access' AND completed_at IS NOT NULL",("id",id))==(capable?"yes":"no"),
                    capable?"A direct-access node with the profile encoder follows « Oui ».":"A direct-access node without the profile encoder follows « Non ».");
            }
            Console.WriteLine("PASS: the library access block branches on direct access.");
        }
        public async Task EarlyRunnerAsync()
        {
            var(id,lease,context)=await EnqueueAsync(RelayFlow());
            var runner=Runner("early-a",out var options);
            using var client=Client(a);using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await runner.RunAsync(lease,client,deadline.Token);
            Check(!Directory.EnumerateFiles(options.DataDirectory,"*",SearchOption.AllDirectories).Any(),"An early relay must not materialize media on the first worker.");
            var received=await runtime.ClaimAsync(b,CancellationToken.None)??throw new InvalidOperationException("The early runner must yield.");
            Check(received.Checkpoint?.Context.Variables["selected"].GetInt32()==42,"The runner persists variables before the relay.");
            await ExpireAsync(id);
            Check(await runtime.ClaimAsync(b,CancellationToken.None) is null,"An interrupted receiver attempt is not reassigned before the recovery service hands it back.");
            var recovery=new RuntimeRecoveryService(db,NullLogger<RuntimeRecoveryService>.Instance);
            await recovery.ProcessOnceAsync(CancellationToken.None);
            var again=await runtime.ClaimAsync(b,CancellationToken.None)??throw new InvalidOperationException("The relay destination must resume the disconnected receiver's attempt.");
            Check(again.ExecutionId==id && again.Checkpoint?.Context.Variables["selected"].GetInt32()==42,"The resumed attempt keeps the relay checkpoint.");
            Check(await executions.CancelAsync(id,CancellationToken.None),"Clean up the resumed relay.");await ExpireAsync(id);await RecoverCancellationAsync(id);
        }
        public async Task CancellationAsync()
        {
            var(id,lease,context)=await EnqueueAsync(RelayFlow());
            await CompleteAsync(id,a,lease,"entry",context);await CompleteAsync(id,a,lease,"variable",context);await CompleteAsync(id,a,lease,"relay",context);
            Check(await executions.CancelAsync(id,CancellationToken.None),"A waiting relay can be cancelled.");await RecoverCancellationAsync(id);
        }
        public async Task ConditionalRelayAsync(string outcome)
        {
            var tree=new ConditionGroup("all",[new ConditionRule("variable.number","eq",J(outcome=="no"?99:42),outcome=="error"?"missing":"selected"),
                new ConditionGroup("any",[new ConditionRule("source.nodeId","eq",J(a.ToString("D"))),new ConditionRule("library.id","exists")])]);
            var flow=RelayFlow();
            var condition=Node(registry,"condition","conditions-match",new(){["rules"]=ConditionTree.Serialize(tree)});
            flow=flow with { Nodes=[..flow.Nodes,condition],Connections=[..flow.Connections.Where(edge=>edge.SourceNodeId!="variable"),
                new("to-condition","variable","success","condition"),new("conditional-relay","condition",outcome,"relay")] };
            var(id,lease,_)=await EnqueueAsync(flow);
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));using var client=Client(a);
            var runner=Runner("condition-"+outcome,out var options);await runner.RunAsync(lease,client,deadline.Token);
            await using var read=db.CreateCommand("SELECT checkpoint->>'outputId' FROM execution_steps WHERE execution_id=@id AND node_id='condition'");
            read.Parameters.AddWithValue("id",id);Check((string?)await read.ExecuteScalarAsync()==outcome,"A native condition must take its declared "+outcome+" output.");
            Check(!Directory.EnumerateFiles(options.DataDirectory,"*",SearchOption.AllDirectories).Any(),"Variable and origin conditions must not download or scan media before the relay.");
            var received=await runtime.ClaimAsync(b,CancellationToken.None);Check(received?.ExecutionId==id,"The condition branch hands off the same execution.");
            Check(await executions.CancelAsync(id,CancellationToken.None),"Cancel condition fixture.");await ExpireAsync(id);await RecoverCancellationAsync(id);
        }
        public async Task MediaRelayAsync(bool planned,bool replace=false,bool strictBuilder=false)
        {
            var tools=new ProcessToolRunner();var scanner=new MediaScanner(tools);
            var source=Path.Combine(directory,"source.mkv");
            await tools.CheckedAsync("ffmpeg",["-y","-nostdin","-v","error","-f","lavfi","-i","testsrc2=size=64x64:rate=5","-t","1","-c:v","libx264",source],directory,CancellationToken.None);
            var flow=Chain([Node(registry,"entry","base-input-file"),
                .. planned?new[]{Node(registry,"prepare",strictBuilder?FfmpegBuilderPolicy.Start:"base-prepare-processing")}:Array.Empty<FlowNodeDefinition>(),
                Node(registry,"encode","base-transcode-video",new(){["processingMode"]=J(planned?"plan":"direct"),["videoCodec"]=J("hevc"),["preset"]=J("fast")}),
                Node(registry,"convert","base-remux-media",new(){["container"]=J("mp4"),["processingMode"]=J(planned?"plan":"direct")}),
                .. planned?Array.Empty<FlowNodeDefinition>():new[]{Node(registry,"condition","conditions-match",new(){["rules"]=ConditionTree.Serialize(new ConditionGroup("all",[
                    new ConditionRule("video.codec","eq",J("hevc")),new ConditionRule("video.codec","eq",J("h264"),Scope:"source")]))})},
                Node(registry,"relay","nodes-handoff",new(){["mode"]=J("node"),["targetNodeId"]=J(b)}),
                .. planned?new[]{Node(registry,"execute",strictBuilder?FfmpegBuilderPolicy.Execute:"base-execute-processing")}:Array.Empty<FlowNodeDefinition>(),
                Node(registry,"end","base-complete-flow",new(){["result"]=J("success")})]);
            if(!planned) flow=flow with { Connections=flow.Connections.Select(edge=>edge.SourceNodeId=="condition"?edge with { SourceOutputId="yes" }:edge).ToArray() };
            var(id,lease,context)=await EnqueueAsync(flow,await File.ReadAllBytesAsync(source));
            if(replace) Check(await new SourceReplacementRepository(db).SavePolicyAsync(lease.LibraryId!.Value,new(true,1,0),CancellationToken.None),"Enable source replacement for the media library.");
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(90));
            using(var client=Client(a)) await Runner("media-a",out _).RunAsync(lease,client,deadline.Token);
            var received=await runtime.ClaimAsync(b,CancellationToken.None)??throw new InvalidOperationException("Converted media must yield.");
            Check(planned?received.Checkpoint!.Context.CurrentSha256==context.CurrentSha256:received.Checkpoint!.Context.CurrentSha256!=context.CurrentSha256,
                "The relay references the original for an unexecuted plan, and the converted file after execution.");
            Check(!planned || received.Checkpoint!.Context.Plan.Count==2,"Unexecuted plan steps survive the relay.");
            Check(received.Checkpoint.Context.BeforeMetadata is not null && received.Checkpoint.Context.CurrentMetadata is not null,"Both scans survive conversion and relay.");
            var candidate=Path.Combine(directory,planned?"received-original.mkv":"received.mp4");
            using(var client=Client(b)) await client.DownloadAsync(id,received.Epoch,received.Checkpoint.Context.CurrentSha256,candidate,deadline.Token);
            await scanner.ValidateAsync(candidate,"video",received.Checkpoint.Context.BeforeMetadata,deadline.Token);
            using var receiver=Client(b);var run=Runner("media-b",out _).RunAsync(received,receiver,deadline.Token);
            RuntimeTransfer? publication=null;
            var transfers=new RuntimeTransferRepository(db);
            while(publication is null)
            {
                publication=(await transfers.ListAsync(a,deadline.Token)).SingleOrDefault(item=>item.ExecutionId==id && item.Publish);
                if(publication is not null) break;
                if(run.IsCompleted)
                {
                    // Do not treat worker completion as "no publication": the transfer row can lag the task.
                    await run;
                    publication=(await transfers.ListAsync(a,deadline.Token)).SingleOrDefault(item=>item.ExecutionId==id && item.Publish);
                    for(var grace=0;publication is null && grace<50;grace++)
                    {
                        await Task.Delay(100,deadline.Token);
                        publication=(await transfers.ListAsync(a,deadline.Token)).SingleOrDefault(item=>item.ExecutionId==id && item.Publish);
                    }
                    break;
                }
                await Task.Delay(100,deadline.Token);
            }
            Check(publication is not null,"The final worker requests publication from the original source node.");
            Runner("source-agent",out var sourceOptions);
            Directory.CreateDirectory(sourceOptions.DataDirectory);
            await File.WriteAllTextAsync(Path.Combine(sourceOptions.DataDirectory,"node-identity.json"),
                JsonSerializer.Serialize(new WorkerIdentityState(a,"fixture","fixture",app.Urls.Single()+"/",Token,true),Json),deadline.Token);
            using var sourceService=new RuntimeTransferService(sourceOptions,new WorkerIdentityStore(sourceOptions),scanner,new PublicationGuardRunner(new NoGuardInvoker(),NullLogger<PublicationGuardRunner>.Instance),localPublicationArtifacts,NullLogger<RuntimeTransferService>.Instance);
            await sourceService.StartAsync(deadline.Token);
            await run;
            await sourceService.StopAsync(CancellationToken.None);
            Check((await executions.GetAsync(id,deadline.Token))!.State=="succeeded","The complete two-worker media execution succeeds.");
            var published=Path.Combine(directory,".encoding-results",id.ToString("D"),"result.mp4");
            Check(await SafeMediaPaths.DigestAsync(published,deadline.Token)==publication!.ResultSha256,"The source service publishes the exact validated artifact.");
            var finalMetadata=await scanner.ScanAsync(published,deadline.Token);
            Check(finalMetadata.GetProperty("streams").EnumerateArray().Any(stream=>stream.GetProperty("codec_name").GetString()=="hevc"),"The published video was actually encoded to H.265.");
            if(replace)
            {
                var replacement=await new SourceReplacementRepository(db).GetAsync(id,deadline.Token);
                Check(replacement is { State: "replaced", FinalPath: "source.mp4" } && !File.Exists(source),"The agent replaced the source with the validated .mp4 and recorded the replacement.");
                Check(await SafeMediaPaths.DigestAsync(Path.Combine(directory,"source.mp4"),deadline.Token)==publication.ResultSha256,"The library now holds exactly the validated result.");
                Check(await SafeMediaPaths.DigestAsync(Path.Combine(directory,replacement!.BackupPath!),deadline.Token)==context.CurrentSha256,"The original is kept in the backup during the grace period.");
                var task=(await new SourceReplacementRepository(db).TasksAsync(a,deadline.Token)).SingleOrDefault(item=>item.ExecutionId==id);
                Check(task is null && await new SourceReplacementRepository(db).RequestRollbackAsync(id,deadline.Token)=="ok","A rollback is requested during the grace period.");
                var restoreTask=(await new SourceReplacementRepository(db).TasksAsync(a,deadline.Token)).Single(item=>item.ExecutionId==id);
                var restored=await SourceReplacer.RollbackAsync(restoreTask.Root,restoreTask,deadline.Token);
                Check(restored.Status=="restored" && Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(source)))==context.CurrentSha256 && !File.Exists(Path.Combine(directory,"source.mp4")),"The restored library file is byte-identical to the original.");
                Console.WriteLine("PASS: real agent replaces the source after H.265 validation, then restores it byte for byte.");
                return;
            }
            Check(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(source)))==context.CurrentSha256,"The source media remains intact.");
        }
        public async ValueTask DisposeAsync()
        {
            if(app is not null) await app.DisposeAsync();
            sourceWriteIdentity?.Dispose();
            if(testDb is not null) await testDb.DisposeAsync();
            if(Directory.Exists(directory)) Directory.Delete(directory,true);
        }
    }
}
