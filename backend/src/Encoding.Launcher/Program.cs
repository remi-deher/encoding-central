using Encoding.Core.Workers;
using Encoding.Launcher;

var builder = Host.CreateApplicationBuilder(args);
var configuration = builder.Configuration;

var dataDirectory = Path.GetFullPath(configuration["Worker:DataDirectory"] ?? "/var/lib/encoding-node");
var apiPort = configuration.GetValue("Launcher:ApiPort", 8080);
var agentPort = configuration.GetValue("WorkerUi:Port", 8081);
var localEnrollmentSecretFile = Path.Combine(dataDirectory, LocalEnrollment.FileName);
LocalEnrollment.Create(localEnrollmentSecretFile);
var home = configuration["Launcher:Home"] ?? "/app";

IReadOnlyDictionary<string, string> ServerEnvironment()
{
    var environment = new Dictionary<string, string>
    {
        ["ASPNETCORE_URLS"] = $"http://+:{apiPort}",
        ["Worker__DataDirectory"] = dataDirectory,
        ["Worker__LocalEnrollmentSecretFile"] = localEnrollmentSecretFile,
    };
    if (string.IsNullOrWhiteSpace(configuration["PluginSettings:KeyDirectory"]))
        environment["PluginSettings__KeyDirectory"] = Path.Combine(dataDirectory, "plugin-keys");
    if (string.IsNullOrWhiteSpace(configuration["FlowBackups:Directory"]))
        environment["FlowBackups__Directory"] = Path.Combine(dataDirectory, "flow-backups");
    if (string.IsNullOrWhiteSpace(configuration["Runtime:ArtifactDirectory"]))
        environment["Runtime__ArtifactDirectory"] = Path.Combine(dataDirectory, "runtime-artifacts");
    return environment;
}

// Le worker intégré au central rejoint toujours le serveur du même conteneur, sauf si l’opérateur a désigné une autre adresse.
IReadOnlyDictionary<string, string> AgentEnvironment()
{
    var environment = new Dictionary<string, string>
    {
        ["ASPNETCORE_URLS"] = $"http://+:{agentPort}",
        ["Worker__Roles"] = "server,worker",
        ["Worker__DataDirectory"] = dataDirectory,
        ["Worker__LocalEnrollmentSecretFile"] = localEnrollmentSecretFile,
    };
    if (string.IsNullOrWhiteSpace(configuration["Central:ApiBaseUrl"]))
        environment["Central__ApiBaseUrl"] = $"http://127.0.0.1:{apiPort}";
    return environment;
}

builder.Services.AddSingleton(new ProcessSupervisor(
    new SystemProcess("serveur", Path.Combine(home, "api"), "Encoding.Api.dll"),
    new SystemProcess("agent", Path.Combine(home, "agent"), "Encoding.WorkerAgent.dll"),
    ServerEnvironment, AgentEnvironment, TimeSpan.FromSeconds(45), () => DateTimeOffset.UtcNow));
builder.Services.AddHostedService<LauncherService>();

await builder.Build().RunAsync();
