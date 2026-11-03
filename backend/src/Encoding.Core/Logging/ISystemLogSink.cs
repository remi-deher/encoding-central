using Encoding.Contracts.Logging;

namespace Encoding.Core.Logging;

/// <summary>
/// Récepteur optionnel pour la persistance locale des logs (ex. SQLite pour Warning/Error).
/// </summary>
public interface ISystemLogSink
{
    ValueTask EmitAsync(SystemLogEntry entry, CancellationToken ct = default);
}

/// <summary>
/// Contexte d'identification du worker pour les logs système.
/// </summary>
public interface ISystemLogWorkerContext
{
    Guid? WorkerId { get; }
    string? WorkerName { get; }
}

public sealed class DefaultSystemLogWorkerContext : ISystemLogWorkerContext
{
    private readonly Func<(Guid? NodeId, string? NodeName)>? _resolver;

    public DefaultSystemLogWorkerContext(Guid? nodeId = null, string? nodeName = null)
    {
        WorkerId = nodeId;
        WorkerName = nodeName;
    }

    public DefaultSystemLogWorkerContext(Func<(Guid? NodeId, string? NodeName)> resolver)
    {
        _resolver = resolver;
    }

    public Guid? WorkerId => _resolver?.Invoke().NodeId ?? field;
    public string? WorkerName => _resolver?.Invoke().NodeName ?? field;
}
