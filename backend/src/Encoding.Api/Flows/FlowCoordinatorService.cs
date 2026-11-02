using System.Text.Json;
using Encoding.Api.Executions;
using Encoding.Api.Infrastructure;
using Encoding.Api.Nodes;
using Encoding.Api.Plugins;
using Encoding.Api.Resources;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Nodes;

namespace Encoding.Api.Flows;

public record SaveDraftResult(bool IsValid, FlowValidationResult? Validation, FlowDraftResponse? Saved);

public record PublishFlowResult(
    bool IsValid,
    FlowValidationResult? Validation,
    IReadOnlyList<string>? MissingResources,
    FlowVersionResponse? Saved);

public record CreateFlowResult(
    bool ShouldPublish,
    bool IsValid,
    FlowValidationResult? Validation,
    FlowVersionResponse? Published,
    FlowDraftResponse? Draft,
    Guid FlowId);

public record FlowExportData(byte[] Bytes, string ContentType, string FileName);

public sealed class FlowCoordinatorService(
    FlowRepository flows,
    NodeRepository nodes,
    ResourceRepository resources,
    FlowTemplateRepository templates,
    FlowDefinitionValidator validator,
    RuntimeEligibility eligibility,
    IServiceProvider? services = null)
{
    public FlowCoordinatorService(IServiceProvider services) : this(
        services.GetService(typeof(FlowRepository)) as FlowRepository ?? null!,
        services.GetService(typeof(NodeRepository)) as NodeRepository ?? null!,
        services.GetService(typeof(ResourceRepository)) as ResourceRepository ?? null!,
        services.GetService(typeof(FlowTemplateRepository)) as FlowTemplateRepository ?? null!,
        services.GetService(typeof(FlowDefinitionValidator)) as FlowDefinitionValidator ?? null!,
        services.GetService(typeof(RuntimeEligibility)) as RuntimeEligibility ?? null!,
        services) { }

    private T? Get<T>() where T : class => services?.GetService(typeof(T)) as T;

    public async Task<object?> GetFlowForEditAsync(Guid id, CancellationToken ct)
    {
        if (await flows.DraftAsync(id, ct) is { } draft) return draft;
        return await flows.GetAsync(id, null, ct);
    }

    public async Task<object?> PreflightAsync(Guid id, TimeSpan offlineAfter, CancellationToken ct)
    {
        var draft = await flows.DraftAsync(id, ct);
        var flow = draft?.Definition ?? (await flows.GetAsync(id, null, ct))?.Definition;
        if (flow is null) return null;

        var validation = validator.Validate(flow);
        var availableWorkers = (await nodes.ListAsync(offlineAfter, ct))
            .Where(node => node.Status == NodePresenceStatus.Online
                && node.Lifecycle == NodeLifecycle.Online
                && node.Roles.Contains(NodeRole.Worker))
            .ToArray();

        var compatible = validation.IsValid
            ? availableWorkers.Where(node => eligibility.CanRun(flow, node.Capabilities, node.Id,
                identity: new NodeIdentityResponse(node.Id, node.DisplayName,
                    node.Labels ?? new Dictionary<string, string?>(),
                    node.Labels ?? new Dictionary<string, string?>(), []),
                inventory: node.Inventory)).ToArray()
            : [];

        return new
        {
            flowId = flow.Id,
            draftRevision = draft?.DraftRevision,
            publishedVersion = draft?.PublishedVersion ?? flow.Version,
            graph = new { valid = validation.IsValid, issues = validation.Issues },
            simulation = new
            {
                available = true,
                status = "not-run",
                requiredInput = "Un fichier de la bibliothèque cible est nécessaire."
            },
            execution = new
            {
                workerCompatible = compatible.Length > 0,
                status = !validation.IsValid
                    ? "invalid-graph"
                    : compatible.Length > 0 ? "worker-compatible" : "no-compatible-worker",
                checks = new
                {
                    workerToolsAndEncoders = "observed",
                    libraryAccess = "requires-target-library",
                    writePermissions = "not-evaluated",
                    aiProvider = "not-evaluated",
                },
                compatibleWorkers = compatible.Select(node => new
                {
                    node.Id,
                    node.Name,
                    node.Capabilities,
                    node.Inventory
                }),
                observedAt = DateTimeOffset.UtcNow,
            },
        };
    }

    public async Task<SaveDraftResult> SaveDraftAsync(SaveFlowDraftRequest request, CancellationToken ct)
    {
        var validation = validator.ValidateDraft(request.Definition);
        if (!validation.IsValid) return new(false, validation, null);
        var saved = await flows.SaveDraftAsync(request, ct);
        return new(true, null, saved);
    }

    public async Task<FlowExportData?> ExportSingleAsync(Guid id, CancellationToken ct)
    {
        if (await flows.DraftAsync(id, ct) is { } draft)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                kind = "draft",
                draftRevision = draft.DraftRevision,
                definition = draft.Definition
            }, FlowRepository.JsonOptions);
            return new FlowExportData(payload, "application/json", $"{id:D}.draft.json");
        }

        var saved = await flows.GetAsync(id, null, ct);
        if (saved is null) return null;

        var bytes = JsonSerializer.SerializeToUtf8Bytes(saved.Definition, FlowRepository.JsonOptions);
        return new FlowExportData(bytes, "application/json", $"{id:D}.v{saved.Definition.Version}.json");
    }

    public Task<IReadOnlyList<FlowLibraryUsage>> UsageAsync(Guid id, CancellationToken ct) =>
        flows.UsageAsync(id, ct);

    public Task<IReadOnlyList<FlowDraftBackupSummary>> DraftBackupsAsync(Guid id, CancellationToken ct) =>
        flows.DraftBackupsAsync(id, ct);

    public Task<FlowDefinition?> DraftBackupAsync(Guid id, int revision, CancellationToken ct) =>
        flows.DraftBackupAsync(id, revision, ct);

    public Task<IReadOnlyList<FlowDefinition>> VersionsAsync(Guid id, CancellationToken ct) =>
        flows.VersionsAsync(id, ct);

    public Task<IReadOnlyList<FlowSummary>> ListAsync(CancellationToken ct) =>
        flows.ListAsync(ct);

    public Task<FlowVersionResponse?> GetAsync(Guid id, int? version, CancellationToken ct) =>
        flows.GetAsync(id, version, ct);

    public async Task<PublishFlowResult> PublishAsync(SaveFlowRequest request, CancellationToken ct)
    {
        var (_, validation) = validator.ValidateDocument(
            JsonSerializer.SerializeToElement(request.Definition, FlowRepository.JsonOptions));
        if (!validation.IsValid)
            return new(false, validation, null, null);

        var missing = await ResourceReferenceCheck.ProblemsAsync(request.Definition, resources, ct);
        if (missing is { Count: > 0 })
            return new(false, null, missing, null);

        var saved = await flows.SaveAsync(request, ct);
        return new(true, null, null, saved);
    }

    public Task<(FlowDeleteResult Result, string? Reason)> DeleteFlowAsync(Guid id, bool detachLibraries, CancellationToken ct) =>
        flows.DeleteFlowAsync(id, ct, detachLibraries: detachLibraries);

    public Task<bool> DeleteDraftAsync(Guid id, CancellationToken ct) =>
        flows.DeleteDraftAsync(id, ct);

    public Task<FlowDraftResponse?> CloneFlowAsync(Guid id, string? name, CancellationToken ct) =>
        flows.CloneFlowAsync(id, name, ct);

    public Task<FlowDraftResponse?> CreateFromTemplateAsync(
        Guid templateId,
        CreateFlowFromTemplateRequest request,
        CancellationToken ct) =>
        flows.CreateFromTemplateAsync(templateId, request, templates, validator, ct);

    public async Task<CreateFlowResult> CreateFlowAsync(
        JsonElement document,
        bool? publish,
        FlowComponentRegistry registry,
        CancellationToken ct)
    {
        if (document.ValueKind != JsonValueKind.Object)
        {
            return new CreateFlowResult(
                false,
                false,
                new FlowValidationResult([new FlowValidationIssue("invalid-json", "", "Le corps de la requête doit être un objet JSON.")]),
                null,
                null,
                Guid.Empty);
        }

        var defElement = document;
        if (document.TryGetProperty("definition", out var nested) && nested.ValueKind == JsonValueKind.Object)
            defElement = nested;

        var definition = FlowDefinitionParser.Parse(defElement, registry);
        var shouldPublish = publish ?? (document.TryGetProperty("publish", out var pubProp)
            && pubProp.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? pubProp.GetBoolean()
            : !(document.TryGetProperty("draft", out var draftProp) && draftProp.ValueKind == JsonValueKind.True));

        if (shouldPublish)
        {
            var validation = validator.Validate(definition);
            if (!validation.IsValid)
                return new CreateFlowResult(true, false, validation, null, null, definition.Id);

            var saved = await flows.SaveAsync(new SaveFlowRequest(0, definition), ct);
            return new CreateFlowResult(true, true, null, saved, null, definition.Id);
        }
        else
        {
            var validation = validator.ValidateDraft(definition);
            if (!validation.IsValid)
                return new CreateFlowResult(false, false, validation, null, null, definition.Id);

            var draft = await flows.SaveDraftAsync(new SaveFlowDraftRequest(0, definition), ct);
            return new CreateFlowResult(false, true, null, null, draft, definition.Id);
        }
    }

    public async Task<IReadOnlyDictionary<string, JsonElement>?> ResolveHandoffDefaultsAsync(
        FlowDefinition flow, CancellationToken ct)
    {
        if (!flow.Nodes.Any(Encoding.Contracts.Executions.RuntimeHandoffPolicy.IsHandoff))
            return null;
        var settings = Get<PluginConfigurationRepository>();
        if (settings is null) return null;
        var effective = await settings.ResolveEffectiveAsync(flow.Id, ["nodes"], ct);
        return effective.GetValueOrDefault("nodes");
    }

    public async Task<IReadOnlyList<FlowComponentPackSummary>> ListComponentPacksAsync(CancellationToken ct)
    {
        var repo = Get<FlowComponentPackRepository>();
        return repo is not null ? await repo.ListAsync(ct) : [];
    }

    public async Task<FlowComponentPackDetails?> GetComponentPackAsync(Guid id, CancellationToken ct)
    {
        var repo = Get<FlowComponentPackRepository>();
        return repo is not null ? await repo.GetAsync(id, ct) : null;
    }

    public async Task<FlowComponentPackDetails?> CreateComponentPackFromFlowAsync(
        Guid id, CreateFlowComponentPackRequest request, CancellationToken ct)
    {
        var repo = Get<FlowComponentPackRepository>();
        return repo is not null ? await repo.CreateFromFlowAsync(id, request, ct) : null;
    }

    public async Task<IReadOnlyList<EncodingProfileSummary>> ListEncodingProfilesAsync(CancellationToken ct)
    {
        var repo = Get<EncodingProfileRepository>();
        return repo is not null ? await repo.ListAsync(ct) : [];
    }

    public async Task<EncodingProfileDetails?> GetEncodingProfileAsync(Guid id, CancellationToken ct)
    {
        var repo = Get<EncodingProfileRepository>();
        return repo is not null ? await repo.GetAsync(id, ct) : null;
    }

    public async Task<EncodingProfileVersion?> GetEncodingProfileVersionAsync(Guid id, int version, CancellationToken ct)
    {
        var repo = Get<EncodingProfileRepository>();
        return repo is not null ? await repo.GetVersionAsync(id, version, ct) : null;
    }

    public async Task<EncodingProfileDetails?> CreateEncodingProfileAsync(Guid id, EncodingProfileDraft draft, CancellationToken ct)
    {
        var repo = Get<EncodingProfileRepository>();
        return repo is not null ? await repo.CreateAsync(id, draft, ct) : null;
    }

    public async Task<EncodingProfileDetails?> SaveEncodingProfileDraftAsync(Guid id, SaveEncodingProfileDraftRequest request, CancellationToken ct)
    {
        var repo = Get<EncodingProfileRepository>();
        return repo is not null ? await repo.SaveDraftAsync(id, request, ct) : null;
    }

    public async Task<EncodingProfileVersion?> PublishEncodingProfileAsync(Guid id, int expectedRevision, CancellationToken ct)
    {
        var repo = Get<EncodingProfileRepository>();
        return repo is not null ? await repo.PublishAsync(id, expectedRevision, ct) : null;
    }
}
