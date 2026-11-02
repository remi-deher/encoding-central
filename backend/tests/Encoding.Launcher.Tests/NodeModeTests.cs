using Encoding.Core.Nodes;

namespace Encoding.Launcher.Tests;

internal static class NodeModeTests
{
    public static Task RunAsync()
    {
        Check.That(NodeMode.Parse(null) == NodeMode.Both, "Sans valeur, le nœud est serveur et worker.");
        Check.That(NodeMode.Parse("worker") == new NodeMode(false, true), "Le mode worker seul est reconnu.");
        Check.That(NodeMode.Parse(" Server , WORKER ") == NodeMode.Both, "Les rôles ne dépendent ni de la casse ni des espaces.");
        Check.That(new NodeMode(true, false).ToRoles() == "server", "Les rôles sont sérialisés pour l’agent.");
        Check.Throws<InvalidOperationException>(() => NodeMode.Parse("admin"), "Un rôle inconnu est refusé.");
        Check.Throws<InvalidOperationException>(() => NodeMode.Validate(new NodeMode(false, false)), "Un nœud sans fonction est refusé.");

        var directory = Directory.CreateTempSubdirectory("launcher-mode-").FullName;
        try
        {
            var store = new NodeModeStore(directory, "worker");
            Check.That(store.Load() == new NodeMode(false, true), "Sans fichier, la variable Node__Roles donne le mode initial.");
            store.Save(new NodeMode(true, false));
            Check.That(new NodeModeStore(directory, "worker").Load() == new NodeMode(true, false), "Le mode enregistré prévaut sur la variable et survit au redémarrage.");
            store.Save(new NodeMode(false, true));
            Check.That(new NodeModeStore(directory, "server").Load() == new NodeMode(false, true), "Un nouveau changement remplace le mode enregistré.");
            Check.That(!File.Exists(Path.Combine(directory, "node-mode.json.tmp")), "La publication atomique ne laisse pas de fichier temporaire.");
            Check.Throws<InvalidOperationException>(() => store.Save(new NodeMode(false, false)), "Un mode vide n’est jamais enregistré.");
            File.WriteAllText(Path.Combine(directory, "node-mode.json"), "{pas du json");
            Check.Throws<InvalidOperationException>(() => store.Load(), "Un fichier illisible est refusé au lieu d’être remplacé en silence.");
        }
        finally { Directory.Delete(directory, true); }
        return Task.CompletedTask;
    }
}
