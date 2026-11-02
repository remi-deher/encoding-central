using Encoding.Core.Nodes;
using Encoding.Launcher;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
var controlPort = configuration.GetValue("Launcher:ControlPort", 8082);
builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenLocalhost(controlPort));

var dataDirectory = Path.GetFullPath(configuration["Node:DataDirectory"] ?? "/var/lib/encoding-node");
var store = new NodeModeStore(dataDirectory, configuration["Node:Roles"]);
var controlUrl = $"http://127.0.0.1:{controlPort}/";
var apiPort = configuration.GetValue("Launcher:ApiPort", 8080);
var agentPort = configuration.GetValue("WorkerUi:Port", 8081);
var localEnrollmentSecretFile = Path.Combine(dataDirectory, LocalEnrollment.FileName);
LocalEnrollment.Create(localEnrollmentSecretFile);
var home = configuration["Launcher:Home"] ?? "/app";

IReadOnlyDictionary<string, string> ServerEnvironment(NodeMode _)
{
    var environment = new Dictionary<string, string>
    {
        ["ASPNETCORE_URLS"] = $"http://+:{apiPort}",
        ["Node__DataDirectory"] = dataDirectory,
        ["Launcher__ControlUrl"] = controlUrl,
        ["Node__LocalEnrollmentSecretFile"] = localEnrollmentSecretFile,
    };
    if (string.IsNullOrWhiteSpace(configuration["PluginSettings:KeyDirectory"]))
        environment["PluginSettings__KeyDirectory"] = Path.Combine(dataDirectory, "plugin-keys");
    if (string.IsNullOrWhiteSpace(configuration["FlowBackups:Directory"]))
        environment["FlowBackups__Directory"] = Path.Combine(dataDirectory, "flow-backups");
    if (string.IsNullOrWhiteSpace(configuration["Runtime:ArtifactDirectory"]))
        environment["Runtime__ArtifactDirectory"] = Path.Combine(dataDirectory, "runtime-artifacts");
    return environment;
}

IReadOnlyDictionary<string, string> AgentEnvironment(NodeMode mode)
{
    var environment = new Dictionary<string, string>
    {
        ["ASPNETCORE_URLS"] = $"http://+:{agentPort}",
        ["Node__Roles"] = mode.ToRoles(),
        ["Node__DataDirectory"] = dataDirectory,
        ["Launcher__ControlUrl"] = controlUrl,
        ["Node__LocalEnrollmentSecretFile"] = localEnrollmentSecretFile,
    };
    // Avec la fonction serveur locale, l’agent rejoint son propre serveur sauf si l’opérateur a désigné un autre cluster.
    if (mode.Server && string.IsNullOrWhiteSpace(configuration["Cluster:ApiBaseUrl"]))
        environment["Cluster__ApiBaseUrl"] = $"http://127.0.0.1:{apiPort}";
    return environment;
}

var supervisor = new ProcessSupervisor(
    new SystemProcess("serveur", Path.Combine(home, "api"), "Encoding.Api.dll"),
    new SystemProcess("agent", Path.Combine(home, "agent"), "Encoding.NodeAgent.dll"),
    ServerEnvironment, AgentEnvironment, TimeSpan.FromSeconds(45), () => DateTimeOffset.UtcNow);
builder.Services.AddSingleton(supervisor);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton<LauncherService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<LauncherService>());

var app = builder.Build();
app.MapGet("/mode", (ProcessSupervisor processes) => Results.Ok(processes.Mode));
app.MapPut("/mode", (NodeMode requested, NodeModeStore modes, LauncherService launcher) =>
{
    NodeMode mode;
    try { mode = NodeMode.Validate(requested); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { title = exception.Message }); }
    modes.Save(mode);
    launcher.ScheduleApply(mode);
    return Results.Ok(mode);
});
await app.RunAsync();
