using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Plugins;

namespace Encoding.Contracts.Flows.Conditions;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ConditionGroup), "group")]
[JsonDerivedType(typeof(ConditionRule), "rule")]
public abstract record ConditionExpression;
public sealed record ConditionGroup(string Match, IReadOnlyList<ConditionExpression> Children) : ConditionExpression;
public sealed record ConditionRule(string Field, string Operator, JsonElement? Value = null, string? VariableName = null,
    string Scope = "current") : ConditionExpression;
public sealed record ConditionFieldDefinition(string Id, string Label, string Type, IReadOnlyList<FlowSelectOption> Operators,
    IReadOnlyList<string> SupportedFlowMediaTypes, bool RequiresMetadata = false, bool SupportsScope = false,
    bool RequiresVariableName = false, IReadOnlyList<FlowSelectOption>? Options = null, string? Unit = null);
public sealed record ConditionPreset(string Id, string Label, ConditionExpression Expression, IReadOnlyList<string> SupportedFlowMediaTypes);
public sealed record ConditionEvaluationContext(RuntimeContext Runtime, Guid SourceNodeId, Guid WorkerNodeId,
    Guid? LibraryId, string MediaPath, string SourceSha256, IReadOnlyList<string>? WorkerCodecs = null);
public sealed class ConditionEvaluationException(string message) : Exception(message);
public sealed record ConditionObservation(ConditionRule Rule, JsonElement? Actual, bool? Result, string? Error = null);

/// <summary>Shared, bounded and typed condition contract. Neither validation nor evaluation executes code.</summary>
public static class ConditionTree
{
    public const int MaxDepth = 8;
    public const int MaxNodes = 100;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowOutOfOrderMetadataProperties = true,
        MaxDepth = 32
    };
    private static readonly string[] AllMedia = ["video", "image", "music", "subtitles"];
    private static readonly FlowSelectOption[] TextOperators = [new("eq", "est égal à"), new("neq", "est différent de"),
        new("contains", "contient"), new("notContains", "ne contient pas"), new("exists", "est renseigné"), new("missing", "est absent")];
    private static readonly FlowSelectOption[] NumberOperators = [new("eq", "est égal à"), new("neq", "est différent de"),
        new("gt", "est supérieur à"), new("gte", "est supérieur ou égal à"), new("lt", "est inférieur à"), new("lte", "est inférieur ou égal à"),
        new("exists", "est renseigné"), new("missing", "est absent")];
    private static readonly FlowSelectOption[] BoolOperators = [new("eq", "est égal à"), new("neq", "est différent de"),
        new("exists", "est renseigné"), new("missing", "est absent")];
    private static readonly FlowSelectOption[] ListOperators = [new("contains", "contient"), new("notContains", "ne contient pas"),
        new("exists", "est renseigné"), new("missing", "est absent")];

    public static IReadOnlyList<ConditionFieldDefinition> Fields { get; } = [
        new("video.codec", "Codec de la première piste vidéo", "text", TextOperators, ["video", "image"], true, true,
            Options: [new("hevc", "H.265 / HEVC"), new("h264", "H.264"), new("av1", "AV1"), new("vp9", "VP9")]),
        new("video.count", "Nombre de pistes vidéo (hors images attachées)", "number", NumberOperators, ["video"], true, true),
        new("video.bitDepth", "Profondeur de couleur de la première piste vidéo", "number", NumberOperators, ["video"], true, true, Unit: "bits"),
        new("video.hdr", "La vidéo est HDR ou Dolby Vision", "boolean", BoolOperators, ["video"], true, true),
        new("video.width", "Largeur vidéo / image", "number", NumberOperators, ["video", "image"], true, true, Unit: "px"),
        new("video.height", "Hauteur vidéo / image", "number", NumberOperators, ["video", "image"], true, true, Unit: "px"),
        new("file.duration", "Durée du média", "number", NumberOperators, ["video", "music"], true, true, Unit: "s"),
        new("file.size", "Taille du fichier", "number", NumberOperators, AllMedia, true, true, Unit: "octets"),
        new("file.extension", "Extension du fichier courant", "text", TextOperators, AllMedia),
        new("file.path", "Chemin relatif du fichier d'origine", "text", TextOperators, AllMedia),
        new("audio.present", "Présence de pistes audio", "boolean", BoolOperators, ["video", "music"], true, true),
        new("audio.count", "Nombre de pistes audio", "number", NumberOperators, ["video", "music"], true, true),
        new("audio.languages", "Langues des pistes audio", "text-list", ListOperators, ["video", "music"], true, true),
        new("audio.codecs", "Codecs des pistes audio", "text-list", ListOperators, ["video", "music"], true, true),
        new("audio.channels", "Canaux de la première piste audio", "number", NumberOperators, ["video", "music"], true, true),
        new("subtitles.present", "Présence de sous-titres", "boolean", BoolOperators, ["video", "subtitles"], true, true),
        new("subtitles.count", "Nombre de pistes de sous-titres", "number", NumberOperators, ["video", "subtitles"], true, true),
        new("subtitles.languages", "Langues des sous-titres", "text-list", ListOperators, ["video", "subtitles"], true, true),
        new("library.id", "Identifiant de la bibliothèque d'origine", "text", TextOperators, AllMedia),
        new("source.nodeId", "Identifiant du nœud d'accès d'origine", "text", TextOperators, AllMedia),
        new("worker.nodeId", "Identifiant du worker courant", "text", TextOperators, AllMedia),
        new("worker.codecs", "Codecs vidéo supportés par le worker", "text-list", ListOperators, ["video", "image"]),
        new("processing.modified", "Le fichier a été transformé", "boolean", BoolOperators, AllMedia),
        new("processing.planPending", "Un plan de traitement est en attente", "boolean", BoolOperators, AllMedia),
        new("variable.text", "Variable texte", "text", TextOperators, AllMedia, RequiresVariableName: true),
        new("variable.number", "Variable numérique", "number", NumberOperators, AllMedia, RequiresVariableName: true),
        new("variable.boolean", "Variable Oui / Non", "boolean", BoolOperators, AllMedia, RequiresVariableName: true)
    ];

    public static IReadOnlyList<ConditionPreset> Presets { get; } = [
        Preset("video-codec", "Codec vidéo H.265", new("video.codec", "eq", Element("hevc")), ["video"]),
        Preset("resolution", "Résolution supérieure à 1080p", new("video.height", "gt", Element(1080)), ["video", "image"]),
        Preset("audio-language", "Piste audio en français", new("audio.languages", "contains", Element("fra")), ["video", "music"]),
        Preset("subtitles", "Présence de sous-titres", new("subtitles.present", "eq", Element(true)), ["video", "subtitles"]),
        Preset("storage", "Stockage d'origine", new("source.nodeId", "eq", Element("")), AllMedia),
        Preset("worker-av1", "Worker compatible AV1", new("worker.codecs", "contains", Element("av1")), ["video", "image"]),
        Preset("variable", "Valeur d'une variable", new("variable.text", "eq", Element(""), "status"), AllMedia)
    ];
    private static ConditionPreset Preset(string id, string label, ConditionRule rule, string[] media) => new(id, label, new ConditionGroup("all", [rule]), media);
    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value);
    public static JsonElement Serialize(ConditionExpression expression) => JsonSerializer.SerializeToElement(expression, Json);

    public static string? Validate(JsonElement value, string mediaType, bool allowIncomplete = false)
    {
        try
        {
            var tree = value.Deserialize<ConditionExpression>(Json);
            if (tree is not ConditionGroup) return "Les conditions doivent commencer par un groupe ET ou OU.";
            var count = 0;
            return ValidateNode(tree, mediaType, 1, ref count, allowIncomplete);
        }
        catch (JsonException) { return "L'arbre de conditions ne respecte pas le contrat."; }
        catch (NotSupportedException) { return "Le type de condition n'est pas pris en charge."; }
    }
    public static ConditionGroup Parse(JsonElement value, string mediaType)
    {
        var error = Validate(value, mediaType);
        if (error is not null) throw new ConditionEvaluationException(error);
        return (ConditionGroup)value.Deserialize<ConditionExpression>(Json)!;
    }
    private static string? ValidateNode(ConditionExpression? node, string mediaType, int depth, ref int count, bool incomplete)
    {
        if (++count > MaxNodes || depth > MaxDepth) return $"Maximum : {MaxNodes} éléments et {MaxDepth} niveaux de conditions.";
        if (node is ConditionGroup group)
        {
            if (group.Match is not ("all" or "any") || group.Children is null) return "Choisissez ET ou OU pour chaque groupe.";
            if (group.Children.Count == 0 && !incomplete) return "Ajoutez au moins une condition dans chaque groupe.";
            foreach (var child in group.Children)
            {
                var error = ValidateNode(child, mediaType, depth + 1, ref count, incomplete);
                if (error is not null) return error;
            }
            return null;
        }
        if (node is not ConditionRule rule) return "Condition inconnue.";
        var field = Fields.SingleOrDefault(field => field.Id == rule.Field);
        if (field is null || !field.SupportedFlowMediaTypes.Contains(mediaType)) return "Ce champ de condition n'est pas disponible pour ce type de flow.";
        if (!field.Operators.Any(op => op.Value == rule.Operator)) return "Cet opérateur n'est pas disponible pour ce champ.";
        if (rule.Scope is not ("current" or "source") || !field.SupportsScope && rule.Scope != "current") return "L'instantané demandé n'est pas disponible pour ce champ.";
        if (field.RequiresVariableName && (rule.VariableName?.Length > 128 || string.IsNullOrWhiteSpace(rule.VariableName) && !incomplete))
            return "Indiquez le nom de la variable (128 caractères maximum).";
        if (!field.RequiresVariableName && rule.VariableName is not null) return "Ce champ n'utilise pas de nom de variable.";
        if (rule.Operator is "exists" or "missing") return null;
        if (rule.Value is not { } scalar || scalar.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return incomplete ? null : "Indiquez la valeur à comparer.";
        return field.Type switch
        {
            "text" or "text-list" => scalar.ValueKind == JsonValueKind.String && scalar.GetString()!.Length <= 4096 ? null : "Une valeur textuelle est attendue.",
            "number" => scalar.ValueKind == JsonValueKind.Number && scalar.TryGetDecimal(out _) ? null : "Un nombre valide est attendu.",
            "boolean" => scalar.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : "Une valeur Oui ou Non est attendue.",
            _ => "Type de comparaison inconnu."
        };
    }
    public static bool RequiresMetadata(ConditionExpression tree) => tree switch
    {
        ConditionGroup group => group.Children.Any(RequiresMetadata),
        ConditionRule rule => Fields.Single(field => field.Id == rule.Field).RequiresMetadata,
        _ => false
    };

    public static bool Evaluate(ConditionExpression tree, ConditionEvaluationContext context, Action<ConditionObservation>? observe = null)
    {
        if (tree is ConditionGroup group)
            return group.Match == "all" ? group.Children.All(child => Evaluate(child, context, observe)) : group.Children.Any(child => Evaluate(child, context, observe));
        var rule = (ConditionRule)tree;
        var actual = Read(rule, context);
        try
        {
            var result = EvaluateRule(rule, actual);
            observe?.Invoke(new(rule, actual, result));
            return result;
        }
        catch (ConditionEvaluationException e) { observe?.Invoke(new(rule, actual, null, e.Message)); throw; }
    }
    private static bool EvaluateRule(ConditionRule rule, JsonElement? actual)
    {
        var present = actual is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) };
        if (rule.Operator == "exists") return present;
        if (rule.Operator == "missing") return !present;
        if (!present) throw new ConditionEvaluationException($"La donnée « {Fields.Single(field => field.Id == rule.Field).Label} » est absente.");
        var field = Fields.Single(field => field.Id == rule.Field);
        var expected = rule.Value!.Value;
        bool equal;
        switch (field.Type)
        {
            case "number":
                if (actual!.Value.ValueKind != JsonValueKind.Number || !actual.Value.TryGetDecimal(out var number)) throw TypeError(field);
                var target = expected.GetDecimal();
                return rule.Operator switch { "eq" => number == target, "neq" => number != target, "gt" => number > target, "gte" => number >= target,
                    "lt" => number < target, "lte" => number <= target, _ => throw TypeError(field) };
            case "boolean":
                if (actual!.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw TypeError(field);
                equal = actual.Value.GetBoolean() == expected.GetBoolean(); break;
            case "text-list":
                if (actual!.Value.ValueKind != JsonValueKind.Array) throw TypeError(field);
                equal = actual.Value.EnumerateArray().Any(item => string.Equals(item.GetString(), expected.GetString(), StringComparison.OrdinalIgnoreCase));
                return rule.Operator == "contains" ? equal : !equal;
            default:
                if (actual!.Value.ValueKind != JsonValueKind.String) throw TypeError(field);
                var text = actual.Value.GetString()!; var sought = expected.GetString()!;
                if (rule.Operator is "contains" or "notContains")
                {
                    var contains = text.Contains(sought, StringComparison.OrdinalIgnoreCase);
                    return rule.Operator == "contains" ? contains : !contains;
                }
                equal = string.Equals(text, sought, StringComparison.OrdinalIgnoreCase); break;
        }
        return rule.Operator == "eq" ? equal : !equal;
    }
    private static ConditionEvaluationException TypeError(ConditionFieldDefinition field) => new($"Le type de la donnée « {field.Label} » ne permet pas cette comparaison.");

    private static JsonElement? Read(ConditionRule rule, ConditionEvaluationContext data)
    {
        if (rule.Field.StartsWith("variable.", StringComparison.Ordinal))
            return data.Runtime.Variables.TryGetValue(rule.VariableName!, out var value) ? value : null;
        switch (rule.Field)
        {
            case "file.extension": return Element(data.Runtime.Extension.TrimStart('.'));
            case "file.path": return Element(data.MediaPath);
            case "source.nodeId": return Element(data.SourceNodeId.ToString("D"));
            case "worker.nodeId": return Element(data.WorkerNodeId.ToString("D"));
            case "worker.codecs": return Element(data.WorkerCodecs ?? []);
            case "library.id": return data.LibraryId is { } library ? Element(library.ToString("D")) : null;
            case "processing.modified": return Element(data.Runtime.CurrentSha256 != data.SourceSha256);
            case "processing.planPending": return Element(data.Runtime.Plan.Count != 0);
        }
        var metadata = rule.Scope == "source" ? data.Runtime.BeforeMetadata : data.Runtime.CurrentMetadata;
        if (metadata is not { ValueKind: JsonValueKind.Object } json) return null;
        if (rule.Field is "file.duration" or "file.size")
        {
            if (!json.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.Object
                || !format.TryGetProperty(rule.Field == "file.duration" ? "duration" : "size", out var value)) return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out _)) return value;
            return value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? Element(number) : null;
        }
        if (!json.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array) return null;
        var type = rule.Field.StartsWith("video.", StringComparison.Ordinal) ? "video" : rule.Field.StartsWith("audio.", StringComparison.Ordinal) ? "audio" : "subtitle";
        var tracks = streams.EnumerateArray().Where(track => track.ValueKind == JsonValueKind.Object && track.TryGetProperty("codec_type", out var codecType) && codecType.GetString() == type
            && !(type == "video" && track.TryGetProperty("disposition", out var disposition) && disposition.ValueKind == JsonValueKind.Object
                && disposition.TryGetProperty("attached_pic", out var cover) && cover.ValueKind == JsonValueKind.Number && cover.GetInt32() != 0)).ToArray();
        var property = rule.Field.Split('.')[1];
        if (rule.Field == "video.bitDepth") return tracks.Length == 0 ? null : Element(MediaConditionEvaluator.DetectBitDepth(tracks[0]));
        if (rule.Field == "video.hdr") return tracks.Length == 0 ? null : Element(MediaConditionEvaluator.DetectHdrFormat(tracks[0]) != MediaConditionEvaluator.HdrFormat.Sdr);
        if (property == "present") return Element(tracks.Length > 0);
        if (property == "count") return Element(tracks.Length);
        if (property is "languages" or "codecs")
        {
            var values = new List<string>();
            foreach (var track in tracks)
            {
                JsonElement value = default;
                var found = property == "codecs" ? track.TryGetProperty("codec_name", out value)
                    : track.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty("language", out value);
                if (!found || value.ValueKind != JsonValueKind.String) return null;
                values.Add(value.GetString()!);
            }
            return Element(values);
        }
        if (tracks.Length == 0) return null;
        return tracks[0].TryGetProperty(property == "codec" ? "codec_name" : property, out var selected) ? selected : null;
    }
}
