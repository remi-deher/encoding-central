using Microsoft.Extensions.Configuration;

namespace Encoding.Launcher;

/// <summary>Environnement du worker intégré au central. Il rejoint toujours le serveur de son conteneur en boucle locale : son enrôlement par secret local exige cette adresse exacte.</summary>
internal static class AgentEnvironment
{
    public static string LoopbackUrl(int apiPort) => $"http://127.0.0.1:{apiPort}";

    /// <summary>Vrai si l'opérateur a fourni une adresse de central différente de la boucle locale ; elle est alors ignorée pour l'agent intégré.</summary>
    public static bool OverridesOperatorUrl(IConfiguration configuration, int apiPort) =>
        !string.IsNullOrWhiteSpace(configuration["Central:ApiBaseUrl"])
        && !string.Equals(configuration["Central:ApiBaseUrl"], LoopbackUrl(apiPort), StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, string> Build(string dataDirectory, string localEnrollmentSecretFile, int agentPort, int apiPort) =>
        new Dictionary<string, string>
        {
            ["ASPNETCORE_URLS"] = $"http://+:{agentPort}",
            ["Worker__Roles"] = "server,worker",
            ["Worker__DataDirectory"] = dataDirectory,
            ["Worker__LocalEnrollmentSecretFile"] = localEnrollmentSecretFile,
            // Toujours imposée, même si l'opérateur a défini Central:ApiBaseUrl : sans l'adresse loopback exacte, le worker intégré ne s'enrôle jamais.
            ["Central__ApiBaseUrl"] = LoopbackUrl(apiPort),
        };
}
