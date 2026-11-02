using System.Text.Json;
using Encoding.Contracts.Flows.Conditions;

namespace Encoding.Contracts.Plugins;

/// <summary>Data-only catalog exchanged with an isolated plugin process.</summary>
public sealed record PluginManifest(
    int ContractVersion,
    string Id,
    string Version,
    string Name,
    IReadOnlyList<int> SupportedHostContractVersions,
    IReadOnlyList<FlowComponentDescriptor> Components,
    FlowConfigurationSchema? ConfigurationSchema = null,
    PluginPublicationGuard? PublicationGuard = null,
    IReadOnlyList<Encoding.Contracts.Tools.PluginToolDependency>? RequiredToolPackages = null);

/// <summary>
/// Point d'extension (architecture 0059) : le plugin fournit une garde consultée par le nœud d'accès avant de remplacer un
/// fichier source. Le composant <see cref="ComponentId"/> est exécuté avec la configuration fixe <see cref="FixedConfiguration"/>
/// complétée par les champs du plugin <see cref="ConfigurationFields"/> (champ du composant -> champ du plugin) ; une sortie de
/// <see cref="WaitOutputs"/> retarde le remplacement, une sortie de <see cref="AllowOutputs"/> l'autorise, toute autre est un refus.
/// </summary>
public sealed record PluginPublicationGuard(
    string ComponentId,
    string EnabledField,
    string MaxWaitField,
    IReadOnlyDictionary<string, JsonElement> FixedConfiguration,
    IReadOnlyDictionary<string, string> ConfigurationFields,
    IReadOnlyList<string> WaitOutputs,
    IReadOnlyList<string> AllowOutputs);

public sealed record FlowComponentDescriptor(
    string Id,
    string Version,
    string Name,
    string Description,
    string Kind,
    string MediaType,
    IReadOnlyList<string> SupportedFlowMediaTypes,
    IReadOnlyList<FlowOutputDescriptor> Outputs,
    FlowConfigurationSchema ConfigurationSchema,
    IReadOnlyList<FlowToolRequirement> RequiredTools,
    bool RequiresMedia,
    bool RequiresWriteAccess,
    IReadOnlyList<string>? CompatiblePluginVersions = null,
    string? MinimumRuntimePluginVersion = null,
    bool ProducesFiles = false,
    int? TimeoutSeconds = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? MediaEffect = null,
    string? Category = null);

/// <summary>Stable functional identifiers used to organize flow components in the editor.</summary>
public static class FlowComponentCategories
{
    public static readonly IReadOnlySet<string> Ids = new HashSet<string>(StringComparer.Ordinal)
    {
        "inputs", "files", "discovery", "metadata", "encoding", "video", "audio", "images", "text",
        "containers", "media-rules", "validation", "conditions", "variables", "scripts", "arr",
        "media-servers", "communications", "cluster", "orchestration", "reporting", "utilities",
    };
}

public sealed record FlowOutputDescriptor(string Id, string Label, string Description);

public sealed record FlowToolRequirement(
    string ToolId,
    string? MinimumVersion,
    IReadOnlyList<string> RequiredCapabilities);

/// <summary>
/// Uses the editor's text, number, boolean and select field vocabulary. <c>Scope</c> (plugin configuration only):
/// <c>server</c> keeps the value on the server (never sent to a worker), <c>worker</c> or absent sends it with the invocation.
/// </summary>
public sealed record FlowConfigurationSchema(
    int Version,
    IReadOnlyList<FlowConfigurationField> Fields);

public sealed record FlowConfigurationField(
    string Id,
    string Type,
    string Label,
    string? Description = null,
    bool Required = false,
    bool Inline = false,
    JsonElement? DefaultValue = null,
    string? Placeholder = null,
    bool Multiline = false,
    decimal? Min = null,
    decimal? Max = null,
    decimal? Step = null,
    IReadOnlyList<decimal>? Suggestions = null,
    IReadOnlyList<FlowSelectOption>? Options = null,
    IReadOnlyList<ConditionFieldDefinition>? ConditionFields = null,
    IReadOnlyList<ConditionPreset>? ConditionPresets = null,
    string? VisibleWhenFieldId = null,
    string? VisibleWhenValue = null,
    bool VisibleWhenEmpty = false,
    string? Importance = null,
    string? Section = null,
    string? Editor = null,
    IReadOnlyList<FlowConfigurationField>? ItemFields = null,
    int? MinItems = null,
    int? MaxItems = null,
    string? RequiresPluginVersion = null,
    string? RequiresPluginVersionValue = null,
    bool LegacyOnly = false,
    string? Scope = null);

public sealed record FlowSelectOption(
    string Value,
    string Label,
    IReadOnlyList<string>? SupportedFlowMediaTypes = null);

/// <summary>
/// Empreinte du contrat d'un manifeste (identité, composants, sorties, types de champs), indépendante des libellés.
/// Le worker l'annonce avec la capacité <c>plugin/&lt;id&gt;</c> et le serveur la compare à son propre catalogue :
/// deux builds portant le même numéro de version mais des contrats différents ne sont pas interchangeables.
/// </summary>
public static class PluginManifestFingerprint
{
    public static string Compute(PluginManifest manifest)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(manifest.Id).Append('@').Append(manifest.Version).Append('\n');
        foreach (var dependency in (manifest.RequiredToolPackages ?? []).OrderBy(item => item.ToolPackageId, StringComparer.Ordinal))
        {
            builder.Append("tool:").Append(dependency.ToolPackageId).Append('@').Append(dependency.MinimumVersion)
                .Append("..<").Append(dependency.MaximumVersionExclusive ?? "*").Append('\n');
            foreach (var capability in (dependency.RequiredCapabilities ?? []).Order(StringComparer.Ordinal))
                builder.Append("  capability:").Append(capability).Append('\n');
        }
        foreach (var field in (manifest.ConfigurationSchema?.Fields ?? []).OrderBy(field => field.Id, StringComparer.Ordinal))
            builder.Append("cfg:").Append(field.Id).Append(':').Append(field.Type).Append('\n');
        foreach (var component in manifest.Components.OrderBy(component => component.Id, StringComparer.Ordinal))
        {
            builder.Append("component:").Append(component.Id).Append('@').Append(component.Version)
                .Append(" schema=").Append(component.ConfigurationSchema.Version).Append(" kind=").Append(component.Kind).Append('\n');
            // Keep legacy fingerprints unchanged; explicit effects require a worker that understands them.
            if(component.MediaEffect is not null) builder.Append("  mediaEffect:").Append(component.MediaEffect).Append('\n');
            foreach (var output in component.Outputs.OrderBy(output => output.Id, StringComparer.Ordinal)) builder.Append("  out:").Append(output.Id).Append('\n');
            foreach (var field in component.ConfigurationSchema.Fields.OrderBy(field => field.Id, StringComparer.Ordinal))
                builder.Append("  field:").Append(field.Id).Append(':').Append(field.Type).Append('\n');
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant()[..32];
    }
}
