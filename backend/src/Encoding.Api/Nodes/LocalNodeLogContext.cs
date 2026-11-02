using Encoding.Core.Logging;

namespace Encoding.Api.Nodes;

/// <summary>
/// Identité du nœud local pour les logs système. Volontairement sans dépendance DI : le fournisseur de logs est sollicité
/// pendant la construction d'autres services (coordinateur pair, dépôts) et ne doit jamais résoudre un service du conteneur.
/// </summary>
public sealed class LocalNodeLogContext : ISystemLogNodeContext
{
    private readonly object gate = new();
    private Guid? nodeId;

    public Guid? NodeId { get { lock (gate) return nodeId; } set { lock (gate) nodeId = value; } }

    public string? NodeName => "Server";
}
