namespace Encoding.WorkerAgent.Runtime;

/// <summary>
/// Délai avant de rouvrir le flux de réveil : backoff exponentiel 1 s, 2 s, 4 s… plafonné à 60 s, avec jitter complet
/// (tirage uniforme dans [0, plafond]) pour que des workers coupés ensemble ne reviennent pas ensemble.
/// L'échelle repart de 1 s après une connexion restée stable 30 s. La ligne SSE <c>retry:</c> du central est un plancher.
/// </summary>
public sealed class WakeReconnectPolicy(TimeProvider time, Func<double>? random = null)
{
    public static readonly TimeSpan Initial = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan Max = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan StableAfter = TimeSpan.FromSeconds(30);
    /// <summary>Un <c>retry:</c> démesuré ne doit pas rendre le worker sourd au central plus longtemps que le plafond du backoff.</summary>
    private static readonly TimeSpan MaxServerRetry = Max;
    /// <summary>Plancher du jitter complet : évite une reconnexion en boucle serrée quand le tirage tombe près de 0.</summary>
    private static readonly TimeSpan Floor = TimeSpan.FromMilliseconds(100);

    private readonly Func<double> random = random ?? Random.Shared.NextDouble;
    private int attempts;
    private DateTimeOffset? connectedAt;
    private TimeSpan? serverRetry;

    /// <summary>Le central a confirmé le flux (<c>ready</c>) : la stabilité se mesure depuis cet instant.</summary>
    public void MarkConnected() => connectedAt ??= time.GetUtcNow();

    /// <summary>Valeur de la ligne <c>retry: &lt;ms&gt;</c> envoyée par le central ; une valeur invalide est ignorée.</summary>
    public void SetServerRetry(TimeSpan retry)
    {
        if (retry >= TimeSpan.Zero) serverRetry = retry > MaxServerRetry ? MaxServerRetry : retry;
    }

    /// <summary>Délai à attendre avant la prochaine connexion, appelé quand le flux vient de se fermer ou de ne pas s'ouvrir.</summary>
    public TimeSpan NextDelay()
    {
        if (connectedAt is { } since && time.GetUtcNow() - since >= StableAfter) attempts = 0;
        connectedAt = null;
        attempts++;
        var ceiling = TimeSpan.FromTicks(Math.Min(Max.Ticks, Initial.Ticks << Math.Min(attempts - 1, 20)));
        var delay = TimeSpan.FromTicks((long)(ceiling.Ticks * Math.Clamp(random(), 0, 1)));
        if (delay < Floor) delay = Floor;
        return serverRetry is { } floor && floor > delay ? floor : delay;
    }
}
