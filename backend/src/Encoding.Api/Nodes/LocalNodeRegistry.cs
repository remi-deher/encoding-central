using Encoding.Core.Nodes;

namespace Encoding.Api.Nodes;

/// <summary>
/// Retient le nœud local de ce serveur (celui de l'image unique, enrôlé d'office, architecture 0037) pour lui donner
/// le nom convivial choisi à l'installation, quel que soit l'ordre entre l'enrôlement de l'agent et la création du premier administrateur.
/// </summary>
public sealed class LocalNodeRegistry(NodeRepository repository, LocalNodeLogContext? logContext = null)
{
    public const int MaximumNameLength = 128;
    private readonly object gate = new();
    private Guid? nodeId;
    private string? pendingName;

    public Guid? NodeId { get { lock (gate) return nodeId; } }

    public static string? NormalizeName(string? value)
    {
        var name = value?.Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    public static bool IsValidName(string? value) => NormalizeName(value) is not { Length: > MaximumNameLength };

    /// <summary>Appelé à l'enrôlement du nœud local ; applique le nom demandé à l'installation s'il attend.</summary>
    public async Task RegisterAsync(Guid id, CancellationToken ct)
    {
        string? name;
        lock (gate)
        {
            nodeId = id;
            if (logContext is not null) logContext.NodeId = id;
            name = pendingName;
            pendingName = null;
        }
        if (name is not null) await repository.SetDisplayNameIfUnsetAsync(id, name, ct);
    }

    /// <summary>Appelé à l'installation ; le nom est appliqué tout de suite si le nœud local est connu, sinon à son enrôlement.</summary>
    public async Task RequestNameAsync(string? value, CancellationToken ct)
    {
        var name = NormalizeName(value);
        if (name is null) return;
        Guid? id;
        lock (gate)
        {
            id = nodeId;
            if (id is null) pendingName = name;
        }
        if (id is not null)
        {
            await repository.SetDisplayNameIfUnsetAsync(id.Value, name, ct);
        }
        else
        {
            await repository.SetFallbackLocalDisplayNameAsync(name, ct);
        }
    }

    public async Task<string?> ResolveDisplayNameAsync(CancellationToken ct)
    {
        string? name;
        Guid? id;
        lock (gate)
        {
            name = pendingName;
            id = nodeId;
        }
        if (!string.IsNullOrWhiteSpace(name)) return name;
        if (id.HasValue)
        {
            var node = await repository.FindByIdAsync(id.Value, ct);
            if (!string.IsNullOrWhiteSpace(node?.DisplayName)) return node.DisplayName;
        }
        return await repository.GetActiveDisplayNameAsync(ct);
    }
}
