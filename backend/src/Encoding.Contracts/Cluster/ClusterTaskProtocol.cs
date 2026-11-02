namespace Encoding.Contracts.Cluster;

public sealed record ClusterTaskProbe(int ProtocolVersion,Guid ClusterId,Guid Nonce,string Kind);
public sealed record ClusterTaskProbeResponse(int ProtocolVersion,Guid ClusterId,Guid Nonce,Guid PeerId,IReadOnlyList<string> Capabilities);
