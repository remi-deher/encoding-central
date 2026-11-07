using Encoding.Core.Workers;
using Microsoft.Extensions.Configuration;

namespace Encoding.Launcher.Tests;

internal static class AgentEnvironmentTests
{
    public static Task RunAsync()
    {
        static IConfiguration Configure(string? url) => new ConfigurationBuilder()
            .AddInMemoryCollection(url is null ? new Dictionary<string, string?>() : new Dictionary<string, string?> { ["Central:ApiBaseUrl"] = url }).Build();

        var secretFile = "/var/lib/encoding-node/local-enrollment.secret";
        var environment = AgentEnvironment.Build("/var/lib/encoding-node", secretFile, 8081, 8080);
        Check.That(environment["Central__ApiBaseUrl"] == "http://127.0.0.1:8080", "Le worker intégré rejoint le serveur local en boucle locale.");
        Check.That(environment["Worker__LocalEnrollmentSecretFile"] == secretFile && environment["Worker__Roles"] == "server,worker", "Le secret local et les rôles du worker intégré sont transmis.");
        Check.That(AgentEnvironment.Build("/d", secretFile, 9001, 9000)["Central__ApiBaseUrl"] == "http://127.0.0.1:9000", "L’adresse loopback suit le port de l’API.");
        Check.That(LocalEnrollment.IsLoopback(environment["Central__ApiBaseUrl"]), "L’adresse imposée est reconnue comme locale par l’enrôlement.");

        Check.That(!AgentEnvironment.OverridesOperatorUrl(Configure(null), 8080), "Sans Central:ApiBaseUrl, rien n’est ignoré.");
        Check.That(!AgentEnvironment.OverridesOperatorUrl(Configure("  "), 8080), "Une valeur vide n’est pas une adresse d’opérateur.");
        Check.That(!AgentEnvironment.OverridesOperatorUrl(Configure("http://127.0.0.1:8080"), 8080), "La boucle locale attendue ne déclenche aucun avertissement.");
        Check.That(AgentEnvironment.OverridesOperatorUrl(Configure("http://server:8080"), 8080), "Une adresse non loopback est signalée comme ignorée.");
        Console.WriteLine("PASS: worker intégré toujours enrôlé en boucle locale.");
        return Task.CompletedTask;
    }
}
