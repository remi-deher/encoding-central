namespace Encoding.NodeAgent.Runtime;

/// <summary>In-process handoff of a published artifact between worker and publisher on the same node.</summary>
public interface ILocalPublicationArtifactStore
{
    void Register(Guid executionId, long epoch, string sha256, string path);
    bool TryGet(Guid executionId, long epoch, string sha256, out string path);
    void Remove(Guid executionId, long epoch);
}