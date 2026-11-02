namespace Encoding.NodeAgent.Runtime;

/// <summary>Keep idle claims bounded while avoiding synchronized bursts across workers.</summary>
internal static class WorkerPolling
{
    public static Task DelayAsync(int minimumMilliseconds, CancellationToken ct) =>
        Task.Delay(TimeSpan.FromMilliseconds(minimumMilliseconds + Random.Shared.Next(0, 501)), ct);
}
