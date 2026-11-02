using Encoding.Api.Executions;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class RuntimeStepEffectsTests
{
    [Theory]
    [InlineData("base","base-publish-result",true)]
    [InlineData("base","base-disk-lock",true)]
    [InlineData("base","base-script",false)]
    [InlineData("video","video-transcode",true)]
    [InlineData("audio","audio-transcode",true)]
    [InlineData("mkv","mkv-remux-matroska",true)]
    [InlineData("mkv","mkv-edit-in-place",false)]
    [InlineData("plex","plex-refresh-library",true)]
    [InlineData("jellyfin","jellyfin-refresh-item",true)]
    [InlineData("radarr","radarr-refresh-movie",true)]
    [InlineData("radarr","radarr-search-movie",false)]
    [InlineData("sonarr","sonarr-search-missing-episodes",false)]
    [InlineData("custom","custom-anything",false)]
    public void ClassifiesReplayableBlocks(string plugin,string component,bool replayable) =>
        Assert.Equal(replayable,RuntimeStepEffects.IsReplayable(plugin,component));
}
