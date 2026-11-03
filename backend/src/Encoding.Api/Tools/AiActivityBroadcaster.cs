namespace Encoding.Api.Tools;

/// <summary>
/// Signale qu'une activité de l'assistant a été enregistrée. Il ne porte ni identifiant ni contenu : un abonné relit sa propre activité,
/// propriétaire compris, si bien que rien d'un autre utilisateur ne transite par ce signal.
/// </summary>
public sealed class AiActivityBroadcaster
{
    private long version;
    private TaskCompletionSource<bool> next = Create();

    public long CurrentVersion => Volatile.Read(ref version);

    public void Publish()
    {
        Interlocked.Increment(ref version);
        Interlocked.Exchange(ref next, Create()).TrySetResult(true);
    }

    /// <summary>Rend la version courante dès qu'elle dépasse <paramref name="known"/>, ou à l'échéance du délai (cœur de flux).</summary>
    public async Task<long> WaitAsync(long known, TimeSpan timeout, CancellationToken ct)
    {
        var pending = next.Task;
        if (CurrentVersion > known) return CurrentVersion;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try { await Task.WhenAny(pending, Task.Delay(timeout, stop.Token)); }
        finally { stop.Cancel(); }
        ct.ThrowIfCancellationRequested();
        return CurrentVersion;
    }

    private static TaskCompletionSource<bool> Create() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
