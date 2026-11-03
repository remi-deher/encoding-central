using Encoding.Core.Logging;

namespace Encoding.Api.Workers;

/// <summary>
/// Identité du worker local pour les logs système. Volontairement sans dépendance DI : le fournisseur de logs est sollicité
/// pendant la construction d'autres services (coordinateur pair, dépôts) et ne doit jamais résoudre un service du conteneur.
/// </summary>
public sealed class LocalWorkerLogContext : ISystemLogWorkerContext
{
    private readonly object gate = new();
    private Guid? workerId;

    public Guid? WorkerId { get { lock (gate) return workerId; } set { lock (gate) workerId = value; } }

    public string? WorkerName => "Server";
}
