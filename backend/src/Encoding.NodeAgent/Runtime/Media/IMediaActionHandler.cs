namespace Encoding.NodeAgent.Runtime.Media;

public interface IMediaActionHandler
{
    bool CanHandle(string actionId);
    Task<MediaActionOutcome> ExecuteAsync(MediaActionContext context, CancellationToken ct);
}
