namespace Encoding.Contracts.Tools;

/// <summary>Capabilities observed from one authenticated provider, never a guessed model list.</summary>
public sealed record AiDiscoveredModel(string Id, string Label, IReadOnlyList<string> ReasoningEfforts,
    string? DefaultReasoningEffort = null, bool IsDefault = false, bool ReasoningKnown = false);
public sealed record AiModelCatalog(string Provider, string State, IReadOnlyList<AiDiscoveredModel> Models,
    DateTimeOffset? ObservedAt, DateTimeOffset CheckedAt, string? Message = null);
public sealed record AiNodeModelCatalog(Guid NodeId, string Name, IReadOnlyList<AiModelCatalog> Providers);
public sealed record AiDisabledModel(string Provider, string Model);
public sealed record AiModelPolicy(IReadOnlyList<AiDisabledModel> DisabledModels);
public sealed record AiClusterModelCatalog(IReadOnlyList<AiNodeModelCatalog> Nodes, AiModelPolicy Policy);
public sealed record AiModelPolicyUpdate(string Provider, string Model, bool Enabled);
public sealed record AiTurnModelSelection(string Provider, string? Model, string? ReasoningEffort, Guid? NodeId = null);
