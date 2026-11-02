using System.Text.Json;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.NodeAgent.Runtime.Media;

namespace Encoding.NodeAgent.Runtime;

/// <summary>
/// État mutable d'une marche de graphe : workspace média, variables, position dans le flow.
/// Les champs de bail (<see cref="Revision"/>, perdu, annulé) ne sont mutés que via les méthodes
/// thread-safe, appelées sous <c>leaseGate</c> ou avec barrières volatiles depuis le renouvellement.
/// </summary>
internal sealed class FlowExecutionState
{
    private int revision;
    private bool cancelled;
    private bool lost;

    public FlowExecutionState(int initialRevision) => revision = initialRevision;

    public bool PublicationConfirmed { get; set; }
    public string? ActiveBlock { get; set; }
    public string? SourceSha { get; set; }
    public bool DirectRead { get; set; }
    public string Extension { get; set; } = "";
    public string Original { get; set; } = "";
    public MediaFileStamp? Observed { get; set; }
    public RuntimeContext Context { get; set; } = null!;
    public FlowPlacementPlan? PlacementPlan { get; set; }
    public Dictionary<string, JsonElement> Variables { get; set; } = new();
    public string? Next { get; set; }
    public string? Terminal { get; set; }
    public string? LastOutput { get; set; }
    public string Current { get; set; } = "";
    public bool Materialized { get; set; }
    public MediaFileStamp? CurrentStamp { get; set; }
    public HashSet<string> ConformingNodes { get; } = new(StringComparer.Ordinal);

    public int Revision => Volatile.Read(ref revision);
    public bool IsCancelled => Volatile.Read(ref cancelled);
    public bool IsLost => Volatile.Read(ref lost);

    /// <summary>Applique un renouvellement de bail réussi. Appeler sous <c>leaseGate</c>.</summary>
    public void ApplyLeaseRenewal(int newRevision, bool cancelRequested, Action requestCancel)
    {
        Volatile.Write(ref revision, newRevision);
        if (!cancelRequested) return;
        Volatile.Write(ref cancelled, true);
        requestCancel();
    }

    /// <summary>Marque le bail comme perdu (409, sondes, interruptions HTTP). Thread-safe.</summary>
    public void MarkLost() => Volatile.Write(ref lost, true);
}
