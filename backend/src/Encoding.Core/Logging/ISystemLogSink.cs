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
/// Contexte d'identification du nœud pour les logs système.
/// </summary>
public interface ISystemLogNodeContext
{
    Guid? NodeId { get; }
    string? NodeName { get; }
}

public sealed class DefaultSystemLogNodeContext : ISystemLogNodeContext
{
    private readonly Func<(Guid? NodeId, string? NodeName)>? _resolver;

    public DefaultSystemLogNodeContext(Guid? nodeId = null, string? nodeName = null)
    {
        NodeId = nodeId;
        NodeName = nodeName;
    }

    public DefaultSystemLogNodeContext(Func<(Guid? NodeId, string? NodeName)> resolver)
    {
        _resolver = resolver;
    }

    public Guid? NodeId => _resolver?.Invoke().NodeId ?? field;
    public string? NodeName => _resolver?.Invoke().NodeName ?? field;
}
