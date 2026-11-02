using System.Text.Json;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Nodes;
using Encoding.Contracts.Flows;
using Encoding.NodeAgent.Runtime;
using Encoding.NodeAgent.Runtime.Media;
using Encoding.NodeAgent.Services;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class MediaAssemblyTests
{
    private sealed class Capabilities : IToolCapabilityProbe
    {
        public Task<IReadOnlyList<NodeToolCapability>> DiscoverAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<NodeToolCapability>>([]);
    }
    private static Dictionary<string,JsonElement> Config(object value)=>JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(JsonSerializer.Serialize(value))!;

    [MkvToolsFact]
    public async Task SubtitlesFontsAndNamesShareOnePrivateMediaOutput()
    {
        var directory=Path.Combine(Path.GetTempPath(),"builder-assembly-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var tools=new ProcessToolRunner();var scanner=new MediaScanner(tools);var actions=new MediaActions(tools,scanner,new Capabilities());
            var source=Path.Combine(directory,"source.mkv");var subtitle=Path.Combine(directory,"source.srt");
            await File.WriteAllTextAsync(subtitle,"1\n00:00:00,000 --> 00:00:00,500\nBonjour\n");
            await tools.CheckedAsync("ffmpeg",["-v","error","-y","-f","lavfi","-i","color=size=128x72:duration=1:rate=5","-f","lavfi","-i","anullsrc=r=48000:cl=stereo","-i",subtitle,"-t","1","-map","0:v","-map","1:a","-map","2:s","-c:v","mpeg4","-c:a","aac","-c:s","srt","-metadata:s:a:0","language=fra","-metadata:s:s:0","language=fra",source],directory,default);
            var sha=await SafeMediaPaths.DigestAsync(source,default);var metadata=await scanner.ScanAsync(source,default);
            var resource=Path.Combine(directory,"fonts");Directory.CreateDirectory(resource);
            // Synthetic attachment: the test checks preservation/attachment, not font rendering.
            await File.WriteAllBytesAsync(Path.Combine(resource,"TestFont.ttf"),[0,1,0,0]);
            using var capture=RuntimeCommandCapture.Begin();var invocation=Guid.NewGuid();
            MediaActionOutcome result;
            using(capture.BeginStep("assembly",invocation))
                result=await actions.AssembleAsync(Config(new { manageSubtitles=true, addFonts=true }),metadata,null,source,directory,"assembly",default,resource);
            Assert.NotNull(result.Primary);
            var commands=capture.ForStep("assembly",invocation);
            Assert.Single(commands,command=>command.Command.Contains("mkvmerge") && command.Command.Contains(" -o "));
            Assert.Contains(commands,command=>command.Command.Contains("mkvpropedit") && command.Command.Contains("--add-attachment"));
            var after=await scanner.ValidateAsync(result.Primary,"video",metadata,default);
            Assert.Contains(after.GetProperty("streams").EnumerateArray(),stream=>stream.GetProperty("codec_name").GetString()=="ass");
            Assert.Equal(sha,await SafeMediaPaths.DigestAsync(source,default));
            Assert.Single(Directory.GetFiles(directory,"out-*.mkv"));
            var identification=await tools.CheckedAsync("mkvmerge",["-J",result.Primary!],directory,default);
            Assert.Contains("TestFont.ttf",identification);
            var empty=Path.Combine(directory,"empty-fonts");Directory.CreateDirectory(empty);
            await Assert.ThrowsAsync<SubtitleException>(()=>actions.AssembleAsync(Config(new { manageSubtitles=true, addFonts=true }),metadata,null,source,directory,"failure",default,empty));
            Assert.Equal(sha,await SafeMediaPaths.DigestAsync(source,default));
        }
        finally { Directory.Delete(directory,true); }
    }
}
