using Encoding.Contracts.Nodes;
using Encoding.Contracts.Flows;
using System.Text.RegularExpressions;

namespace Encoding.Api.Nodes;

public sealed record NodePresenceOptions(TimeSpan OfflineAfter);

public static class NodeHeartbeatValidator
{
    private static readonly HashSet<string> SupportedMediaTypes = new(StringComparer.Ordinal)
    {
        "video", "image", "audio", "subtitles", "music", "general"
    };
    private static readonly Regex PairingKeyPattern = new("^[0-9A-F]{16}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static Dictionary<string, string[]> Validate(NodeHeartbeatRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 128)
            errors["name"] = ["Le nom est obligatoire et limité à 128 caractères."];
        if (request.Roles is null || request.Roles.Count == 0 || request.Roles.Distinct().Count() != request.Roles.Count)
            errors["roles"] = ["Déclarez au moins un rôle sans doublon."];
        if (request.MaxConcurrentTasks < 0 || request.MaxConcurrentTasks > 1024)
            errors["maxConcurrentTasks"] = ["La limite doit être comprise entre 0 et 1024."];
        if (request.Inventory is { } inventory && (string.IsNullOrWhiteSpace(inventory.OperatingSystem)
            || inventory.OperatingSystem.Length > 128 || string.IsNullOrWhiteSpace(inventory.Architecture)
            || inventory.Architecture.Length > 64 || inventory.ProcessorCount is < 0 or > 4096
            || inventory.MemoryMb is < 0 or > 4_194_304
            || inventory.ProcessorModel?.Length > 256
            || inventory.FontSets is { Count: > 64 }
            || inventory.FontSets?.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.IndexOfAny(['/', '\\', '\r', '\n']) >= 0) == true
            || inventory.Gpus is { Count: > 16 }
            || inventory.Gpus?.Any(g => string.IsNullOrWhiteSpace(g.Name) || g.Name.Length > 256
                || g.Vendor?.Length > 64 || g.Driver?.Length > 64 || g.Device?.Length > 128) == true))
            errors["inventory"] = ["L’inventaire système contient des valeurs invalides."];
        if (request.Capabilities is null || request.Capabilities.Count > 256)
            errors["capabilities"] = ["La liste des capacités est obligatoire et limitée à 256 éléments."];
        else
        {
            for (var index = 0; index < request.Capabilities.Count; index++)
            {
                var capability = request.Capabilities[index];
                var prefix = $"capabilities[{index}]";
                if (string.IsNullOrWhiteSpace(capability.ToolId) || capability.ToolId.Length > 128)
                    errors[$"{prefix}.toolId"] = ["L'identifiant de l'outil est obligatoire et limité à 128 caractères."];
                if (string.IsNullOrWhiteSpace(capability.Version) || capability.Version.Length > 128)
                    errors[$"{prefix}.version"] = ["La version est obligatoire et limitée à 128 caractères."];
                if (capability.MediaTypes is null || capability.MediaTypes.Any(type => !SupportedMediaTypes.Contains(type)))
                    errors[$"{prefix}.mediaTypes"] = ["Un ou plusieurs types de média ne sont pas pris en charge."];
                if (capability.AccelerationTypes is null || capability.AccelerationTypes.Any(string.IsNullOrWhiteSpace))
                    errors[$"{prefix}.accelerationTypes"] = ["Les types d'accélération doivent être renseignés."];
                if (capability.EncoderChecks is { } checks
                    && (checks.Count > 64
                        || checks.Select(check => check.EncoderId).Distinct(StringComparer.Ordinal).Count() != checks.Count
                        || checks.Any(check => VideoEncoderCatalog.Find(check.EncoderId) is null
                            || check.Status is not ("usable" or "detected"))))
                    errors[$"{prefix}.encoderChecks"] = ["Les vérifications d’encodeur contiennent une entrée inconnue ou invalide."];
            }
        }
        return errors;
    }

    public static Dictionary<string, string[]> Validate(NodeEnrollmentRequest request)
    {
        var errors = Validate(new NodeHeartbeatRequest(request.Name, request.Roles, request.MaxConcurrentTasks, request.Capabilities, request.Inventory));
        if (request.Id == Guid.Empty) errors["id"] = ["L’identifiant du nœud doit être un UUID non vide."];
        if (string.IsNullOrWhiteSpace(request.NodeToken) || request.NodeToken.Length is < 40 or > 256)
            errors["nodeToken"] = ["L’identité locale du nœud est invalide."];
        if (request.DisplayName is { Length: > 128 })
            errors["displayName"] = [$"Le nom d’affichage ne peut pas dépasser {LocalNodeRegistry.MaximumNameLength} caractères."];
        if ((request.ClusterPeerId is null) != string.IsNullOrWhiteSpace(request.ClusterPeerPublicKey)
            || request.ClusterPeerId == Guid.Empty)
            errors["clusterPeer"] = ["L’identité du serveur de cluster est incomplète."];
        if (!string.IsNullOrWhiteSpace(request.ClusterPeerPublicKey))
        {
            try
            {
                using var rsa = Encoding.Api.Infrastructure.ClusterPeerIdentityStore.ImportPublicKey(request.ClusterPeerPublicKey);
            }
            catch (Exception exception) when (exception is FormatException or System.Security.Cryptography.CryptographicException)
            {
                errors["clusterPeerPublicKey"] = ["La clé publique du serveur est invalide."];
            }
        }
        if (!string.IsNullOrWhiteSpace(request.ClusterPeerReconciliationUrl)
            && Encoding.Api.Infrastructure.ClusterReconciliationRepository.TryNormalizePeerUrl(request.ClusterPeerReconciliationUrl) is null)
        {
            errors["clusterPeerReconciliationUrl"] = ["L’adresse de réconciliation du serveur est invalide."];
        }
        return errors;
    }

    public static Dictionary<string, string[]> Validate(CreateNodeEnrollmentRequest request)
    {
        if (request.Node is null)
            return new Dictionary<string, string[]> { ["node"] = ["Les informations du nœud sont obligatoires."] };
        var errors = Validate(request.Node);
        if (request.PairingKey is null || !PairingKeyPattern.IsMatch(request.PairingKey))
            errors["pairingKey"] = ["La clé de jumelage doit contenir 16 caractères hexadécimaux majuscules."];
        return errors;
    }

    public static Dictionary<string, string[]> Validate(ApproveNodeEnrollmentRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.PairingKey is null || !PairingKeyPattern.IsMatch(request.PairingKey))
            errors["pairingKey"] = ["La clé de jumelage doit contenir 16 caractères hexadécimaux majuscules."];
        return errors;
    }
}
