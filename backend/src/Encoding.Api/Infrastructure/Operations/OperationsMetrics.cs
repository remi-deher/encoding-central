using System.Collections.Concurrent;

namespace Encoding.Api.Infrastructure.Operations;

/// <summary>Compteurs du processus courant exposés par <c>/metrics</c>. Les compteurs repartent de zéro au redémarrage, comme le veut Prometheus.</summary>
public static class OperationsMetrics
{
    public sealed record HttpKey(string Method, string Route, int Status);
    public sealed class HttpStat { public long Count; public long TicksTotal; }

    private static readonly ConcurrentDictionary<HttpKey, HttpStat> Http = new();
    private static long retentionRuns, retentionExecutions, retentionBytes, retentionTemporary, retentionErrors, retentionLastRun;

    public static IReadOnlyDictionary<HttpKey, HttpStat> HttpRequests => Http;
    public static long RetentionRuns => Interlocked.Read(ref retentionRuns);
    public static long RetentionExecutions => Interlocked.Read(ref retentionExecutions);
    public static long RetentionBytes => Interlocked.Read(ref retentionBytes);
    public static long RetentionTemporaryFiles => Interlocked.Read(ref retentionTemporary);
    public static long RetentionErrors => Interlocked.Read(ref retentionErrors);
    public static long RetentionLastRunUnix => Interlocked.Read(ref retentionLastRun);

    public static void Request(string method, string route, int status, long elapsedTicks)
    {
        var stat = Http.GetOrAdd(new(method, route, status), _ => new());
        Interlocked.Increment(ref stat.Count);
        Interlocked.Add(ref stat.TicksTotal, elapsedTicks);
    }

    public static void RetentionRun(RetentionResult result)
    {
        Interlocked.Increment(ref retentionRuns);
        if (!result.DryRun)
        {
            Interlocked.Add(ref retentionExecutions, result.Purged);
            Interlocked.Add(ref retentionBytes, result.BytesFreed);
            Interlocked.Add(ref retentionTemporary, result.TempFilesRemoved);
        }
        Interlocked.Exchange(ref retentionLastRun, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    public static void RetentionFailed() => Interlocked.Increment(ref retentionErrors);
}
