namespace Encoding.WorkerAgent;

public sealed record WorkerAgentOptions(
    string Name,
    IReadOnlyList<Encoding.Contracts.Workers.NodeRole> Roles,
    int MaxConcurrentTasks,
    string DataDirectory,
    string? ClusterApiBaseUrl,
    bool AutomaticEnrollmentEnabled,
    TimeSpan HeartbeatInterval,
    int LocalApiPort,
    string? LocalEnrollmentSecretFile = null,
    string? DisplayName = null,
    bool WorkerImage = false)
{
    public static WorkerAgentOptions FromConfiguration(IConfiguration configuration)
    {
        var name = configuration["Worker:Name"]?.Trim();
        if (string.IsNullOrWhiteSpace(name)) name = Environment.MachineName;
        if (name.Length > 128) throw new InvalidOperationException("Worker:Name ne peut pas dépasser 128 caractères.");

        // Image `worker` : l'agent n'a qu'un rôle, il n'est jamais serveur et choisit son central depuis son interface.
        var workerImage = configuration.GetValue("Worker:WorkerImage", false);
        var configuredRoles = (configuration["Worker:Roles"] ?? "worker")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Enum.TryParse<Encoding.Contracts.Workers.NodeRole>(value, true, out var role)
                ? role
                : throw new InvalidOperationException($"Rôle de worker inconnu : {value}."))
            .Distinct()
            .ToArray();
        var roles = workerImage
            ? [Encoding.Contracts.Workers.NodeRole.Worker]
            : configuredRoles;
        if (roles.Length == 0) throw new InvalidOperationException("Worker:Roles doit déclarer au moins un rôle.");

        var maxConcurrentTasks = configuration.GetValue("Worker:MaxConcurrentTasks", 1);
        if (maxConcurrentTasks is < 0 or > 1024) throw new InvalidOperationException("Worker:MaxConcurrentTasks doit être compris entre 0 et 1024.");
        var intervalSeconds = configuration.GetValue("Worker:HeartbeatIntervalSeconds", 10);
        if (intervalSeconds is < 1 or > 300) throw new InvalidOperationException("Worker:HeartbeatIntervalSeconds doit être compris entre 1 et 300.");
        var localApiPort = configuration.GetValue("WorkerUi:Port", 8081);
        if (localApiPort is < 1 or > 65535) throw new InvalidOperationException("WorkerUi:Port est invalide.");

        var dataDirectory = Path.GetFullPath(configuration["Worker:DataDirectory"] ?? ".encoding-node");
        var configuredSecretFile = configuration[Encoding.Core.Workers.LocalEnrollment.SecretFileSetting];
        // Dans l'image worker, aucun lanceur ne crée le secret local : l'agent le garde dans son dossier de données.
        var secretFile = !string.IsNullOrWhiteSpace(configuredSecretFile) ? configuredSecretFile
            : workerImage ? Path.Combine(dataDirectory, Encoding.Core.Workers.LocalEnrollment.FileName) : null;

        return new WorkerAgentOptions(
            name,
            roles,
            maxConcurrentTasks,
            dataDirectory,
            NormalizeOptionalUrl(configuration["Central:ApiBaseUrl"]),
            configuration.GetValue("Worker:AutoAcceptEnrollment", false),
            TimeSpan.FromSeconds(intervalSeconds),
            localApiPort,
            secretFile,
            string.IsNullOrWhiteSpace(configuration["Worker:DisplayName"]) ? null : configuration["Worker:DisplayName"]!.Trim(),
            workerImage);
    }

    private static string? NormalizeOptionalUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("Central:ApiBaseUrl doit être une URL HTTP ou HTTPS absolue.");
        return uri.AbsoluteUri.EndsWith('/') ? uri.AbsoluteUri : uri.AbsoluteUri + '/';
    }
}
