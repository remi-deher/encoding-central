namespace Encoding.Api.Resources;

public sealed record ResourceFile(string Name, string Sha256, long Bytes);

public sealed record ResourceVersion(Guid ResourceId, int Version, IReadOnlyList<ResourceFile> Files, long TotalBytes, DateTimeOffset CreatedAt);

public sealed record ResourceSummary(Guid Id, string Kind, string Name, string Description, int LatestVersion, long TotalBytes, DateTimeOffset UpdatedAt);

public sealed record ResourceDetails(ResourceSummary Summary, IReadOnlyList<ResourceVersion> Versions);

public sealed record ResourceFileReference(string Name, string Sha256);

public sealed record CreateResourceRequest(string Kind, string Name, string Description, IReadOnlyList<ResourceFileReference> Files);

public sealed record AddResourceVersionRequest(IReadOnlyList<ResourceFileReference> Files);

public sealed record UpdateResourceRequest(string Name, string Description);

public sealed record ResourceUsage(Guid FlowId, string FlowName, int FlowVersion, string NodeId, string? NodeName);

/// <summary>Erreur de validation d'une ressource, dont le message est affiché tel quel.</summary>
public sealed class ResourceException(string message) : Exception(message);
