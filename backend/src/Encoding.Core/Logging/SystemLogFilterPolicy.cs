using Microsoft.Extensions.Logging;

namespace Encoding.Core.Logging;

/// <summary>
/// Politique de filtrage anti-pollution pour éviter de saturer le tampon avec les requêtes HTTP 200 de routine,
/// les healthchecks et les transports de consensus réguliers.
/// </summary>
public static class SystemLogFilterPolicy
{
    public static bool ShouldCapture(
        string category,
        LogLevel level,
        string? message,
        bool allowRoutineDiagnostics = false)
    {
        // Les avertissements, erreurs et alertes critiques ne sont JAMAIS supprimés.
        if (level >= LogLevel.Warning)
            return true;

        if (allowRoutineDiagnostics)
            return true;

        // Requêtes HTTP de routine Kestrel / Hosting
        if (category.StartsWith("Microsoft.AspNetCore.Hosting.Diagnostics", StringComparison.OrdinalIgnoreCase) ||
            category.StartsWith("Microsoft.AspNetCore.Routing", StringComparison.OrdinalIgnoreCase) ||
            category.StartsWith("Microsoft.AspNetCore.StaticFiles", StringComparison.OrdinalIgnoreCase))
        {
            if (message != null && (
                message.Contains("Request finished", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Request reached the end", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Executing endpoint", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Executed endpoint", StringComparison.OrdinalIgnoreCase) ||
                message.Contains(" 200 ", StringComparison.OrdinalIgnoreCase) ||
                message.Contains(" 204 ", StringComparison.OrdinalIgnoreCase) ||
                message.Contains(" 304 ", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        // HealthChecks périodiques
        if (category.Contains("HealthChecks", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("HealthCheck", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Télémétrie du maillage en niveau Trace/Debug
        if (level <= LogLevel.Debug && (
            category.Contains("ClusterMesh", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("ClusterMeshTransport", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }
}
