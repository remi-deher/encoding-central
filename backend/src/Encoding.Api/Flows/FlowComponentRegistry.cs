using System.Collections.Concurrent;
using System.Text.Json;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Plugins;
using Encoding.Contracts.Flows.Conditions;

namespace Encoding.Api.Flows;

/// <summary>Installed descriptors only. Catalog availability does not imply a runnable handler.</summary>
public sealed class FlowComponentRegistry
{
    private readonly ConcurrentDictionary<FlowComponentReference, FlowComponentDescriptor> components = [];
    private readonly ConcurrentDictionary<FlowComponentReference,string> runtimeVersions=[];
    private readonly object installGate = new();
    private readonly HashSet<string> storeInstalled = new(StringComparer.Ordinal);
    private volatile IReadOnlyList<PluginManifest> plugins = [];
    /// <summary>Catalogue courant ; remplacé en bloc (jamais modifié en place) quand un paquet du Store est installé ou retiré.</summary>
    public IReadOnlyList<PluginManifest> Plugins => plugins;

    public FlowComponentRegistry(IConfiguration configuration)
    {
        var manifests = new List<PluginManifest>();
        var catalogDirectory = Path.Combine(AppContext.BaseDirectory, "Flows", "Catalog");
        foreach (var file in Directory.Exists(catalogDirectory)
                     ? Directory.EnumerateFiles(catalogDirectory, "*.json").Order()
                     : Enumerable.Empty<string>())
        {
            var extra = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(file), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidOperationException("Manifeste plugin intégré invalide.");
            if (extra.ContractVersion != 1 || !extra.SupportedHostContractVersions.Contains(1))
                throw new InvalidOperationException("Manifeste plugin intégré incompatible.");
            manifests.Add(extra);
        }
        manifests = manifests.Select(Normalize).ToList();
        foreach (var installed in manifests)
        {
            Validate(installed);
            Register(installed, installed.Components.Select(component => Reference(installed, component)).ToList());
        }
        // Plugins tiers : un manifeste défaillant est écarté et signalé, il ne doit jamais empêcher le serveur de démarrer.
        var rejected = new List<RejectedPluginManifest>();
        var additional = configuration["FlowPlugins:ManifestDirectory"];
        if (additional is not null && Directory.Exists(additional))
        {
            foreach (var file in Directory.EnumerateFiles(additional, "*.json").Order())
            {
                var name = Path.GetFileName(file);
                try
                {
                    var extra = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(file), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                        ?? throw new InvalidOperationException("Le manifeste est vide.");
                    var (normalized, references) = CheckThirdParty(extra, manifests, null);
                    Register(normalized, references);
                    manifests.Add(normalized);
                }
                catch (Exception exception) when (exception is InvalidOperationException or JsonException or IOException or ArgumentException or NotSupportedException)
                {
                    rejected.Add(new RejectedPluginManifest(name, exception.Message));
                }
            }
        }
        rejectedList = rejected;
        plugins = manifests;
    }

    /// <summary>
    /// Contrôle un manifeste tiers (fichier du dossier de manifestes ou paquet du Store) et retourne sa forme normalisée avec
    /// ses références. <paramref name="replacing"/> : identifiant du plugin que ce manifeste remplace (mise à jour d'un paquet).
    /// </summary>
    private (PluginManifest Manifest, List<FlowComponentReference> References) CheckThirdParty(PluginManifest extra, IReadOnlyList<PluginManifest> existing, string? replacing, bool native = false)
    {
        if (extra.Id is null || (native
                ? !Encoding.Core.Plugins.PluginPackage.NativeComponentPluginIds.Contains(extra.Id)
                : !PluginIdentity.IsValidPluginId(extra.Id)))
            throw new InvalidOperationException($"Identifiant de plugin invalide : « {extra.Id} » (minuscules, chiffres et tirets, avec un espace de noms optionnel « espace.nom »).");
        if (!native && Encoding.Core.Plugins.PluginPackage.NativeComponentPluginIds.Contains(extra.Id))
            throw new InvalidOperationException($"L'identifiant « {extra.Id} » est réservé à un paquet natif distribué par le Store.");
        var others = existing.Where(item => item.Id != replacing).ToList();
        if (others.Any(item => item.Id == extra.Id) || replacing is null && existing.Any(item => item.Id == extra.Id))
            throw new InvalidOperationException($"L’identifiant « {extra.Id} » est déjà utilisé par un autre plugin.");
        if (extra.ContractVersion != 1 || extra.SupportedHostContractVersions is null || !extra.SupportedHostContractVersions.Contains(1))
            throw new InvalidOperationException("Le manifeste doit déclarer contractVersion 1 et supportedHostContractVersions [1].");
        if (string.IsNullOrWhiteSpace(extra.Version) || !System.Text.RegularExpressions.Regex.IsMatch(extra.Version, @"^\d+\.\d+\.\d+$"))
            throw new InvalidOperationException("La version du plugin doit être de la forme X.Y.Z.");
        if (extra.Components is null) throw new InvalidOperationException("Le manifeste ne déclare aucun composant.");
        foreach (var component in extra.Components)
            if (!PluginIdentity.IsValidComponentId(component.Id))
                throw new InvalidOperationException($"Identifiant de composant invalide : « {component.Id} ».");
        extra = Normalize(extra);
        Validate(extra);
        var references = extra.Components.Select(component => Reference(extra, component)).ToList();
        var keys = references.Concat(extra.Components.SelectMany((component, index) =>
            (component.CompatiblePluginVersions ?? []).Select(version => references[index] with { PluginVersion = version }))).ToList();
        if (keys.Distinct().Count() != keys.Count) throw new InvalidOperationException("Une identité de composant est déclarée deux fois dans ce plugin.");
        foreach (var reference in references)
            if (others.Any(item => item.Components.Any(component => component.Id == reference.ComponentId)))
                throw new InvalidOperationException($"Le composant « {reference.ComponentId} » existe déjà dans un autre plugin.");
        if (keys.Any(key => components.ContainsKey(key) && key.PluginId != replacing)) throw new InvalidOperationException("Une identité de composant existe déjà dans un autre plugin.");
        return (extra, references);
    }

    /// <summary>
    /// Installe (ou met à jour) un plugin tiers à chaud, sans redémarrage. En mise à jour, les composants de l'ancienne version
    /// restent résolubles par les flows existants : leurs références pointent vers le descripteur de la nouvelle version, qui doit
    /// donc rester compatible avec son schéma (un changement incompatible exige un nouvel identifiant de composant).
    /// </summary>
    public bool TryInstall(PluginManifest manifest, out string reason, bool native = false)
    {
        lock (installGate)
        {
            try
            {
                var previous = plugins.SingleOrDefault(item => item.Id == manifest.Id);
                if (previous is not null && !storeInstalled.Contains(previous.Id))
                    throw new InvalidOperationException($"L’identifiant « {manifest.Id} » est déjà utilisé par un plugin fourni avec l’application.");
                if (previous is not null && Version.Parse(manifest.Version) <= Version.Parse(previous.Version))
                    throw new InvalidOperationException($"La version {manifest.Version} n’est pas plus récente que la version installée ({previous.Version}).");
                var inherited = previous is null ? [] : previous.Components.SelectMany(component => new[] { previous.Version }.Concat(component.CompatiblePluginVersions ?? [])).Distinct().ToArray();
                var withHistory = previous is null ? manifest : manifest with
                {
                    Components = manifest.Components.Select(component => component with
                    {
                        CompatiblePluginVersions = (component.CompatiblePluginVersions ?? []).Concat(inherited).Distinct().ToArray()
                    }).ToArray()
                };
                if (native && !Encoding.Core.Plugins.PluginPackage.NativeComponentPluginIds.Contains(manifest.Id))
                    throw new InvalidOperationException("Ce plugin natif n'est pas une famille de composants moteur autorisée.");
                var (normalized, references) = CheckThirdParty(withHistory, plugins, previous?.Id, native);
                if (previous is not null) RemoveRegistrations(previous.Id);
                Register(normalized, references);
                plugins = plugins.Where(item => item.Id != normalized.Id).Append(normalized).ToList();
                storeInstalled.Add(normalized.Id);
                fingerprints.Clear();
                reason = "";
                return true;
            }
            catch (InvalidOperationException exception)
            {
                reason = exception.Message;
                return false;
            }
        }
    }

    /// <summary>Retire un plugin installé depuis le Store. Les plugins fournis avec l'application ne sont jamais retirés.</summary>
    public bool Uninstall(string pluginId)
    {
        lock (installGate)
        {
            if (!storeInstalled.Remove(pluginId)) return false;
            RemoveRegistrations(pluginId);
            plugins = plugins.Where(item => item.Id != pluginId).ToList();
            fingerprints.Clear();
            return true;
        }
    }

    private void RemoveRegistrations(string pluginId)
    {
        foreach (var key in components.Keys.Where(key => key.PluginId == pluginId).ToList())
        {
            components.TryRemove(key, out _);
            runtimeVersions.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Retire d'une configuration effective les champs de portée « server » avant de l'envoyer à un worker : le secret d'un
    /// webhook n'a de sens que sur le serveur et un plugin exécuté sur un worker n'a aucune raison de le recevoir.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>> ForWorker(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>> settings)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>(StringComparer.Ordinal);
        foreach (var (pluginId, values) in settings)
        {
            var serverOnly = Plugins.SingleOrDefault(plugin => plugin.Id == pluginId)?.ConfigurationSchema?.Fields
                .Where(field => field.Scope == "server").Select(field => field.Id).ToHashSet(StringComparer.Ordinal);
            result[pluginId] = serverOnly is { Count: > 0 }
                ? values.Where(pair => !serverOnly.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                : values;
        }
        return result;
    }

    /// <summary>
    /// Vrai si la capacité annoncée par un worker décrit le même contrat que le catalogue du serveur. Un worker plus ancien
    /// qui n'annonce pas d'empreinte reste accepté (compatibilité de déploiement progressif).
    /// </summary>
    public bool FingerprintMatches(string pluginId, string? announced)
    {
        if (announced is null) return true;
        var plugin = Plugins.SingleOrDefault(item => item.Id == pluginId);
        return plugin is null || fingerprints.GetOrAdd(pluginId, _ => PluginManifestFingerprint.Compute(plugin)) == announced;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> fingerprints = new(StringComparer.Ordinal);

    /// <summary>Manifestes tiers écartés au chargement (fichier et motif), pour affichage dans la page Plugins.</summary>
    public IReadOnlyList<RejectedPluginManifest> Rejected => rejectedList;
    private volatile IReadOnlyList<RejectedPluginManifest> rejectedList = [];

    /// <summary>Signale un paquet installé qui n'a pas pu être rechargé au démarrage.</summary>
    public void AddRejected(string file, string reason)
    {
        lock (installGate) rejectedList = rejectedList.Append(new RejectedPluginManifest(file, reason)).ToList();
    }

    private static FlowComponentReference Reference(PluginManifest installed, FlowComponentDescriptor component) =>
        new(installed.Id, installed.Version, component.Id, component.Version, component.ConfigurationSchema.Version);

    private static PluginManifest Normalize(PluginManifest installed) => installed with
    {
        Components = installed.Components.Select(component => component with
        {
            ConfigurationSchema = component.ConfigurationSchema with
            {
                Fields = component.ConfigurationSchema.Fields.Select(field => field.Type == "condition-tree" ? field with
                {
                    ConditionFields = ConditionTree.Fields,
                    ConditionPresets = ConditionTree.Presets,
                    DefaultValue = field.DefaultValue ?? ConditionTree.Serialize(new ConditionGroup("all", []))
                } : field.Type == "encoding-profile" ? field with
                {
                    DefaultValue = field.DefaultValue ?? JsonSerializer.SerializeToElement(BuiltInEncodingProfiles.Balanced, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                } : field).ToArray()
            }
        }).ToArray()
    };

    private static void Validate(PluginManifest installed)
    {
        if (installed.ConfigurationSchema is { } pluginSchema)
        {
            if (pluginSchema.Version != 1) throw new InvalidOperationException($"Schéma de configuration invalide : {installed.Id} (version de schéma {pluginSchema.Version}, 1 attendu).");
            if (pluginSchema.Fields.Select(field => field.Id).Distinct(StringComparer.Ordinal).Count() != pluginSchema.Fields.Count)
                throw new InvalidOperationException($"Schéma de configuration invalide : {installed.Id} (identifiant de champ dupliqué).");
            if (pluginSchema.Fields.FirstOrDefault(field => field.Scope is not (null or "server" or "worker")) is { } badScope)
                throw new InvalidOperationException($"Schéma de configuration invalide : {installed.Id} (champ « {badScope.Id} » : portée « {badScope.Scope} » inconnue, « server » ou « worker » attendu).");
            if (pluginSchema.Fields.FirstOrDefault(field => field.Type is not ("text" or "secret" or "number" or "boolean" or "select")) is { } badField)
                throw new InvalidOperationException($"Schéma de configuration invalide : {installed.Id} (champ « {badField.Id} » de type « {badField.Type} » non pris en charge).");
        }
        if (installed.PublicationGuard is { } guard)
        {
            var guardComponent = installed.Components.SingleOrDefault(item => item.Id == guard.ComponentId);
            var pluginFields = installed.ConfigurationSchema?.Fields.ToDictionary(field => field.Id, StringComparer.Ordinal) ?? [];
            var componentFields = guardComponent?.ConfigurationSchema.Fields.Select(field => field.Id).ToHashSet(StringComparer.Ordinal) ?? [];
            var outputs = guardComponent?.Outputs.Select(output => output.Id).ToHashSet(StringComparer.Ordinal) ?? [];
            if (guardComponent is null
                || !pluginFields.TryGetValue(guard.EnabledField, out var enabled) || enabled.Type != "boolean"
                || !pluginFields.TryGetValue(guard.MaxWaitField, out var maxWait) || maxWait.Type != "number"
                || guard.WaitOutputs.Count == 0 || guard.AllowOutputs.Count == 0
                || guard.WaitOutputs.Concat(guard.AllowOutputs).Any(id => !outputs.Contains(id))
                || guard.WaitOutputs.Intersect(guard.AllowOutputs).Any()
                || guard.FixedConfiguration.Keys.Concat(guard.ConfigurationFields.Keys).Any(id => !componentFields.Contains(id))
                || guard.FixedConfiguration.Keys.Intersect(guard.ConfigurationFields.Keys).Any()
                || guard.ConfigurationFields.Values.Any(id => !pluginFields.ContainsKey(id)))
                throw new InvalidOperationException($"Garde de publication invalide : {installed.Id}.");
        }
        foreach (var component in installed.Components)
        {
            var fields = component.ConfigurationSchema.Fields;
            string? reason = null;
            if (string.IsNullOrWhiteSpace(component.Id) || string.IsNullOrWhiteSpace(component.Version)) reason = "identifiant ou version vide";
            else if (component.MinimumRuntimePluginVersion is not null && !Version.TryParse(component.MinimumRuntimePluginVersion, out _)) reason = "minimumRuntimePluginVersion illisible";
            else if (fields.Any(field => field.RequiresPluginVersion is not null && !Version.TryParse(field.RequiresPluginVersion, out _))) reason = "requiresPluginVersion illisible";
            else if (component.TimeoutSeconds is < 1 or > 21600) reason = "timeoutSeconds hors de 1 à 21600";
            else if (!MediaEffects.IsValid(component.MediaEffect)) reason = "mediaEffect inconnu (readOnly, producesOutput, modifiesCurrent attendus)";
            else if (component.Category is not null && !FlowComponentCategories.Ids.Contains(component.Category)) reason = $"catégorie « {component.Category} » inconnue";
            else if (component.ConfigurationSchema.Version != 1) reason = $"version de schéma {component.ConfigurationSchema.Version} (1 attendu)";
            else if (component.Outputs.Select(o => o.Id).Distinct(StringComparer.Ordinal).Count() != component.Outputs.Count) reason = "identifiant de sortie dupliqué";
            else if (fields.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count() != fields.Count) reason = "identifiant de champ dupliqué";
            else if (fields.FirstOrDefault(f => f.Type == "record-list" &&
                    (f.ItemFields is null || f.ItemFields.Count == 0 || f.ItemFields.Any(child => child.Type is not ("text" or "number" or "boolean" or "select"))
                    || f.ItemFields.Select(child => child.Id).Distinct().Count() != f.ItemFields.Count
                    || (f.MinItems ?? 0) < 0 || (f.MaxItems ?? 64) > 64 || (f.MinItems ?? 0) > (f.MaxItems
                        ?? 64))) is { } badList) reason = $"liste « {badList.Id} » invalide (champs d’élément, bornes 0 à 64)";
            else if (fields.FirstOrDefault(f => f.Importance is not (null or "basic" or "advanced")
                    || f.Editor is not (null or "languages" or "language" or "node" or "node-group" or "flow-node" or "indices" or "labels" or "artifact" or "font-set" or "resource")
                    || f.VisibleWhenFieldId is not null
                        && !fields.Any(other => other.Id == f.VisibleWhenFieldId)) is { } badOption) reason = $"champ « {badOption.Id} » : importance, éditeur ou visibleWhenFieldId invalide";
            else if (fields.FirstOrDefault(f => f.Type is not ("text" or "number" or "boolean" or "select" or "condition-tree" or "script-reference" or "encoding-profile" or "video-encoder" or "record-list")) is { } badType) reason = $"champ « {badType.Id} » : type « {badType.Type} » non pris en charge";
            if (reason is not null) throw new InvalidOperationException($"Descripteur invalide : {component.Id} ({reason}).");
        }
    }

    private void Register(PluginManifest installed, IReadOnlyList<FlowComponentReference> references)
    {
        for (var index = 0; index < references.Count; index++)
        {
            var component = installed.Components[index];
            var reference = references[index];
            if (!components.TryAdd(reference, component)) throw new InvalidOperationException($"Composant dupliqué : {component.Id}.");
            runtimeVersions[reference] = installed.Version;
            foreach (var version in component.CompatiblePluginVersions ?? [])
            {
                var compatible = reference with { PluginVersion = version };
                if (string.IsNullOrWhiteSpace(version) || !components.TryAdd(compatible, component))
                    throw new InvalidOperationException($"Référence compatible dupliquée ou invalide : {component.Id}.");
                runtimeVersions[compatible] = installed.Version;
            }
        }
    }

    private static readonly Dictionary<string, (string PluginId, string ComponentId)> LegacyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        // MKV plugin
        ["base-remux-matroska"] = ("mkv", "mkv-remux-matroska"),
        ["base-remove-tracks"] = ("mkv", "mkv-remove-tracks"),
        ["base-extract-track"] = ("mkv", "mkv-extract-track"),
        ["base-reorder-tracks"] = ("mkv", "mkv-reorder-tracks"),
        ["base-edit-track-properties"] = ("mkv", "mkv-edit-track-properties"),
        ["base-edit-matroska-metadata"] = ("mkv", "mkv-edit-matroska-metadata"),
        ["base-extract-attachments"] = ("mkv", "mkv-extract-attachments"),
        ["base-remove-attachments"] = ("mkv", "mkv-remove-attachments"),
        ["base-extract-chapters"] = ("mkv", "mkv-extract-chapters"),
        ["base-replace-chapters"] = ("mkv", "mkv-replace-chapters"),
        // Images plugin
        ["base-convert-image"] = ("images", "image-convert"),
        ["base-resize-image"] = ("images", "image-resize"),
        ["base-crop-image"] = ("images", "image-crop"),
        // Probe plugin
        ["base-probe-media"] = ("probe", "probe-media"),
        ["base-probe-tracks"] = ("probe", "probe-tracks"),
        ["base-probe-video"] = ("probe", "probe-video"),
        ["base-probe-audio"] = ("probe", "probe-audio"),
        ["base-inspect-media"] = ("probe", "probe-inspect-media"),
        ["base-inspect-hdr"] = ("probe", "probe-inspect-hdr"),
        ["base-inspect-track"] = ("probe", "probe-inspect-track"),
        ["base-export-media-report"] = ("probe", "probe-export-report"),
        // Video plugin
        ["base-remux-media"] = ("video", "video-remux-media"),
        ["base-transcode-video"] = ("video", "video-transcode"),
        ["base-resize-video"] = ("video", "video-resize"),
        ["base-auto-crop"] = ("video", "video-auto-crop"),
        ["base-deinterlace-video"] = ("video", "video-deinterlace"),
        ["base-video-thumbnail"] = ("video", "video-thumbnail"),
        ["base-verify-decoding"] = ("video", "video-verify-decoding"),
        ["base-file-size-guard"] = ("video", "video-file-size-guard"),
        // Audio plugin
        ["base-transcode-audio"] = ("audio", "audio-transcode"),
        ["base-normalize-audio"] = ("audio", "audio-normalize"),
        ["base-extract-audio"] = ("audio", "audio-extract"),
        ["base-audio-rules"] = ("audio", "audio-rules"),
        ["base-add-audio-track"] = ("audio", "audio-add-track"),
        ["base-filter-languages"] = ("audio", "audio-filter-languages"),
        ["base-remove-commentary"] = ("audio", "audio-remove-commentary"),
        // Subtitles plugin
        ["base-extract-subtitles"] = ("subtitles", "subtitles-extract"),
        ["base-subtitle-manage"] = ("subtitles", "subtitles-manage"),
        ["base-add-fonts"] = ("subtitles", "subtitles-add-fonts"),
        ["base-add-subtitle-track"] = ("subtitles", "subtitles-add-track"),
        ["base-track-names"] = ("subtitles", "subtitles-track-names"),
    };

    public static bool TryGetLegacyAlias(string componentId, out (string PluginId, string ComponentId) target) =>
        LegacyAliases.TryGetValue(componentId, out target);

    /// <summary>New effects require an observed runtime version, including through a historical Base alias.</summary>
    public (string PluginId, string Version)? MinimumRuntime(FlowComponentReference reference, IReadOnlyDictionary<string, JsonElement> values)
    {
        var component = Resolve(reference);
        if (component is null) return null;
        var minimum = component.MinimumRuntimePluginVersion;
        foreach (var field in component.ConfigurationSchema.Fields)
        {
            if (field.RequiresPluginVersion is null || !values.TryGetValue(field.Id, out var value)
                || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
            var active = field.RequiresPluginVersionValue is { } expected
                ? value.ValueKind == JsonValueKind.String && value.GetString() == expected
                : field.DefaultValue is not { } fallback || !JsonElement.DeepEquals(value, fallback);
            if (active && (minimum is null || Version.Parse(field.RequiresPluginVersion) > Version.Parse(minimum)))
                minimum = field.RequiresPluginVersion;
        }
        var pluginId = reference.PluginId == "base" && TryGetLegacyAlias(reference.ComponentId, out var alias) ? alias.PluginId : reference.PluginId;
        return minimum is null ? null : (pluginId, minimum);
    }

    public FlowComponentDescriptor? Resolve(FlowComponentReference reference)
    {
        if (components.TryGetValue(reference, out var descriptor))
            return descriptor;

        if (reference.PluginId == "base" && LegacyAliases.TryGetValue(reference.ComponentId, out var target))
        {
            var targetRef = FindReference(target.ComponentId, target.PluginId);
            if (targetRef is not null && components.TryGetValue(targetRef, out var aliased))
                return aliased;
        }

        return null;
    }

    public string? RuntimePluginVersion(FlowComponentReference reference)
    {
        if (runtimeVersions.TryGetValue(reference, out var v))
            return v;

        if (reference.PluginId == "base" && LegacyAliases.TryGetValue(reference.ComponentId, out var target))
        {
            var targetRef = FindReference(target.ComponentId, target.PluginId);
            if (targetRef is not null && runtimeVersions.TryGetValue(targetRef, out var aliasedVersion))
                return aliasedVersion;
        }

        return null;
    }

    public FlowComponentReference? FindReference(string componentId, string? pluginId = null)
    {
        foreach (var manifest in Plugins)
        {
            if (pluginId is not null && !string.Equals(manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase))
                continue;
            var comp = manifest.Components.FirstOrDefault(c => string.Equals(c.Id, componentId, StringComparison.OrdinalIgnoreCase));
            if (comp is not null)
            {
                return new FlowComponentReference(
                    manifest.Id,
                    manifest.Version,
                    comp.Id,
                    comp.Version,
                    comp.ConfigurationSchema.Version);
            }
        }

        if (LegacyAliases.TryGetValue(componentId, out var target))
        {
            return FindReference(target.ComponentId, target.PluginId);
        }

        return null;
    }
}


/// <summary>Manifeste de plugin tiers écarté au chargement.</summary>
public sealed record RejectedPluginManifest(string File, string Reason);
