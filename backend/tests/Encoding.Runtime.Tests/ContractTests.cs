using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class ContractTests
{
    [Fact]
    public void RelayContractsSegmentEligibilityInheritanceAndOldAgentCompatibility() => Harness.RelayContracts();

    [Fact]
    public async Task DirectAccessConstraintEnforcesDirectModeAndAllowsRelayToTransferWorker()
    {
        var (registry, validator, eligibility) = Harness.Services();
        await using var harness = await Harness.Fixture.CreateAsync("Data Source=:memory:", registry, validator, eligibility);
        await harness.DirectAccessRejectsTransferWorkerAsync();
        await harness.DefaultPlacementPrefersCapableDirectWorkerAsync();
        await harness.FreshExecutionWithoutAnyCapableNodeFailsEarlyAsync();
        await harness.DirectAccessConstraintAsync();
    }

    [Fact]
    public void FlowValidationRejectsInvalidGraphsAndIncompatibleComponents() => FlowValidationScenarios.Run();

    [Fact]
    public void RequireCapabilityEncoderKindSeparatesHardwareFromCpu() => Harness.EncoderKindContracts();

    [Fact]
    public void LibraryAccessCanRequireTheProfileEncoder() => Harness.LibraryAccessWithEncoderContracts();

    [Fact]
    public void RequireCapabilityFollowsTheVideoEncodingProfile() => Harness.ProfileEncoderContracts();

    [Fact]
    public void NativeConditionsEvaluateNestedGroupsAndMetadataSnapshots()
    {
        var (registry, validator, eligibility) = Harness.Services();
        ConditionScenarios.Run(registry, validator, eligibility);
    }

    [Fact]
    public void ScriptsThatNeedToolsOnlyRunOnWorkersWithTheHelper()
    {
        var (registry, validator, eligibility) = Harness.Services();
        ScriptToolsScenarios.Run(registry, validator, eligibility);
    }

    [Fact]
    public void AudioRulesDecideConversionsAndRefuseUnsafeLayouts()
    {
        var (registry, validator, eligibility) = Harness.Services();
        AudioRuleScenarios.Run(registry, validator, eligibility);
    }

    [Fact]
    public void TrackNamesFollowTheTemplateAndKeepOriginalVariants()
    {
        var (registry, validator, eligibility) = Harness.Services();
        TrackNameScenarios.Run(registry, validator, eligibility);
    }

    [Fact]
    public async Task PublicationGuardsWaitAllowAndRefuseSafely()
    {
        var (registry, _, _) = Harness.Services();
        await PublicationGuardScenarios.RunAsync(registry);
    }

    [Fact]
    public void SubtitleProfileConversionDuplicatesForcedTracksAndFonts()
    {
        var (registry, validator, eligibility) = Harness.Services();
        SubtitleScenarios.Run(registry, validator, eligibility);
    }

    [Fact]
    public async Task FlowSimulationCoversBranchesVariablesAndMediaInfoImport()
    {
        var (registry, validator, _) = Harness.Services();
        await FlowSimulationScenarios.RunAsync(registry, validator);
    }

    [Fact]
    public void OperationsMigrationCatalogMetricsAndRetentionOptionsAreValid() => OperationsScenarios.Run();

    [Fact]
    public Task SourceReplacementFilesPolicyGraceAndRestore() => SourceReplacementScenarios.RunAsync();

    [Fact]
    public void PasswordHashingRehashDetectionAndCredentialPolicy() => AuthScenarios.RunUnit();

    [Fact]
    public void NodeScheduleValidationAndEvaluation() => NodeScheduleScenarios.Run();

    [Fact]
    public void NodeLabelSelectorsRequireEveryKeyAndValue()
    {
        IReadOnlyDictionary<string, string?> labels = new Dictionary<string, string?> { ["gpu"] = "nvidia", ["region"] = "eu" };
        Assert.True(Encoding.Contracts.Nodes.NodeLabelSelector.Matches("gpu,region=eu", labels));
        Assert.False(Encoding.Contracts.Nodes.NodeLabelSelector.Matches("gpu,region=us", labels));
        Assert.False(Encoding.Contracts.Nodes.NodeLabelSelector.Matches("missing", labels));
    }

    [Fact]
    public void AutomaticNodeGroupsMatchAllDeclaredCharacteristics()
    {
        var node = new Encoding.Contracts.Nodes.NodeResponse(Guid.NewGuid(), "worker", [Encoding.Contracts.Nodes.NodeRole.Worker], 4, [],
            Encoding.Contracts.Nodes.NodePresenceStatus.Online, DateTimeOffset.UtcNow,
            new Encoding.Contracts.Nodes.NodeInventory("Linux", "X64", 8, 32768));
        var criteria = new Encoding.Contracts.Nodes.NodeGroupCriteria([Encoding.Contracts.Nodes.NodeRole.Worker], "Linux", "X64", 16384, ["ffmpeg"], 2);
        Assert.False(Encoding.Api.Nodes.NodeGroupRepository.Matches(criteria, node));
        var equipped = node with { Capabilities = [new Encoding.Contracts.Nodes.NodeToolCapability("ffmpeg", "7.0", ["video"], [])] };
        Assert.True(Encoding.Api.Nodes.NodeGroupRepository.Matches(criteria, equipped));
    }

    [Fact]
    public void RelaySelectorCombinesGroupAndInheritedLabels()
    {
        var group = Guid.NewGuid(); var previous = Guid.NewGuid(); var candidate = Guid.NewGuid();
        var selector = new Encoding.Contracts.Executions.RuntimeHandoffSelector("selector", TargetGroupId: group, RequiredLabels: "gpu=nvidia,region");
        var identity = new Encoding.Contracts.Nodes.NodeIdentityResponse(candidate, null, new Dictionary<string,string?>(),
            new Dictionary<string,string?> { ["gpu"] = "nvidia", ["region"] = null }, [group]);
        Assert.True(selector.Accepts(candidate, previous, identity));
        Assert.False(selector.Accepts(candidate, previous, identity with { GroupIds = [] }));
        Assert.False(selector.Accepts(candidate, previous, identity with { EffectiveLabels = new Dictionary<string,string?> { ["gpu"] = "amd", ["region"] = null } }));
    }
}
