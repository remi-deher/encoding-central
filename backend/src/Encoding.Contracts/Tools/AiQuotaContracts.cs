namespace Encoding.Contracts.Tools;

public sealed record AiQuotaWindow(string Id, string Name, double? UsedPercent, DateTimeOffset? ResetsAt)
{
    public double? RemainingPercent => UsedPercent is { } used ? Math.Clamp(100 - used, 0, 100) : null;
}

/// <summary>Account-wide provider observation, never an application token budget.</summary>
public sealed record AiQuotaSnapshot(string Provider, string State, IReadOnlyList<AiQuotaWindow> Windows,
    DateTimeOffset? ObservedAt, DateTimeOffset CheckedAt, bool Experimental, string? Message = null);
public sealed record AiNodeQuotas(Guid NodeId, string Name, IReadOnlyList<AiQuotaSnapshot> Providers);
public sealed record AiClusterQuotas(IReadOnlyList<AiNodeQuotas> Nodes);
