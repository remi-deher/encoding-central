using Encoding.Api.Plugins;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class ExtensionActivationCapabilityTests
{
    [Fact]
    public void DisabledMediaToolDropsOnlyThatTool()
    {
        bool Plugin(string id) => id is "base" or "nodes" or "video";
        bool Tool(string id) => id is "ffmpeg" or "ffprobe";

        Assert.True(ClusterExtensionActivationState.KeepsCapability("plugin/video", Plugin, Tool));
        Assert.False(ClusterExtensionActivationState.KeepsCapability("plugin/audio", Plugin, Tool));
        Assert.True(ClusterExtensionActivationState.KeepsCapability("ffmpeg", Plugin, Tool));
        Assert.False(ClusterExtensionActivationState.KeepsCapability("mkvtoolnix", Plugin, Tool));
        Assert.True(ClusterExtensionActivationState.KeepsCapability("runtime/direct-read-v1", Plugin, Tool));
        Assert.True(ClusterExtensionActivationState.KeepsCapability("artifact-references", Plugin, Tool));
    }
}
