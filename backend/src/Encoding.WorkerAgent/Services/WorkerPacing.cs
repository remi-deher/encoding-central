namespace Encoding.WorkerAgent.Services;

/// <summary>
/// Cadences communes des boucles de l'agent : un jitter évite que tous les workers redémarrés ensemble frappent le central au même instant,
/// et une cadence lente remplace les sondages rapides une fois le worker enrôlé (le central le pilote alors par le flux de réveil).
/// </summary>
public static class WorkerPacing
{
    /// <summary>Cadence des boucles de découverte et d'enrôlement automatique d'un worker déjà enrôlé.</summary>
    public static readonly TimeSpan EnrolledPollInterval = TimeSpan.FromMinutes(5);

    /// <summary>Écart relatif appliqué au heartbeat (±10 %).</summary>
    public const double HeartbeatJitter = 0.10;

    /// <summary>
    /// Étale <paramref name="interval"/> de ±<paramref name="fraction"/>. <paramref name="random01"/> est un tirage dans [0, 1) ;
    /// 0,5 redonne l'intervalle exact. Résultat jamais négatif.
    /// </summary>
    public static TimeSpan Jitter(TimeSpan interval, double fraction, double random01)
    {
        var factor = 1 + ((Math.Clamp(random01, 0, 1) * 2) - 1) * fraction;
        return TimeSpan.FromTicks(Math.Max(0, (long)(interval.Ticks * factor)));
    }

    public static TimeSpan Jitter(TimeSpan interval, double fraction) => Jitter(interval, fraction, Random.Shared.NextDouble());
}
