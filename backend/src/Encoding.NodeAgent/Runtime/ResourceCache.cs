namespace Encoding.NodeAgent.Runtime;

/// <summary>Cache local du worker pour les fichiers de ressources du catalogue (ADR 0006) : un fichier par empreinte SHA-256.</summary>
public static class ResourceCache
{
    public static string Root(NodeAgentOptions options) => Path.Combine(options.DataDirectory, "resources-cache");
}
