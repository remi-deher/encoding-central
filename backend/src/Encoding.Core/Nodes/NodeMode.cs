namespace Encoding.Core.Nodes;

/// <summary>Mode de fonctionnement d’un nœud : fonctions serveur (API, interface, orchestration) et/ou worker (traitement).</summary>
public sealed record NodeMode(bool Server, bool Worker)
{
    public static readonly NodeMode Both = new(true, true);

    /// <summary>Lit une liste de rôles séparés par des virgules (<c>server</c>, <c>worker</c>) ; refuse un rôle inconnu ou une liste vide.</summary>
    public static NodeMode Parse(string? roles)
    {
        var values = (string.IsNullOrWhiteSpace(roles) ? "server,worker" : roles)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool server = false, worker = false;
        foreach (var value in values)
        {
            if (value.Equals("server", StringComparison.OrdinalIgnoreCase)) server = true;
            else if (value.Equals("worker", StringComparison.OrdinalIgnoreCase)) worker = true;
            else throw new InvalidOperationException($"Rôle de nœud inconnu : {value}.");
        }
        return Validate(new NodeMode(server, worker));
    }

    /// <summary>Un nœud doit garder au moins une fonction : sans serveur ni worker il ne ferait plus partie du cluster.</summary>
    public static NodeMode Validate(NodeMode mode) =>
        mode.Server || mode.Worker
            ? mode
            : throw new InvalidOperationException("Le nœud doit garder au moins une fonction : serveur ou worker.");

    public string ToRoles() => string.Join(',', new[] { Server ? "server" : null, Worker ? "worker" : null }.Where(role => role is not null));
}
