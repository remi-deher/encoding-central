namespace Encoding.Api.Executions;

/// <summary>Motif d'un refus de <c>steps/start</c> ou <c>steps/complete</c> : <see cref="Code"/> est court, stable et sans donnée sensible.</summary>
public sealed record StepRefusal(string Code);

/// <summary>Codes de refus stables. Ne jamais renommer un code existant : il apparaît dans les journaux, le corps du 409 et les échecs <c>cluster_refused</c>.</summary>
public static class StepRefusalCodes
{
    // Forme du checkpoint (validée avant tout accès base).
    public const string CheckpointMissing = "checkpoint_missing";
    public const string ContextIncomplete = "context_incomplete";
    public const string VideoEncodingsLimit = "video_encodings_limit";
    public const string SourceDigestInvalid = "source_digest_invalid";
    public const string ExtensionInvalid = "extension_invalid";
    public const string CommandsLimit = "commands_limit";
    public const string CommandLogInvalid = "command_log_invalid";
    public const string CommandIdDuplicate = "command_id_duplicate";
    public const string CommandsTooLarge = "commands_too_large";
    public const string CheckpointTooLarge = "checkpoint_too_large";

    // Bail et état de l'exécution.
    public const string ExecutionUnknown = "execution_unknown";
    public const string ExecutionNotActive = "execution_not_active";
    public const string LeaseWrongWorker = "lease_wrong_worker";
    public const string LeaseEpochMismatch = "lease_epoch_mismatch";
    public const string LeaseExpired = "lease_expired";
    public const string ReservationLost = "reservation_lost";
    public const string ExecutionNotRunning = "execution_not_running";
    public const string DuplicateCheckpointDiffers = "duplicate_checkpoint_differs";

    // Enchaînement du flow.
    public const string NodeUnknown = "node_unknown";
    public const string StepOutOfOrder = "step_out_of_order";
    public const string StepNotStarted = "step_not_started";
    public const string StepLogsInvalid = "step_logs_invalid";
    public const string StepAlreadyCompleted = "step_already_completed";
    public const string OutputInvalid = "output_invalid";
    public const string NextNodeMismatch = "next_node_mismatch";

    // Source et artefacts.
    public const string VideoEncodingInvalid = "video_encoding_invalid";
    public const string DirectSourceNotAllowed = "direct_source_not_allowed";
    public const string DirectSourceStateInvalid = "direct_source_state_invalid";
    public const string ArtifactMissing = "artifact_missing";
    public const string ExtraArtifactInvalid = "extra_artifact_invalid";
    public const string ExtraArtifactMissing = "extra_artifact_missing";

    // Relais et attentes.
    public const string HandoffPolicyInvalid = "handoff_policy_invalid";
    public const string HandoffMismatch = "handoff_mismatch";
    public const string PlacementHandoffInvalid = "placement_handoff_invalid";
    public const string PlacementHandoffUnexpected = "placement_handoff_unexpected";
    public const string WaitPolicyInvalid = "wait_policy_invalid";
    public const string WaitMismatch = "wait_mismatch";
    public const string WaitUnexpected = "wait_unexpected";

    // Journal.
    public const string JournalTooLarge = "journal_too_large";
    public const string RevisionConflict = "revision_conflict";
}
