namespace Encoding.Core.Runtime;

/// <summary>
/// Niveau de confinement demandé par l'hôte au point d'entrée <c>--isolated-process</c> (variable d'environnement
/// <see cref="EnvironmentVariable"/>, liste séparée par des virgules). Absente ou vide : politique la plus stricte, sans réseau et avec
/// un accès <c>/proc</c> limité au processus lui-même. Une valeur inconnue est une erreur (le bac à sable refuse de démarrer).
/// </summary>
/// <param name="AllowNetwork">Autorise les sockets TCP/IP : capacité <c>network</c> déclarée par le plugin, ou CLI IA.</param>
/// <param name="HostProcessAccess">Garde la lecture large de <c>/proc</c> et <c>/sys</c> (CLI IA officiels), au lieu du strict nécessaire.</param>
public sealed record SandboxPolicy(bool AllowNetwork, bool HostProcessAccess)
{
    public const string EnvironmentVariable = "ENCODING_SANDBOX_POLICY";
    private const string NetworkToken = "network", HostProcessToken = "host-proc";

    /// <summary>Plugins et scripts sans capacité déclarée.</summary>
    public static readonly SandboxPolicy Strict = new(false, false);
    /// <summary>Plugins qui déclarent la capacité <c>network</c>.</summary>
    public static readonly SandboxPolicy Network = new(true, false);
    /// <summary>CLI IA officiels : réseau vers les fournisseurs, runtime Node qui lit <c>/proc</c> et <c>/sys</c>.</summary>
    public static readonly SandboxPolicy AiCli = new(true, true);

    public static SandboxPolicy ForPlugin(bool allowNetwork) => allowNetwork ? Network : Strict;

    public static SandboxPolicy Parse(string? value)
    {
        bool network = false, host = false;
        foreach (var token in (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (token)
            {
                case NetworkToken: network = true; break;
                case HostProcessToken: host = true; break;
                default: throw new FormatException($"Politique de bac à sable inconnue : {token}.");
            }
        }
        return new(network, host);
    }

    public string Serialize() => string.Join(',', new[] { AllowNetwork ? NetworkToken : null, HostProcessAccess ? HostProcessToken : null }.OfType<string>());
}
