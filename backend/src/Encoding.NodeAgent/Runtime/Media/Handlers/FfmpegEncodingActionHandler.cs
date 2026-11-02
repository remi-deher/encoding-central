using Encoding.Contracts.Executions;

namespace Encoding.NodeAgent.Runtime.Media.Handlers;

public sealed class FfmpegEncodingActionHandler(MediaActions mediaActions) : IMediaActionHandler
{
    public bool CanHandle(string actionId) => actionId is
        "base-transcode-video" or "video-transcode" or
        "base-transcode-audio" or "audio-transcode" or
        "base-normalize-audio" or "audio-normalize" or
        "base-resize-video" or "video-resize" or
        "base-remux-media" or "video-remux-media" or
        "base-auto-crop" or "video-auto-crop" or
        "base-deinterlace-video" or "video-deinterlace";

    public Task<MediaActionOutcome> ExecuteAsync(MediaActionContext context, CancellationToken ct)
    {
        return mediaActions.FfmpegPlanAsync(
            [new(context.Id, context.Config, context.Node)],
            context.Current,
            context.Directory,
            context.Node,
            context.WorkerId,
            ct,
            knownMetadata: context.KnownMetadata);
    }
}
