using System.Text.Json;

namespace Encoding.Contracts.Plugins;

public sealed record SavePluginConfigurationRequest(
    /// <summary>Révision conservée pour compatibilité des clients ; une valeur périmée est acceptée (dernier enregistrement gagné).</summary>
    int ExpectedRevision,
    IReadOnlyDictionary<string, JsonElement> Values,
    IReadOnlyList<string>? ClearSecrets = null);

public sealed record PluginConfigurationResponse(
    string PluginId,
    int Revision,
    IReadOnlyDictionary<string, JsonElement> Values,
    IReadOnlyList<string> ConfiguredSecrets);

public sealed record RuntimePluginConfiguration(
    string PluginId,
    IReadOnlyDictionary<string, JsonElement> Values);
