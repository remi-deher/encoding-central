using System.Security.Claims;
using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using Encoding.Api.Executions;
using Encoding.Api.Flows;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Libraries;
using Encoding.Api.Logging;
using Encoding.Api.Nodes;
using Encoding.Api.Plugins;
using Encoding.Api.Resources;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Logging;
using Encoding.Contracts.Plugins;
using Encoding.Core.Logging;
using Encoding.Contracts.Tools;

namespace Encoding.Api.Mcp;

public sealed class McpPluginsHandler(
    FlowComponentRegistry components,
    PluginConfigurationRepository pluginConfigurations,
    PluginConnectionTester pluginTester,
    FlowRepository flows,
    SystemLogRingBuffer ringBuffer,
    SystemLogRepository systemLogs,
    Encoding.Api.Updates.ReleaseService releases)
    : IMcpDomainHandler
{
    private static JsonSerializerOptions JsonOptions => McpToolHelpers.JsonOptions;

    private static McpCallToolResult Invalid(string message) => McpToolHelpers.Invalid(message);
    private static McpCallToolResult Denied(string message) => McpToolHelpers.Denied(message);
    private static bool TryGuid(JsonElement args, string name, out Guid value) => McpToolHelpers.TryGuid(args, name, out value);
    private static bool TryInt(JsonElement args, string name, out int value) => McpToolHelpers.TryInt(args, name, out value);
    private static bool TryDeserialize<T>(JsonElement args, out T value) where T : class => McpToolHelpers.TryDeserialize(args, out value);

    public IReadOnlyCollection<string> ToolNames { get; } =
    [
        "list_plugins",
        "get_plugin_configuration",
        "save_plugin_configuration",
        "test_plugin_connection",
        "get_flow_plugin_configuration",
        "save_flow_plugin_configuration",
        "list_media_tools",
        "get_media_tool_info",
        "validate_tool_definition",
        "validate_plugin_manifest",
        "get_system_logs",
        "get_version",
        "get_updates"
    ];

    public async Task<McpCallToolResult> HandleAsync(string toolName, JsonElement arguments, bool isAdmin, CancellationToken ct) =>
        toolName switch
        {
            "list_plugins" => ToolListPlugins(),
            "get_plugin_configuration" => await ToolGetPluginConfigurationAsync(arguments, ct),
            "save_plugin_configuration" => await ToolSavePluginConfigurationAsync(arguments, isAdmin, ct),
            "test_plugin_connection" => await ToolTestPluginConnectionAsync(arguments, ct),
            "get_flow_plugin_configuration" => await ToolGetFlowPluginConfigurationAsync(arguments, ct),
            "save_flow_plugin_configuration" => await ToolSaveFlowPluginConfigurationAsync(arguments, isAdmin, ct),
            "list_media_tools" => ToolListMediaTools(),
            "get_media_tool_info" => ToolGetMediaToolInfo(arguments),
            "validate_tool_definition" => ToolValidateToolDefinition(arguments),
            "validate_plugin_manifest" => ToolValidatePluginManifest(arguments),
            "get_system_logs" => await ToolGetSystemLogsAsync(arguments, ct),
            "get_version" => ToolGetVersion(),
            "get_updates" => await ToolGetUpdatesAsync(ct),
            _ => McpCallToolResult.Text($"Outil introuvable : '{toolName}'", isError: true)
        };

    public McpCallToolResult ToolListPlugins()
    {
        var list = components.Plugins.Select(p => new
        {
            p.Id,
            p.Name,
            p.Version,
            p.ConfigurationSchema,
            componentsCount = p.Components.Count,
            components = p.Components.Select(c => new
            {
                c.Id,
                c.Name,
                c.Description,
                c.Kind,
                c.MediaType
            })
        }).ToList();

        return McpCallToolResult.Text(JsonSerializer.Serialize(list, JsonOptions));
    }

    public async Task<McpCallToolResult> ToolGetPluginConfigurationAsync(JsonElement arguments, CancellationToken ct)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("pluginId", out var idProp) || idProp.GetString() is not { Length: > 0 } pluginId)
            return McpCallToolResult.Text("Paramètre 'pluginId' requis.", isError: true);

        var plugin = components.Plugins.SingleOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        if (plugin is null)
            return McpCallToolResult.Text($"Plugin '{pluginId}' introuvable.", isError: true);

        var config = await pluginConfigurations.GetGlobalAsync(plugin, ct);
        return McpCallToolResult.Text(JsonSerializer.Serialize(config, JsonOptions));
    }

    public async Task<McpCallToolResult> ToolSavePluginConfigurationAsync(JsonElement arguments, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin)
            return McpCallToolResult.Text("Permission refusée : la configuration des plugins requiert le rôle administrateur.", isError: true);

        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("pluginId", out var idProp) || idProp.GetString() is not { Length: > 0 } pluginId
            || !arguments.TryGetProperty("values", out var valsProp) || valsProp.ValueKind != JsonValueKind.Object)
        {
            return McpCallToolResult.Text("Paramètres 'pluginId' et 'values' (objet) requis.", isError: true);
        }

        var plugin = components.Plugins.SingleOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        if (plugin is null)
            return McpCallToolResult.Text($"Plugin '{pluginId}' introuvable.", isError: true);

        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in valsProp.EnumerateObject())
            values[prop.Name] = prop.Value.Clone();

        var clearSecrets = new List<string>();
        if (arguments.TryGetProperty("clearSecrets", out var csProp) && csProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in csProp.EnumerateArray())
                if (item.GetString() is { Length: > 0 } sec) clearSecrets.Add(sec);
        }

        var expectedRev = arguments.TryGetProperty("expectedRevision", out var revProp) && revProp.TryGetInt32(out var r) ? r : 0;
        var request = new SavePluginConfigurationRequest(expectedRev, values, clearSecrets);

        var saved = await pluginConfigurations.SaveGlobalAsync(plugin, request, ct);
        return saved is not null
            ? McpCallToolResult.Text(JsonSerializer.Serialize(new { message = "Configuration du plugin enregistrée.", configuration = saved }, JsonOptions))
            : McpCallToolResult.Text("Échec d'enregistrement de la configuration.", isError: true);
    }

    public async Task<McpCallToolResult> ToolTestPluginConnectionAsync(JsonElement arguments, CancellationToken ct)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("pluginId", out var idProp) || idProp.GetString() is not { Length: > 0 } pluginId)
            return McpCallToolResult.Text("Paramètre 'pluginId' requis.", isError: true);

        var plugin = components.Plugins.SingleOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        if (plugin is null)
            return McpCallToolResult.Text($"Plugin '{pluginId}' introuvable.", isError: true);

        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (arguments.TryGetProperty("values", out var valsProp) && valsProp.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in valsProp.EnumerateObject())
                values[prop.Name] = prop.Value.Clone();
        }

        var resolvedValues = await pluginConfigurations.ResolveGlobalForConnectionTestAsync(plugin, values, ct);
        var testResult = await pluginTester.TestAsync(plugin.Id, resolvedValues, ct);

        return McpCallToolResult.Text(JsonSerializer.Serialize(testResult, JsonOptions));
    }

    public async Task<McpCallToolResult> ToolGetFlowPluginConfigurationAsync(JsonElement args, CancellationToken ct)
    {
        if (!TryGuid(args, "flowId", out var flowId) || !args.TryGetProperty("pluginId", out var pluginValue)
            || pluginValue.ValueKind != JsonValueKind.String) return Invalid("Paramètres 'flowId' et 'pluginId' requis.");
        var plugin = components.Plugins.SingleOrDefault(item => item.Id == pluginValue.GetString());
        if (plugin is null) return Invalid("Plugin introuvable.");
        if (await flows.GetAsync(flowId, null, ct) is null && await flows.DraftAsync(flowId, ct) is null)
            return Invalid("Flow introuvable.");
        var configuration = await pluginConfigurations.GetFlowAsync(flowId, plugin, ct);
        return configuration is null ? Invalid("Flow introuvable.") : McpCallToolResult.Text(JsonSerializer.Serialize(configuration, JsonOptions));
    }

    public async Task<McpCallToolResult> ToolSaveFlowPluginConfigurationAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La configuration d’un plugin dans un flow exige le rôle administrateur.");
        if (!TryGuid(args, "flowId", out var flowId) || !args.TryGetProperty("pluginId", out var pluginValue)
            || pluginValue.ValueKind != JsonValueKind.String || !TryDeserialize<Encoding.Contracts.Plugins.SavePluginConfigurationRequest>(args, out var request))
            return Invalid("Paramètres de configuration du plugin dans le flow invalides.");
        var plugin = components.Plugins.SingleOrDefault(item => item.Id == pluginValue.GetString());
        if (plugin is null) return Invalid("Plugin introuvable.");
        if (await flows.GetAsync(flowId, null, ct) is null && await flows.DraftAsync(flowId, ct) is null)
            return Invalid("Flow introuvable.");
        try
        {
            var saved = await pluginConfigurations.SaveFlowAsync(flowId, plugin, request, ct);
            return saved is null ? Invalid("La configuration du plugin n’a pas pu être enregistrée.") : McpCallToolResult.Text(JsonSerializer.Serialize(saved, JsonOptions));
        }
        catch (PluginConfigurationValidationException exception) { return Invalid(exception.Message); }
    }

    // --- 8. OUTILS MÉDIA & VALIDATION IA ---

    public McpCallToolResult ToolListMediaTools()
    {
        var tools = new object[]
        {
            new
            {
                id = "ffmpeg",
                name = "FFmpeg",
                description = "Moteur multimédia complet pour l'encodage, le décodage, le transcodage, le filtrage et la normalisation audio/vidéo.",
                executables = new[] { "ffmpeg" },
                mediaTypes = new[] { "video", "audio", "subtitles", "general" },
                commonOperations = new[] { "transcode_video", "transcode_audio", "normalize_audio_ebur128", "extract_track", "change_container", "create_thumbnail", "crop_detect" },
                scriptUsage = "tools.run(\"ffmpeg\", [\"-hide_banner\", \"-i\", \"source.mkv\", ...])",
                notes = "Outil de référence système conservé pour les sondes matérielles (0064). S'exécute sous Landlock sur les fichiers temporaires."
            },
            new
            {
                id = "ffprobe",
                name = "FFprobe",
                description = "Analyseur de flux et de conteneurs pour extraire les métadonnées techniques détaillées.",
                executables = new[] { "ffprobe" },
                mediaTypes = new[] { "video", "audio", "subtitles", "general" },
                commonOperations = new[] { "probe_streams", "probe_format", "read_tags", "detect_codecs" },
                scriptUsage = "tools.run(\"ffprobe\", [\"-v\", \"quiet\", \"-print_format\", \"json\", \"-show_format\", \"-show_streams\", \"file.mkv\"])",
                notes = "Sortie JSON recommandée pour un parsing fiable par les scripts et l'IA."
            },
            new
            {
                id = "mkvtoolnix",
                name = "MKVToolNix",
                description = "Suite d'outils spécialisée pour le format Matroska (MKV) : multiplexage, extraction et modification de propriétés en place.",
                executables = new[] { "mkvmerge", "mkvextract", "mkvpropedit" },
                mediaTypes = new[] { "video", "audio", "subtitles" },
                commonOperations = new[] { "remux_mkv", "extract_subtitles_ass", "extract_audio_raw", "edit_track_names", "set_default_track" },
                scriptUsage = "tools.run(\"mkvmerge\", [\"-o\", \"output.mkv\", \"--no-audio\", \"input.mkv\"])",
                notes = "Attention : mkvmerge renvoie 0 pour succès total et 1 en présence d'avertissements (considéré valide dans Encoding)."
            },
            new
            {
                id = "mediainfo",
                name = "MediaInfo",
                description = "Extracteur d'informations techniques approfondies (profils HDR, Dolby Vision, agencement de canaux audio).",
                executables = new[] { "mediainfo" },
                mediaTypes = new[] { "video", "audio", "general" },
                commonOperations = new[] { "read_hdr_metadata", "read_audio_channels_layout", "export_json" },
                scriptUsage = "tools.run(\"mediainfo\", [\"--Output=JSON\", \"file.mkv\"])",
                notes = "Complément indispensable pour les métadonnées HDR10 / Dolby Vision."
            },
            new
            {
                id = "imagemagick",
                name = "ImageMagick",
                description = "Traitement, conversion et redimensionnement d'images, jaquettes et miniatures.",
                executables = new[] { "magick", "convert" },
                mediaTypes = new[] { "image" },
                commonOperations = new[] { "resize", "crop", "convert_format", "optimize_poster" },
                scriptUsage = "tools.run(\"convert\", [\"input.png\", \"-resize\", \"600x900\", \"output.jpg\"])",
                notes = "Utilisé pour la génération et retouche de visuels."
            },
            new
            {
                id = "python",
                name = "Python 3 Runtime",
                description = "Moteur d'exécution pour les plugins d'intégration et scripts avancés.",
                executables = new[] { "python3", "python" },
                mediaTypes = new[] { "general" },
                commonOperations = new[] { "run_plugin_process", "execute_custom_script" },
                scriptUsage = "Utilise le SDK encoding_plugin_sdk.py pour communiquer avec le worker.",
                notes = "S'exécute sous le bac à sable Landlock (0032). Aucun accès aux secrets du nœud."
            },
            new
            {
                id = "javascript",
                name = "Node.js / JavaScript Runtime",
                description = "Moteur de scripts légers et évaluation d'expressions conditionnelles.",
                executables = new[] { "node" },
                mediaTypes = new[] { "general" },
                commonOperations = new[] { "evaluate_conditions", "lightweight_script" },
                scriptUsage = "base-script avec moteur JavaScript.",
                notes = "Confiné sous Landlock."
            }
        };

        return McpCallToolResult.Text(JsonSerializer.Serialize(tools, JsonOptions));
    }

    public McpCallToolResult ToolGetMediaToolInfo(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("toolId", out var idProp) || idProp.GetString() is not { Length: > 0 } toolId)
            return McpCallToolResult.Text("Paramètre 'toolId' requis.", isError: true);

        var id = toolId.Trim().ToLowerInvariant();

        object? result = id switch
        {
            "ffmpeg" => new
            {
                id = "ffmpeg",
                name = "FFmpeg",
                executables = new[] { "ffmpeg" },
                role = "Transcodage, filtrage et encodage vidéo/audio universel",
                conformanceRequirements = new[]
                {
                    "Option -version renvoyant une version >= 5.1",
                    "Option -hide_banner -encoders renvoyant la liste des encodeurs reconnus",
                    "Smoke test synthétique CPU (1s mire de test encodée sans erreur)"
                },
                safeUsageGuidelines = new[]
                {
                    "Toujours utiliser -hide_banner et -nostats pour éviter de polluer les logs",
                    "Spécifier -y pour écraser la sortie temporaire de travail",
                    "Ne jamais interpoler de chemins sans guillemets ou dans un shell arbitraire",
                    "Consulter VideoEncoderCatalog pour les options de qualité par encodeur"
                },
                supportedVideoEncoders = VideoEncoderCatalog.All.Select(e => new
                {
                    e.Id,
                    e.Codec,
                    e.Name,
                    e.Acceleration,
                    e.QualityMode,
                    e.QualityOption,
                    e.PresetOption,
                    qualityRange = new { min = e.QualityMinimum, max = e.QualityMaximum, @default = e.DefaultQuality },
                    presets = e.Presets.Select(p => new { p.Id, p.Name, p.Value })
                }).ToList()
            },
            "ffprobe" => new
            {
                id = "ffprobe",
                name = "FFprobe",
                executables = new[] { "ffprobe" },
                role = "Analyse et sondage de flux multimédia",
                recommendedCommand = new[] { "ffprobe", "-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", "{filePath}" },
                expectedOutputFormat = "JSON structuré contenant les tableaux 'streams' (codec_name, width, height, channels, tags) et 'format' (duration, size, bit_rate)."
            },
            "mkvtoolnix" => new
            {
                id = "mkvtoolnix",
                name = "MKVToolNix",
                executables = new[] { "mkvmerge", "mkvextract", "mkvpropedit" },
                subTools = new[]
                {
                    new { executable = "mkvmerge", purpose = "Remuxer des conteneurs, sélectionner/ordonner des flux et ajouter des pistes.",
                        exitCodeRules = "Code 0 = Succès, Code 1 = Succès avec avertissements mineurs (valide), Code > 1 = Erreur critique." },
                    new { executable = "mkvextract", purpose = "Extraire des pistes de sous-titres (ASS/SRT), pistes audio natives, chapitres ou pièces jointes.", exitCodeRules = "Code 0 = Succès." },
                    new { executable = "mkvpropedit",
                        purpose = "Modifier les métadonnées de pistes (titre, langue, flag 'default', flag 'forced') directement sur la copie de travail sans réécriture intégrale.",
                        exitCodeRules = "Code 0 = Succès, Code 1 = Avertissements." }
                }
            },
            "mediainfo" => new
            {
                id = "mediainfo",
                name = "MediaInfo",
                executables = new[] { "mediainfo" },
                role = "Inspection avancée des métadonnées de diffusion et mastering",
                recommendedCommand = new[] { "mediainfo", "--Output=JSON", "{filePath}" },
                keyFields = new[] { "MasteringDisplay_ColorPrimaries", "MasteringDisplay_Luminance", "MaxCLL", "MaxFALL", "HDR_Format", "Format_Commercial_IfAny" }
            },
            "imagemagick" => new
            {
                id = "imagemagick",
                name = "ImageMagick",
                executables = new[] { "magick", "convert" },
                role = "Conversion et retouche graphique d'affiches/miniatures",
                typicalCommands = new[]
                {
                    "convert input.jpg -resize 300x450^ -gravity center -extent 300x450 poster.jpg",
                    "convert input.png -strip -quality 85 output.webp"
                }
            },
            _ => null
        };

        return result is not null
            ? McpCallToolResult.Text(JsonSerializer.Serialize(result, JsonOptions))
            : McpCallToolResult.Text($"Outil '{toolId}' non répertorié ou aucune documentation spécifique disponible.", isError: true);
    }

    public McpCallToolResult ToolValidateToolDefinition(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("definition", out var defProp) || defProp.ValueKind != JsonValueKind.Object)
            return McpCallToolResult.Text("Paramètre 'definition' (objet JSON décrivant tool.json) requis.", isError: true);

        var errors = new List<string>();
        var warnings = new List<string>();

        // 1. Id
        if (!defProp.TryGetProperty("id", out var idProp) || idProp.GetString() is not { Length: > 0 } id)
            errors.Add("Le champ 'id' est obligatoire.");
        else if (!Regex.IsMatch(id, "^[a-z0-9_-]+$"))
            errors.Add($"L'identifiant '{id}' doit être en minuscules et ne contenir que des lettres, chiffres, tirets et underscores.");

        // 2. Name
        if (!defProp.TryGetProperty("name", out var nameProp) || string.IsNullOrWhiteSpace(nameProp.GetString()))
            errors.Add("Le champ 'name' est obligatoire.");

        // 3. Executable
        if (!defProp.TryGetProperty("executable", out var execProp) || string.IsNullOrWhiteSpace(execProp.GetString()))
            errors.Add("Le champ 'executable' est obligatoire.");

        // 4. Probe
        if (!defProp.TryGetProperty("probe", out var probeProp) || probeProp.ValueKind != JsonValueKind.Object)
        {
            errors.Add("Le champ 'probe' est obligatoire (objet décrivant la commande de version et la regex).");
        }
        else
        {
            if (!probeProp.TryGetProperty("versionArgs", out var vArgs) || vArgs.ValueKind != JsonValueKind.Array || vArgs.GetArrayLength() == 0)
                errors.Add("Le champ 'probe.versionArgs' doit être un tableau non vide de chaînes (ex: ['--version']).");

            if (!probeProp.TryGetProperty("versionRegex", out var vRegex) || vRegex.GetString() is not { Length: > 0 } regexStr)
            {
                errors.Add("Le champ 'probe.versionRegex' est obligatoire.");
            }
            else
            {
                try
                {
                    _ = new Regex(regexStr);
                }
                catch (Exception ex)
                {
                    errors.Add($"L'expression régulière 'probe.versionRegex' est invalide : {ex.Message}");
                }
            }
        }

        // 5. Capabilities
        if (!defProp.TryGetProperty("capabilitiesExposed", out var capProp) || capProp.ValueKind != JsonValueKind.Array || capProp.GetArrayLength() == 0)
            warnings.Add("Le champ 'capabilitiesExposed' est vide ou absent. L'outil n'annoncera aucune capacité au cluster.");

        var isValid = errors.Count == 0;
        var response = new
        {
            isValid,
            errors,
            warnings,
            summary = isValid ? "Manifeste d'outil conforme aux spécifications de l'architecture 0064." : "Des erreurs bloquantes empêchent l'enregistrement de l'outil."
        };

        return McpCallToolResult.Text(JsonSerializer.Serialize(response, JsonOptions), isError: !isValid);
    }

    public McpCallToolResult ToolValidatePluginManifest(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("manifest", out var manProp) || manProp.ValueKind != JsonValueKind.Object)
            return McpCallToolResult.Text("Paramètre 'manifest' (objet JSON décrivant plugin.json) requis.", isError: true);

        var errors = new List<string>();
        var warnings = new List<string>();
        var suggestions = new List<string>();

        // 1. Id avec Namespace strict (0063)
        if (!manProp.TryGetProperty("id", out var idProp) || idProp.GetString() is not { Length: > 0 } id)
        {
            errors.Add("Le champ 'id' est obligatoire.");
        }
        else
        {
            if (!id.Contains('.'))
                errors.Add($"L'identifiant '{id}' doit respecter la règle de namespace strict '<namespace>.<plugin-name>' (ex: 'community.whisper').");
            if (id.StartsWith("official.", StringComparison.OrdinalIgnoreCase))
                warnings.Add("Le namespace 'official.' est réservé aux plugins du dépôt officiel Encoding.");
        }

        // 2. Version SemVer
        if (!manProp.TryGetProperty("version", out var verProp) || verProp.GetString() is not { Length: > 0 } ver)
            errors.Add("Le champ 'version' est obligatoire.");
        else if (!Regex.IsMatch(ver, @"^\d+\.\d+\.\d+(-[a-zA-Z0-9.-]+)?$"))
            errors.Add($"La version '{ver}' n'est pas un SemVer valide (ex: 1.0.0 ou 1.2.3-beta).");

        // 3. Runtime Python sous Landlock (0032)
        if (manProp.TryGetProperty("runtime", out var rtProp) && rtProp.ValueKind == JsonValueKind.Object)
        {
            if (rtProp.TryGetProperty("type", out var typeProp) && typeProp.GetString() != "python3")
                errors.Add("Le runtime du plugin doit être 'python3' pour s'exécuter sous le bac à sable Landlock (les exécutables .NET ou compilés arbitraires sont proscrits, architecture 0032).");
            if (!rtProp.TryGetProperty("entrypoint", out var epProp) || string.IsNullOrWhiteSpace(epProp.GetString()))
                errors.Add("Le champ 'runtime.entrypoint' (ex: 'main.py') est obligatoire.");
        }

        // 4. Composants et sémantique IA (0062)
        if (!manProp.TryGetProperty("components", out var compsProp) || compsProp.ValueKind != JsonValueKind.Array || compsProp.GetArrayLength() == 0)
        {
            warnings.Add("Le plugin ne déclare aucun composant de flow dans 'components'.");
        }
        else
        {
            var compIndex = 0;
            foreach (var comp in compsProp.EnumerateArray())
            {
                compIndex++;
                var compId = comp.TryGetProperty("id", out var cId) ? cId.GetString() : $"composant_{compIndex}";
                if (comp.TryGetProperty("mediaEffect", out var effect) && effect.ValueKind != JsonValueKind.Null
                    && (effect.ValueKind != JsonValueKind.String || !MediaEffects.IsValid(effect.GetString())))
                    errors.Add($"Composant '{compId}' : mediaEffect doit être readOnly, producesOutput ou modifiesCurrent.");

                if (!comp.TryGetProperty("description", out var cDesc) || (cDesc.GetString() ?? "").Length < 10)
                    suggestions.Add($"Composant '{compId}' : Fournir une description détaillée (> 10 caractères) pour aider l'IA à comprendre le rôle du bloc.");

                if (comp.TryGetProperty("outputs", out var outsProp) && outsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var outItem in outsProp.EnumerateArray())
                    {
                        var outId = outItem.TryGetProperty("id", out var oId) ? oId.GetString() : "?";
                        if (!outItem.TryGetProperty("description", out var oDesc) || string.IsNullOrWhiteSpace(oDesc.GetString()))
                            errors.Add($"Composant '{compId}', Sortie '{outId}' : La description sémantique est OBLIGATOIRE (règle 0062) pour éviter que l'IA n'inverse les branches de succès et d'échec.");
                    }
                }
                else
                {
                    errors.Add($"Composant '{compId}' : Doit déclarer un tableau 'outputs' avec au moins une sortie.");
                }
            }
        }

        var isValid = errors.Count == 0;
        var result = new
        {
            isValid,
            errors,
            warnings,
            suggestions,
            summary = isValid ? "Manifeste de plugin validé avec succès pour l'écosystème IA." : "Le manifeste comporte des erreurs de conformité bloquantes."
        };

        return McpCallToolResult.Text(JsonSerializer.Serialize(result, JsonOptions), isError: !isValid);
    }

    public async Task<McpCallToolResult> ToolGetSystemLogsAsync(JsonElement arguments, CancellationToken ct)
    {
        var minLevelStr = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("min_level", out var mlProp)
            ? mlProp.GetString()
            : "Warning";
        var minLevel = Enum.TryParse<SystemLogLevel>(minLevelStr, true, out var parsedLevel)
            ? parsedLevel
            : SystemLogLevel.Warning;

        var category = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("category", out var catProp)
            ? catProp.GetString()
            : null;

        var search = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("search", out var sProp)
            ? sProp.GetString()
            : null;

        var limit = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("limit", out var lProp) && lProp.TryGetInt32(out var l)
            ? Math.Clamp(l, 1, 200)
            : 50;

        Guid? nodeId = null;
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("node_id", out var nProp) &&
            Guid.TryParse(nProp.GetString(), out var parsedNodeId))
        {
            nodeId = parsedNodeId;
        }

        var filter = new SystemLogFilterQuery(minLevel, category, search, nodeId, limit);
        var inMemory = ringBuffer.Query(filter);

        if (filter.MinLevel >= SystemLogLevel.Warning || inMemory.Count < filter.Limit)
        {
            var dbEntries = await systemLogs.QueryAsync(filter, ct);
            if (dbEntries.Count > 0)
            {
                var seenIds = new HashSet<string>(inMemory.Select(e => e.Id));
                var combined = new List<SystemLogEntry>(inMemory);
                foreach (var dbEntry in dbEntries)
                {
                    if (seenIds.Add(dbEntry.Id))
                    {
                        combined.Add(dbEntry);
                    }
                }
                combined.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
                return McpCallToolResult.Text(JsonSerializer.Serialize(combined.Take(filter.Limit).ToList(), JsonOptions));
            }
        }

        return McpCallToolResult.Text(JsonSerializer.Serialize(inMemory, JsonOptions));
    }

    public McpCallToolResult ToolGetVersion() =>
        McpCallToolResult.Text(JsonSerializer.Serialize(new { version = releases.Installed.Version, commit = releases.Installed.Commit }, JsonOptions));

    public async Task<McpCallToolResult> ToolGetUpdatesAsync(CancellationToken ct) =>
        McpCallToolResult.Text(JsonSerializer.Serialize(await releases.GetUpdatesAsync(ct), JsonOptions));
}
