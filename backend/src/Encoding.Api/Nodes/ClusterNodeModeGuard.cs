using Encoding.Core.Nodes;
namespace Encoding.Api.Nodes;

/// <summary>The local launcher path observes the same cluster checks as remote mode requests.</summary>
public sealed class ClusterNodeModeGuard(NodeRepository nodes,LocalNodeRegistry local,NodePresenceOptions presence):INodeModeTransitionGuard
{
    public async Task<string?> ValidateAsync(NodeMode requested,CancellationToken ct)
    {
        if(local.NodeId is not {} id) return "Attendre l’identification du nœud local avant de changer ses fonctions.";
        return await nodes.RequestModeAsync(id,requested.Server,requested.Worker,presence.OfflineAfter,ct,persist:false) switch
        {
            NodeRepository.ModeRequestResult.Accepted=>null,
            NodeRepository.ModeRequestResult.LastServer=>"La fonction du dernier serveur ne peut pas être retirée.",
            NodeRepository.ModeRequestResult.NotFound=>"Le nœud local n’est plus admis.",
            _=>"Changement de fonctions différé : réconcilier les serveurs et déplacer ou terminer les tâches possédées."
        };
    }
}
