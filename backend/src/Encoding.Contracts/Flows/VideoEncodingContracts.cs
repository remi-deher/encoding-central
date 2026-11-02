namespace Encoding.Contracts.Flows;

public sealed record VideoEncoderDescriptor(
    string Id,
    string Codec,
    string Name,
    string Acceleration,
    string QualityMode,
    string QualityOption,
    string PresetOption,
    decimal QualityMinimum,
    decimal QualityMaximum,
    decimal DefaultQuality,
    IReadOnlyList<VideoEncoderPreset> Presets);

public sealed record VideoEncoderPreset(string Id, string Name, string Value);

public sealed record EncodingProfileTarget(
    string EncoderId,
    decimal Quality,
    string Preset,
    int Priority);

/// <summary>A portable immutable profile snapshot embedded in each flow component.</summary>
public sealed record EncodingProfileSnapshot(
    Guid Id,
    int Version,
    string Name,
    string Description,
    IReadOnlyList<EncodingProfileTarget> Targets);

public sealed record EncodingProfileDraft(
    string Name,
    string Description,
    IReadOnlyList<EncodingProfileTarget> Targets);

public sealed record EncodingProfileVersionSummary(
    Guid ProfileId,
    int Version,
    string Name,
    string Description,
    DateTimeOffset CreatedAt);

public sealed record EncodingProfileSummary(
    Guid Id,
    int DraftRevision,
    string DraftName,
    string DraftDescription,
    int PublishedVersion,
    int EncoderCount,
    DateTimeOffset UpdatedAt,
    bool IsBuiltIn,
    IReadOnlyList<EncodingProfileVersionSummary> Versions);

public sealed record EncodingProfileDetails(
    EncodingProfileSummary Profile,
    EncodingProfileDraft Draft);

public sealed record EncodingProfileVersion(
    Guid ProfileId,
    int Version,
    string Name,
    string Description,
    IReadOnlyList<EncodingProfileTarget> Targets,
    DateTimeOffset CreatedAt);

public sealed record CreateEncodingProfileRequest(EncodingProfileDraft Draft);
public sealed record SaveEncodingProfileDraftRequest(int ExpectedRevision, EncodingProfileDraft Draft);
public sealed record PublishEncodingProfileRequest(int ExpectedRevision);

public static class VideoEncoderCatalog
{
    private static readonly VideoEncoderPreset[] CommonPresets =
    [
        new("slow", "Lent / meilleure compression", "slow"),
        new("medium", "Équilibré", "medium"),
        new("fast", "Rapide", "fast")
    ];

    private static readonly VideoEncoderPreset[] NvencPresets =
    [
        new("slow", "Lent / meilleure compression", "p7"),
        new("medium", "Équilibré", "p4"),
        new("fast", "Rapide", "p1")
    ];

    private static readonly VideoEncoderPreset[] SvtPresets =
    [
        new("slow", "Lent / meilleure compression", "4"),
        new("medium", "Équilibré", "8"),
        new("fast", "Rapide", "10")
    ];

    private static readonly VideoEncoderPreset[] AomPresets =
    [
        new("slow", "Lent / meilleure compression", "4"),
        new("medium", "Équilibré", "6"),
        new("fast", "Rapide", "8")
    ];

    public static IReadOnlyList<VideoEncoderDescriptor> All { get; } =
    [
        new("libx264", "h264", "x264 · CPU", "cpu", "crf", "-crf", "-preset", 0, 51, 23, CommonPresets),
        new("h264_nvenc", "h264", "NVENC · H.264", "nvenc", "cq", "-cq", "-preset", 0, 51, 23, NvencPresets),
        new("h264_qsv", "h264", "Quick Sync · H.264", "qsv", "global_quality", "-global_quality", "-preset", 1, 51, 25, CommonPresets),
        new("h264_vaapi", "h264", "VAAPI · H.264", "vaapi", "qp", "-qp", "", 0, 51, 23, []),
        new("libx265", "hevc", "x265 · CPU", "cpu", "crf", "-crf", "-preset", 0, 51, 23, CommonPresets),
        new("hevc_nvenc", "hevc", "NVENC · HEVC", "nvenc", "cq", "-cq", "-preset", 0, 51, 25, NvencPresets),
        new("hevc_qsv", "hevc", "Quick Sync · HEVC", "qsv", "global_quality", "-global_quality", "-preset", 1, 51, 25, CommonPresets),
        new("hevc_vaapi", "hevc", "VAAPI · HEVC", "vaapi", "qp", "-qp", "", 0, 51, 23, []),
        new("libsvtav1", "av1", "SVT-AV1 · CPU", "cpu", "crf", "-crf", "-preset", 0, 63, 30, SvtPresets),
        new("libaom-av1", "av1", "libaom · CPU", "cpu", "crf", "-crf", "-cpu-used", 0, 63, 30, AomPresets),
        new("av1_nvenc", "av1", "NVENC · AV1", "nvenc", "cq", "-cq", "-preset", 0, 51, 25, NvencPresets),
        new("av1_qsv", "av1", "Quick Sync · AV1", "qsv", "global_quality", "-global_quality", "-preset", 1, 51, 25, CommonPresets),
        new("av1_vaapi", "av1", "VAAPI · AV1", "vaapi", "qp", "-qp", "", 0, 255, 120, [])
    ];

    public static VideoEncoderDescriptor? Find(string? id) => All.FirstOrDefault(item => item.Id == id);

    public static string? ValidateTargets(IReadOnlyList<EncodingProfileTarget>? targets)
    {
        if (targets is null || targets.Count is < 1 or > 64) return "Le profil doit contenir entre 1 et 64 réglages d’encodeur.";
        if (targets.Select(target => target.EncoderId).Distinct(StringComparer.Ordinal).Count() != targets.Count)
            return "Un encodeur ne peut apparaître qu’une fois dans le profil.";
        if (targets.Select(target => target.Priority).Distinct().Count() != targets.Count)
            return "Chaque encodeur doit avoir une priorité différente.";
        foreach (var target in targets)
        {
            var descriptor = Find(target.EncoderId);
            if (descriptor is null) return $"Encodeur inconnu : {target.EncoderId}.";
            if (target.Quality < descriptor.QualityMinimum || target.Quality > descriptor.QualityMaximum)
                return $"La qualité de {descriptor.Name} doit être comprise entre {descriptor.QualityMinimum} et {descriptor.QualityMaximum}.";
            if (target.Priority is < 0 or > 999)
                return "La priorité doit être comprise entre 0 et 999.";
            if (descriptor.Presets.Count == 0
                ? !string.IsNullOrEmpty(target.Preset)
                : !descriptor.Presets.Any(preset => preset.Id == target.Preset))
                return $"Le preset choisi n’est pas pris en charge par {descriptor.Name}.";
        }
        return null;
    }

    public static string? ValidateSnapshot(EncodingProfileSnapshot? profile)
    {
        if (profile is null || profile.Id == Guid.Empty || profile.Version < 1
            || string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 128
            || profile.Description is null || profile.Description.Length > 1000)
            return "La référence du profil de qualité est invalide.";
        return ValidateTargets(profile.Targets);
    }
}

public static class BuiltInEncodingProfiles
{
    public static readonly Guid BalancedId = Guid.Parse("4e85ee3d-8976-4e8e-a625-5a82f0000027");

    public static EncodingProfileSnapshot Balanced { get; } = new(
        BalancedId,
        1,
        "Équilibré",
        "Valeurs de qualité par encodeur ; le worker utilise le meilleur candidat disponible selon les priorités.",
        VideoEncoderCatalog.All.Select((encoder, index) => new EncodingProfileTarget(
            encoder.Id,
            encoder.DefaultQuality,
            encoder.Presets.FirstOrDefault(preset => preset.Id == "medium")?.Id ?? string.Empty,
            (encoder.Acceleration switch { "nvenc" => 0, "qsv" => 100, "vaapi" => 200, _ => 300 }) + index)).ToArray());
}
