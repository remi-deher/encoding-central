namespace Encoding.Contracts.Flows;

/// <summary>Opt-in integrity policy: only flows containing this block perform quick or full media decoding checks.</summary>
public static class MediaIntegrityPolicy
{
    public const string Component = "probe-check-integrity";
    public const string Capability = "media-integrity-policy/v1";
    public const string ReceiptVariable = "runtime.integrity";
    public static string? DescribeFailure(string? reason) => reason switch {
        "invalid_configuration" => "Configuration du contrôle invalide.",
        "artifact_changed" => "Le fichier a changé ; contrôle et publication refusés.",
        "ffmpeg_unavailable" => "FFmpeg est indisponible sur ce nœud.",
        "gpu_decoder_unavailable" => "Aucun décodeur GPU utilisable pour ce média ; le secours CPU est interdit.",
        "gpu_video_required" => "Le GPU demandé nécessite une piste vidéo compatible.",
        "duration_unavailable" => "La durée est indisponible ; les passages représentatifs ne peuvent pas être choisis.",
        "decode_errors" => "Des erreurs de décodage ont été détectées.",
        "permission_denied" => "L’accès au fichier a été refusé.",
        "inspection_timeout" => "Le délai du contrôle média a été dépassé.",
        "inspection_unavailable" => "L’analyse ou l’outil nécessaire au contrôle est indisponible.",
        _ => null
    };
    public static bool IsExplicit(FlowDefinition flow) => flow.Nodes.Any(node =>
        node.Component.PluginId == "probe" && node.Component.ComponentId == Component);
}
