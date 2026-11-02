namespace Encoding.Api.Infrastructure.Operations;

/// <summary>Réglages d’exploitation (section <c>Operations</c>) ; les valeurs par défaut sont identiques sur tous les nœuds serveur.</summary>
public sealed class OperationsOptions
{
    public const string Section = "Operations";

    public MetricsOptions Metrics { get; init; } = new();
    public HealthOptions Health { get; init; } = new();
    public ArtifactRetentionOptions ArtifactRetention { get; init; } = new();

    public static OperationsOptions From(IConfiguration configuration)
    {
        var options = configuration.GetSection(Section).Get<OperationsOptions>() ?? new();
        options.ArtifactRetention.Validate();
        if (options.Health.MinFreeDiskBytes < 0) throw new InvalidOperationException("Operations:Health:MinFreeDiskBytes ne peut pas être négatif.");
        return options;
    }
}

public sealed class MetricsOptions
{
    public bool Enabled { get; init; } = true;
    /// <summary>Jeton Bearer facultatif. Sans jeton, <c>/metrics</c> est ouvert : limiter l’accès au réseau d’exploitation.</summary>
    public string? Token { get; init; }
}

public sealed class HealthOptions
{
    /// <summary>Espace libre sous lequel un volume de données est signalé dégradé.</summary>
    public long MinFreeDiskBytes { get; init; } = 5L * 1024 * 1024 * 1024;
    /// <summary>Âge au-delà duquel une sauvegarde de flow en attente est signalée.</summary>
    public int BackupLagMinutes { get; init; } = 15;
}

public sealed class ArtifactRetentionOptions
{
    /// <summary>Activé par défaut : les artefacts de travail d'exécutions terminées ne doivent pas remplir le disque. Ne touche jamais aux médias.</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>Jours de conservation après la fin d’une exécution réussie ou annulée ; 0 purge dès la passe suivante.</summary>
    public int Days { get; init; }
    /// <summary>Jours de conservation après un échec ; 0 purge dès la passe suivante (la reprise repart de zéro).</summary>
    public int FailedDays { get; init; }
    public int IntervalMinutes { get; init; } = 10;
    public int BatchSize { get; init; } = 50;
    /// <summary>Journalise et compte sans rien supprimer.</summary>
    public bool DryRun { get; init; }
    /// <summary>Âge minimal des fichiers <c>.tmp</c> orphelins avant suppression.</summary>
    public int TempFileHours { get; init; } = 24;

    public void Validate()
    {
        if (!Enabled) return;
        if (Days < 0) throw new InvalidOperationException("Operations:ArtifactRetention:Days ne peut pas être négatif.");
        if (FailedDays < 0) throw new InvalidOperationException("Operations:ArtifactRetention:FailedDays ne peut pas être négatif.");
        if (IntervalMinutes < 1) throw new InvalidOperationException("Operations:ArtifactRetention:IntervalMinutes doit être supérieur à zéro.");
        if (BatchSize is < 1 or > 1000) throw new InvalidOperationException("Operations:ArtifactRetention:BatchSize doit être compris entre 1 et 1000.");
        if (TempFileHours < 1) throw new InvalidOperationException("Operations:ArtifactRetention:TempFileHours doit être supérieur à zéro.");
    }
}
