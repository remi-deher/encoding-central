using Xunit;

namespace Encoding.Runtime.Tests;

[Collection(RuntimeCollection.Name)]
public sealed class RuntimeScenarioTests(RuntimeFixture database)
{
    private Harness.Fixture Scenarios => database.Scenarios ?? throw new InvalidOperationException("The SQLite fixture is not initialized.");

    [MediaToolsFact]
    public Task ExplicitQuickIntegrityIsReusedAndDoesNotHideAFullDecodeBeforePublication() => Scenarios.ExplicitQuickIntegrityPublicationAsync();

    [DatabaseFact]
    public Task DirectInspectionDefersTransfersAndFencesPreparation() => Scenarios.DirectInspectionDefersTransfersAndFencesPreparationAsync();

    [DatabaseFact]
    public Task DirectInspectionCannotCheckpointAMutatingBlock() => Scenarios.DirectInspectionCannotCheckpointAMutatingBlockAsync();

    [MediaToolsFact]
    public Task DirectInspectionFinishesWithoutCopyOrHash() => Scenarios.DirectInspectionFinishesWithoutCopyOrHashAsync();

    [DatabaseFact]
    public Task ChangedDirectSourceFailsBeforeAnyPreparation() => Scenarios.ChangedDirectSourceFailsBeforeAnyPreparationAsync();

    [DatabaseFact]
    public Task DirectInspectionPreparesBeforeHandoff() => Scenarios.DirectInspectionPreparesBeforeHandoffAsync();

    [DatabaseFact]
    public Task FlowDetailsPreviewAndRestoreKeepImmutableHistoryAndLibraryAssignments() => Scenarios.FlowDetailsHistoryAsync();

    [DatabaseFact]
    public Task FlowSavingIsIdempotentAndConcurrencySafe() => Scenarios.FlowSavingAsync();

    [DatabaseFact]
    public Task NodeGroupsResolveAutomaticMembershipAndLabelInheritance() => Scenarios.NodeGroupsAndLabelsAsync();

    [DatabaseFact]
    public Task DurableRelayBeforeTransformation() => Scenarios.BackendRelayAsync(false);

    [DatabaseFact]
    public Task DurableRelayAfterTransformation() => Scenarios.BackendRelayAsync(true);

    [DatabaseFact]
    public Task ConfigurationSnapshotsStayFixedAndForgedTargetsAreRejected() => Scenarios.FrozenConfigurationAsync();

    [DatabaseFact]
    public Task MissingOrCorruptArtifactsAndUnavailableTargetsAreRefused() => Scenarios.IntegrityAndAvailabilityAsync();

    [DatabaseFact]
    public Task ConcurrentReceiverClaimsKeepOneOwner() => Scenarios.ConcurrentRelayAsync();

    [DatabaseFact]
    public Task EarlyWorkerRelayAvoidsDownloadsAndSurvivesReceiverLoss() => Scenarios.EarlyRunnerAsync();

    [DatabaseFact]
    public Task RejectedCheckpointWithLiveLeaseFailsWithoutRetry() => Scenarios.ConflictDuringCheckpointFailsExecutionAsync();

    [MediaToolsFact]
    public Task RejectedFinalCompletionWithLiveLeaseFailsWithoutRetry() => Scenarios.ConflictDuringFinishFailsExecutionAsync();

    [DatabaseFact]
    public Task CancellationReleasesTheDurableReservation() => Scenarios.CancellationAsync();

    [DatabaseFact]
    public Task ConditionalRelayYesBranch() => Scenarios.ConditionalRelayAsync("yes");

    [DatabaseFact]
    public Task ConditionalRelayNoBranch() => Scenarios.ConditionalRelayAsync("no");

    [DatabaseFact]
    public Task ConditionalRelayErrorBranch() => Scenarios.ConditionalRelayAsync("error");

    [MkvToolsFact]
    public Task MissingProducerOnSelectedBranchFailsWithoutPublishing() => Scenarios.ArtifactRelayAsync(true);

    [MkvToolsFact]
    public Task ProducedAudioFileIsConsumedAfterDurableRelay() => Scenarios.ArtifactRelayAsync();

    [MediaToolsFact]
    public Task H265ConversionHandedOffAfterExecution() => Scenarios.MediaRelayAsync(false);

    [MediaToolsFact]
    public Task H265PlanHandedOffBeforeExecution() => Scenarios.MediaRelayAsync(true);

    [DatabaseFact]
    public Task OperationsMigrationsHealthAndArtifactRetention() => OperationsScenarios.RunDatabaseAsync(RuntimeFixture.ConnectionString!);

    [DatabaseFact]
    public Task SourceReplacementLifecycle() => Scenarios.ReplacementLifecycleAsync();

    [DatabaseFact]
    public Task PublishStepKeepsTheExecutionActive() => Scenarios.PublishStepKeepsTheExecutionActiveAsync();

    [DatabaseFact]
    public Task PublicationRequestedBeforeWorkerLossIsAdoptedNotRepeated() => Scenarios.PublicationRequestedBeforeWorkerLossIsAdoptedNotRepeatedAsync();

    [DatabaseFact]
    public Task ReplicatedExecutionCannotBeClaimedOnAnotherServer() => Scenarios.ReplicatedExecutionCannotBeClaimedOnAnotherServerAsync();

    [DatabaseFact]
    public Task ResumeOnAnotherDirectWorkerWhenTheDeadWorkerWasTheAccessNode() => Scenarios.ResumeOnAnotherDirectWorkerWhenTheDeadWorkerWasTheAccessNodeAsync();

    [DatabaseFact]
    public Task WaitMediaPolicy() => Scenarios.WaitMediaPolicyAsync();

    [DatabaseFact]
    public Task WorkerLossFailover() => Scenarios.WorkerLossFailoverAsync();

    [DatabaseFact]
    public Task CancelledExecutionCanBeResumedAsFreshAttempt() => Scenarios.CancelledExecutionCanBeResumedAsFreshAttemptAsync();
    [DatabaseFact]
    public Task QuarantinedCancelledExecutionCannotBeResumed() => Scenarios.QuarantinedCancelledExecutionCannotBeResumedAsync();

    [DatabaseFact]
    public Task UncertainPluginSideEffectIsNotRepeatedAfterWorkerLoss() => Scenarios.UncertainPluginStepIsNotRepeatedAfterWorkerLossAsync();

    [DatabaseFact]
    public Task RelayWaitsForABusyWorker() => Scenarios.RelayWaitsForABusyWorkerAsync();

    [DatabaseFact]
    public Task ResumeWaitsForAnEligibleWorker() => Scenarios.ResumeWaitsForAnEligibleWorkerAsync();

    [DatabaseFact]
    public Task ReplayableStepIsResumedAfterWorkerLoss() => Scenarios.ReplayableStepIsResumedAfterWorkerLossAsync();

    [DatabaseFact]
    public Task LibraryAccessBlockBranchesOnDirectAccess() => Scenarios.LibraryAccessBlockBranchesOnDirectAccessAsync();

    [DatabaseFact]
    public Task ResumeKeepsTheRelayDestination() => Scenarios.ResumeKeepsTheRelayDestinationAsync();

    [DatabaseFact]
    public Task RetryPolicyKeepsPriorityOverResume() => Scenarios.RetryPolicyKeepsPriorityOverResumeAsync();

    [DatabaseFact]
    public Task FlowRetryPolicyIsBounded() => Scenarios.BoundedFlowRetryAsync();

    [MediaToolsFact]
    public Task H265SourceReplacedThenRestored() => Scenarios.MediaRelayAsync(false, true);

    [DatabaseFact]
    public Task AuthenticationRolesPairingAndAnonymousRouteCatalogue() => AuthScenarios.RunAsync(RuntimeFixture.ConnectionString!);

    [DatabaseFact]
    public Task NodeScheduleBlocksNewClaimsOnly() => Scenarios.NodeScheduleAsync();

    [DatabaseFact]
    public Task PublicationGuardsComeFromTheConfiguredPlugin() => Scenarios.PublicationGuardsAsync();

    [DatabaseFact]
    public Task DiskLockIsReleasedWhenWorkerAttemptFails() => Scenarios.DiskLockReleasedAfterWorkerLossAsync();

    [DatabaseFact]
    public Task DiskLocksSerializeHeavyWorkPerDisk() => Scenarios.DiskLocksAsync();

    [DatabaseFact]
    public Task WaitTimerReleasesTheWorkerAndResumesAfterTheBlock() => Scenarios.WaitTimerAsync();

    [DatabaseFact]
    public Task WaitDeadlineWithTimeoutOutput() => Scenarios.WaitDeadlineAsync(true);

    [DatabaseFact]
    public Task WaitDeadlineWithoutTimeoutOutputFails() => Scenarios.WaitDeadlineAsync(false);

    [DatabaseFact]
    public Task WaitServerEvaluatedEvents() => Scenarios.WaitServerEventsAsync();

    [DatabaseFact]
    public Task WaitOnPluginConditionUsesFencedProbes() => Scenarios.WaitBlockProbeAsync();

    [DatabaseFact]
    public Task WaitRunnerYieldsAndCancellationClosesTheWait() => Scenarios.WaitRunnerAndCancellationAsync();

    [DatabaseFact]
    public void WaitBlockContractsAndCompatibility() => Scenarios.WaitContracts();

    [DatabaseFact]
    public Task ApiImprovementsOpenApiProblemDetailsPaginationSseFiltersAndRateLimiting() =>
        ApiImprovementsScenarios.RunAsync(RuntimeFixture.ConnectionString!);
}
