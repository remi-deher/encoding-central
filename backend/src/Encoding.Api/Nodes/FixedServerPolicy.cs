namespace Encoding.Api.Nodes;

/// <summary>
/// Serveur central fixe : la fonction serveur ne se donne pas à un worker et ne quitte pas le serveur qui la détient.
/// </summary>
internal static class FixedServerPolicy
{
    public const string Refusal = "Les fonctions sont fixées par l’image : le central reste serveur et worker, un worker ne change pas de fonction.";

    public static bool Enabled(IConfiguration? configuration) =>
        configuration?.GetValue("Cluster:FixedAuthority", false) ?? false;
}
