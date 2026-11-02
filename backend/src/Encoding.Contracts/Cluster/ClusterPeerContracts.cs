namespace Encoding.Contracts.Cluster;

public sealed record ClusterPeerPublicIdentity(Guid PeerId, string PublicKey, string? AdvertisedUrl = null);

/// <summary>An approver-signed membership grant. The data key is wrapped for the admitted peer only.</summary>
public sealed record ClusterPeerAdmissionGrant(
    Guid ClusterId,
    Guid PeerId,
    Guid? NodeId,
    string PublicKey,
    Guid ApprovedByPeerId,
    string? WrappedDataKey,
    string KeyFingerprint,
    DateTimeOffset IssuedAt,
    DateTimeOffset? RevokedAt,
    string Signature);

public sealed record ClusterJoinBootstrap(
    Guid ClusterId,
    string ClusterName,
    Guid PeerId,
    IReadOnlyList<ClusterPeerAdmissionGrant> Grants, ClusterUserNameSnapshot? UserNames = null);

public sealed record ClusterUserName(Guid Id, string Username);
public sealed record ClusterUserNameSnapshot(Guid IssuerPeerId, string Timestamp,
    IReadOnlyList<ClusterUserName> Users, string Signature);
