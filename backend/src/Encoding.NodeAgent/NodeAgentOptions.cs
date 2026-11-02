namespace Encoding.NodeAgent;

public sealed record NodeAgentOptions(
    string Name,
    IReadOnlyList<Encoding.Contracts.Nodes.NodeRole> Roles,
    int MaxConcurrentTasks,
    string DataDirectory,
    string? ClusterApiBaseUrl,
    bool AutomaticEnrollmentEnabled,
    TimeSpan HeartbeatInterval,
    int LocalApiPort,
    string? LocalEnrollmentSecretFile = null,
    string? DisplayName = null)
{
    public static NodeAgentOptions FromConfiguration(IConfiguration configuration)
    {
        var name = configuration["Node:Name"]?.Trim();
        if (string.IsNullOrWhiteSpace(name)) name = Environment.MachineName;
        if (name.Length > 128) throw new InvalidOperationException("Node:Name ne peut pas dépasser 128 caractères.");

        var roles = (configuration["Node:Roles"] ?? "worker")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Enum.TryParse<Encoding.Contracts.Nodes.NodeRole>(value, true, out var role)
                ? role
                : throw new InvalidOperationException($"Rôle de nœud inconnu : {value}."))
            .Distinct()
            .ToArray();
        if (roles.Length == 0) throw new InvalidOperationException("Node:Roles doit déclarer au moins un rôle.");

        var maxConcurrentTasks = configuration.GetValue("Node:MaxConcurrentTasks", 1);
        if (maxConcurrentTasks is < 0 or > 1024) throw new InvalidOperationException("Node:MaxConcurrentTasks doit être compris entre 0 et 1024.");
        var intervalSeconds = configuration.GetValue("Node:HeartbeatIntervalSeconds", 10);
        if (intervalSeconds is < 1 or > 300) throw new InvalidOperationException("Node:HeartbeatIntervalSeconds doit être compris entre 1 et 300.");
        var localApiPort = configuration.GetValue("WorkerUi:Port", 8081);
        if (localApiPort is < 1 or > 65535) throw new InvalidOperationException("WorkerUi:Port est invalide.");

        return new NodeAgentOptions(
            name,
            roles,
            maxConcurrentTasks,
            Path.GetFullPath(configuration["Node:DataDirectory"] ?? ".encoding-node"),
            NormalizeOptionalUrl(configuration["Cluster:ApiBaseUrl"]),
            configuration.GetValue("Worker:AutoAcceptEnrollment", false),
            TimeSpan.FromSeconds(intervalSeconds),
            localApiPort,
            string.IsNullOrWhiteSpace(configuration[Encoding.Core.Nodes.LocalEnrollment.SecretFileSetting]) ? null : configuration[Encoding.Core.Nodes.LocalEnrollment.SecretFileSetting],
            string.IsNullOrWhiteSpace(configuration["Node:DisplayName"]) ? null : configuration["Node:DisplayName"]!.Trim());
    }

    private static string? NormalizeOptionalUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("Cluster:ApiBaseUrl doit être une URL HTTP ou HTTPS absolue.");
        return uri.AbsoluteUri.EndsWith('/') ? uri.AbsoluteUri : uri.AbsoluteUri + '/';
    }
}
