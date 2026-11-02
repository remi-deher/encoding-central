using System.Text.Json;
using Encoding.Api.Executions;
using Encoding.Api.Flows;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Flows.Conditions;
using Encoding.Contracts.Nodes;
using Encoding.Contracts.Plugins;

namespace Encoding.Runtime.Tests;

internal static class ConditionScenarios
{
    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    public static void Run(FlowComponentRegistry registry, FlowDefinitionValidator validator, RuntimeEligibility eligibility)
    {
        var before=J(new { streams=new object[]{new{codec_type="video",codec_name="h264",width=1920,height=1080}}, format=new{size="1000",duration="12.5"} });
        var after=J(new { streams=new object[]{new{codec_type="video",codec_name="hevc",width=3840,height=2160},new{codec_type="audio",codec_name="aac",channels=6,tags=new{language="fra"}}}, format=new{size="800",duration="12.5"} });
        var source=Guid.NewGuid();var worker=Guid.NewGuid();var library=Guid.NewGuid();
        var runtime=new RuntimeContext(new string('b',64),".mkv",new Dictionary<string,JsonElement>{["score"]=J(42),["enabled"]=J(true)},before,after,[]);
        var data=new ConditionEvaluationContext(runtime,source,worker,library,"folder/source.mkv",new string('a',64));
        ConditionRule Rule(string field,string op,object? value=null,string? variable=null,string scope="current")=>new(field,op,value is null?null:J(value),variable,scope);
        var tree=new ConditionGroup("all",[Rule("video.codec","eq","HEVC"),new ConditionGroup("any",[Rule("video.height","gt",1080),Rule("variable.number","lt",1,"score")]),Rule("audio.languages","contains","FRA")]);
        var document=ConditionTree.Serialize(tree);
        Check(ConditionTree.Validate(document,"video") is null,"Nested AND/OR conditions validate.");
        Check(document.GetProperty("kind").GetString()=="group" && document.GetProperty("children")[0].GetProperty("kind").GetString()=="rule","The portable tree has explicit discriminators.");
        Check(ConditionTree.Evaluate(ConditionTree.Parse(document,"video"),data),"Nested AND/OR conditions evaluate against current metadata.");
        Check(ConditionTree.RequiresMetadata(tree),"Media criteria require a scan.");
        Check(ConditionTree.Evaluate(Rule("video.codec","eq","h264",scope:"source"),data),"The original snapshot remains selectable after conversion.");
        Check(!ConditionTree.Evaluate(Rule("subtitles.present","eq",true),data),"Absent subtitle tracks are an observed absence.");
        Check(ConditionTree.Evaluate(Rule("file.duration","eq",12.5m),data),"ffprobe numeric strings use invariant culture.");
        Check(ConditionTree.Evaluate(Rule("file.size","lt",1000),data),"File sizes compare numerically.");
        Check(ConditionTree.Evaluate(Rule("variable.boolean","eq",true,"enabled"),data),"Boolean variables stay typed.");
        Check(ConditionTree.Evaluate(Rule("source.nodeId","eq",source.ToString()),data) && ConditionTree.Evaluate(Rule("library.id","eq",library.ToString()),data),"Origin criteria use stable execution context.");
        Check(ConditionTree.Evaluate(Rule("processing.modified","eq",true),data),"Modified state uses current and source digests.");
        var hdrAfter=J(new { streams=new object[]{new{codec_type="video",codec_name="h264",pix_fmt="yuv420p10le",color_transfer="smpte2084"},new{codec_type="video",codec_name="mjpeg",disposition=new{attached_pic=1}},new{codec_type="video",codec_name="h264",pix_fmt="yuv420p"}}, format=new{size="1",duration="1"} });
        var copyData=data with { Runtime=runtime with { CurrentMetadata=hdrAfter } };
        Check(ConditionTree.Evaluate(Rule("video.count","eq",2),copyData),"Attached pictures are not video tracks.");
        Check(ConditionTree.Evaluate(Rule("video.bitDepth","gte",10),copyData) && !ConditionTree.Evaluate(Rule("video.bitDepth","gte",10),data),"The colour depth of the first video track is readable.");
        Check(ConditionTree.Evaluate(Rule("video.hdr","eq",true),copyData) && !ConditionTree.Evaluate(Rule("video.hdr","eq",true),data),"HDR is detected from the transfer characteristics.");
        Check(!ConditionTree.RequiresMetadata(new ConditionGroup("all",[Rule("variable.number","gte",42,"score"),Rule("source.nodeId","eq",source.ToString())])),"Variable and origin rules do not need a scan.");
        Check(ConditionTree.Evaluate(Rule("variable.text","missing",variable:"absent"),data),"Missing values can be tested explicitly.");
        Check(!ConditionTree.Evaluate(new ConditionGroup("all",[Rule("variable.text","exists",variable:"absent"),Rule("variable.text","eq","x","absent")]),data),"AND short-circuits an explicit presence guard.");
        ExpectError(()=>ConditionTree.Evaluate(Rule("variable.text","eq","x","absent"),data),"A missing value is an error, not false.");
        ExpectError(()=>ConditionTree.Evaluate(Rule("variable.text","eq","42","score"),data),"A wrong runtime type is an error.");
        var invalid=new ConditionGroup("all",[Rule("video.height","contains","1080")]);
        Check(ConditionTree.Validate(ConditionTree.Serialize(invalid),"video") is not null,"Incompatible operators are rejected.");
        Check(ConditionTree.Validate(document,"music") is not null,"Media-incompatible fields are rejected.");
        Check(ConditionTree.Validate(ConditionTree.Serialize(new ConditionGroup("all",[])),"video") is not null,"Empty groups cannot execute.");
        Check(ConditionTree.Validate(ConditionTree.Serialize(new ConditionGroup("all",[])),"video",true) is null,"Drafts can retain incomplete groups.");
        Check(ConditionTree.Validate(J(new{kind="group",match="all",children=new[]{new{kind="rule",field="file.extension",@operator="eq",value="mkv",code="arbitrary"}}}),"video") is not null,"Unknown properties cannot introduce executable code.");
        ConditionExpression deep=Rule("file.extension","eq","mkv");
        for(var i=0;i<ConditionTree.MaxDepth;i++) deep=new ConditionGroup("all",[deep]);
        Check(ConditionTree.Validate(ConditionTree.Serialize(deep),"video") is not null,"Excessive nesting is rejected.");
        Check(ConditionTree.Validate(ConditionTree.Serialize(new ConditionGroup("all",Enumerable.Repeat<ConditionExpression>(Rule("file.extension","eq","mkv"),100).ToArray())),"video") is not null,"Excessive node counts are rejected.");

        var manifest=registry.Plugins.Single(plugin=>plugin.Id=="conditions");var component=manifest.Components.Single(c=>c.Id=="conditions-match");
        var presetJson=JsonSerializer.SerializeToElement(manifest,new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Check(presetJson.GetProperty("components")[0].GetProperty("configurationSchema").GetProperty("fields")[0].GetProperty("conditionPresets")[0].GetProperty("expression").GetProperty("kind").GetString()=="group","Catalog presets serialize as portable groups.");
        var input=registry.Plugins.Single(plugin=>plugin.Id=="base").Components.Single(component=>component.Id=="base-input-file");
        var reference=new FlowComponentReference("conditions",manifest.Version,component.Id,component.Version,1);
        var flow=new FlowDefinition(1,Guid.NewGuid(),1,"Native conditions","video",[
            new("input",null,new("base","1.0.0",input.Id,input.Version,1),new Dictionary<string,JsonElement>()),
            new("condition",null,reference,new Dictionary<string,JsonElement>{["rules"]=document})],
            [new("edge","input","success","condition")]);
        Check(validator.Validate(flow).IsValid,"The flow validator accepts the structured tree.");
        IReadOnlyList<NodeToolCapability> caps=[new("plugin/base","1.0.0",["general"],[]),new("plugin/conditions",manifest.Version,["general"],[]),new("ffprobe","7.0",["video"],[])];
        Check(eligibility.CanRun(flow,caps,worker),"Native conditions do not require JavaScript or a third-party process.");
        Check(!eligibility.CanRun(flow,caps.Where(cap=>cap.ToolId!="plugin/conditions").ToArray(),worker),"An old agent cannot execute the new plugin.");
        var draft=flow with { Nodes=[flow.Nodes[0],flow.Nodes[1] with { Configuration=new Dictionary<string,JsonElement>{["rules"]=ConditionTree.Serialize(new ConditionGroup("all",[]))} }] };
        Check(validator.ValidateDraft(draft).IsValid && !validator.Validate(draft).IsValid,"An incomplete draft cannot become an execution.");

        // MediaConditionEvaluator tests on dedicated conditions
        var configVideoCodec = new Dictionary<string, JsonElement> { ["codec"] = J("hevc") };
        Check(MediaConditionEvaluator.Evaluate("conditions-video-codec", configVideoCodec, after) == "yes", "Video codec HEVC matches after metadata.");
        Check(MediaConditionEvaluator.Evaluate("conditions-video-codec", configVideoCodec, before) == "no", "Video codec HEVC does not match before metadata.");
        var configVideoCodecInvert = new Dictionary<string, JsonElement> { ["codec"] = J("hevc"), ["invert"] = J(true) };
        Check(MediaConditionEvaluator.Evaluate("conditions-video-codec", configVideoCodecInvert, after) == "no", "Invert video codec matches not HEVC.");
        Check(MediaConditionEvaluator.Evaluate("conditions-video-codec", configVideoCodecInvert, before) == "yes", "Invert video codec matches not HEVC on h264.");

        var configAudioCodec = new Dictionary<string, JsonElement> { ["codec"] = J("aac"), ["scope"] = J("any") };
        Check(MediaConditionEvaluator.Evaluate("conditions-audio-codec", configAudioCodec, after) == "yes", "Audio codec AAC matches.");
        Check(MediaConditionEvaluator.Evaluate("conditions-audio-codec", new Dictionary<string, JsonElement> { ["codec"] = J("eac3") }, after) == "no", "Audio codec EAC3 does not match.");

        var configResolution = new Dictionary<string, JsonElement> { ["resolution"] = J("1080p"), ["operator"] = J("gte") };
        Check(MediaConditionEvaluator.Evaluate("conditions-video-resolution", configResolution, after) == "yes", "3840x2160 is at least 1080p.");
        Check(MediaConditionEvaluator.Evaluate("conditions-video-resolution", new Dictionary<string, JsonElement> { ["resolution"] = J("4k"), ["operator"] = J("eq") }, after) == "yes", "3840x2160 is exactly 4K.");
        Check(MediaConditionEvaluator.Evaluate("conditions-video-resolution", new Dictionary<string, JsonElement> { ["resolution"] = J("720p"), ["operator"] = J("lte") }, after) == "no", "3840x2160 is not <= 720p.");

        var configChannels = new Dictionary<string, JsonElement> { ["channels"] = J("6"), ["operator"] = J("gte"), ["scope"] = J("first") };
        Check(MediaConditionEvaluator.Evaluate("conditions-audio-channels", configChannels, after) == "yes", "6 channels is >= 6.");
        Check(MediaConditionEvaluator.Evaluate("conditions-audio-channels", new Dictionary<string, JsonElement> { ["channels"] = J("8"), ["operator"] = J("gte") }, after) == "no", "6 channels is not >= 8.");

        var configLang = new Dictionary<string, JsonElement> { ["language"] = J("fra"), ["scope"] = J("any") };
        Check(MediaConditionEvaluator.Evaluate("conditions-audio-language", configLang, after) == "yes", "Language fra is present.");
        Check(MediaConditionEvaluator.Evaluate("conditions-audio-language", new Dictionary<string, JsonElement> { ["language"] = J("eng") }, after) == "no", "Language eng is not present.");

        // MediaConditionEvaluator tests on HDR and BitDepth
        var hdrMetadata = J(new {
            streams = new object[] {
                new {
                    codec_type = "video",
                    codec_name = "hevc",
                    width = 3840,
                    height = 2160,
                    pix_fmt = "yuv420p10le",
                    color_transfer = "smpte2084",
                    color_primaries = "bt2020",
                    side_data_list = new object[] {
                        new { side_data_type = "DOVI configuration record" },
                        new { side_data_type = "Mastering display metadata" }
                    }
                }
            }
        });
        Check(MediaConditionEvaluator.Evaluate("conditions-video-hdr", new Dictionary<string, JsonElement> { ["format"] = J("dolby-vision") }, hdrMetadata) == "yes", "Dolby Vision detected from side data.");
        Check(MediaConditionEvaluator.Evaluate("conditions-video-hdr", new Dictionary<string, JsonElement> { ["format"] = J("hdr10") }, hdrMetadata) == "yes", "HDR10 transfer detected.");
        Check(MediaConditionEvaluator.Evaluate("conditions-video-hdr", new Dictionary<string, JsonElement> { ["format"] = J("any-hdr") }, hdrMetadata) == "yes", "Any HDR matches HDR file.");
        Check(MediaConditionEvaluator.Evaluate("conditions-video-hdr", new Dictionary<string, JsonElement> { ["format"] = J("sdr") }, hdrMetadata) == "no", "HDR file is not SDR.");
        Check(MediaConditionEvaluator.Evaluate("conditions-video-hdr", new Dictionary<string, JsonElement> { ["format"] = J("sdr") }, before) == "yes", "SDR video correctly identified as SDR.");

        Check(MediaConditionEvaluator.Evaluate("conditions-video-bitdepth", new Dictionary<string, JsonElement> { ["depth"] = J("10-bit") }, hdrMetadata) == "yes", "10-bit detected via yuv420p10le.");
        Check(MediaConditionEvaluator.Evaluate("conditions-video-bitdepth", new Dictionary<string, JsonElement> { ["depth"] = J("8-bit") }, hdrMetadata) == "no", "10-bit is not 8-bit.");
        Check(MediaConditionEvaluator.Evaluate("conditions-video-bitdepth", new Dictionary<string, JsonElement> { ["depth"] = J("8-bit") }, before) == "yes", "Standard stream defaults to 8-bit.");

        // Flow validation with dedicated condition components
        var videoCodecComp = manifest.Components.Single(c => c.Id == "conditions-video-codec");
        var videoCodecRef = new FlowComponentReference("conditions", manifest.Version, videoCodecComp.Id, videoCodecComp.Version, 1);
        var hdrComp = manifest.Components.Single(c => c.Id == "conditions-video-hdr");
        var hdrRef = new FlowComponentReference("conditions", manifest.Version, hdrComp.Id, hdrComp.Version, 1);
        var flowVideoCodec = new FlowDefinition(1, Guid.NewGuid(), 1, "Video codec condition", "video", [
            new("input", null, new("base", "1.0.0", input.Id, input.Version, 1), new Dictionary<string, JsonElement>()),
            new("cond", null, videoCodecRef, new Dictionary<string, JsonElement> { ["codec"] = J("hevc") }),
            new("hdr", null, hdrRef, new Dictionary<string, JsonElement> { ["format"] = J("dolby-vision") })],
            [new("edge1", "input", "success", "cond"), new("edge2", "cond", "yes", "hdr")]);
        Check(validator.Validate(flowVideoCodec).IsValid, "Flow with dedicated conditions-video-codec and conditions-video-hdr is valid.");

        // ADR 0070: FileFlows-inspired processing blocks unit tests
        // 1. CropDetector
        var cropLine = "[Parsed_cropdetect_0 @ 0x7f8] crop=1920:800:0:140 ts:10.000000";
        var parsedCrop = CropDetector.ParseCropDetectLine(cropLine);
        Check(parsedCrop is not null && parsedCrop.Width == 1920 && parsedCrop.Height == 800 && parsedCrop.X == 0 && parsedCrop.Y == 140, "Crop line parsed.");
        Check(parsedCrop!.ToFilter() == "crop=1920:800:0:140", "Crop filter string matches.");
        Check(parsedCrop.IsSignificant(1920, 1080), "Letterbox crop is significant on 1080p.");
        Check(!new CropCoordinates(1916, 1076, 2, 2).IsSignificant(1920, 1080, 16), "Minor crop is insignificant.");
        var consensus = CropDetector.CalculateConsensus([
            "[crop] crop=1920:804:0:138",
            "[crop] crop=1920:800:0:140",
            "[crop] crop=1920:800:0:140"
        ], roundMultiple: 2);
        Check(consensus is not null && consensus.Width == 1920 && consensus.Height == 800, "Crop consensus median calculated.");

        // 2. DeinterlaceDetector
        var idetOut = "Multi frame detection: TFF: 400 BFF: 100 Progressive: 500 Undetermined: 0";
        var parsedIdet = DeinterlaceDetector.ParseIdetOutput(idetOut);
        Check(parsedIdet is not null && parsedIdet.Value.Tff == 400 && parsedIdet.Value.Progressive == 500, "Idet output parsed.");
        Check(DeinterlaceDetector.IsInterlaced(idetOut, 10.0), "50% interlaced frames detected as interlaced.");
        var progOut = "Multi frame detection: TFF: 10 BFF: 10 Progressive: 980 Undetermined: 0";
        Check(!DeinterlaceDetector.IsInterlaced(progOut, 10.0), "2% interlaced frames detected as progressive.");

        // 3. FileSizeGuard
        var (red1, p1) = FileSizeGuard.Evaluate(1000, 800, 10m);
        Check(red1 && p1 == 20m, "20% reduction meets 10% threshold.");
        var (red2, p2) = FileSizeGuard.Evaluate(1000, 1050, 10m);
        Check(!red2 && p2 == -5m, "File increase fails reduction guard.");

        // 4. LanguageFilterPlanner
        var multiLangStreams = J(new object[] {
            new { codec_type = "audio", index = 1, tags = new { language = "fra" }, disposition = new { @default = 1 } },
            new { codec_type = "audio", index = 2, tags = new { language = "eng" }, disposition = new { @default = 0 } },
            new { codec_type = "audio", index = 3, tags = new { language = "ger" }, disposition = new { @default = 0 } },
            new { codec_type = "subtitle", index = 4, tags = new { language = "ger" }, disposition = new { forced = 0 } },
            new { codec_type = "subtitle", index = 5, tags = new { language = "ita" }, disposition = new { forced = 1 } }
        });
        var droppedAudio = LanguageFilterPlanner.IdentifyTracksToRemove(multiLangStreams, "audio", ["fra", "eng"], keepUndefined: true, keepFirstAudioFallback: true, keepForcedSubtitles: true);
        Check(droppedAudio.Contains(3) && !droppedAudio.Contains(1) && !droppedAudio.Contains(2), "German audio dropped, French and English kept.");
        var droppedSubs = LanguageFilterPlanner.IdentifyTracksToRemove(multiLangStreams, "subtitles", ["fra"], keepUndefined: true, keepFirstAudioFallback: true, keepForcedSubtitles: true);
        Check(droppedSubs.Contains(4) && !droppedSubs.Contains(5), "German sub dropped, Italian forced sub kept.");

        // 5. CommentaryFilterPlanner
        var commentaryStreams = J(new object[] {
            new { codec_type = "audio", index = 1, tags = new { title = "Film Français VFF" } },
            new { codec_type = "audio", index = 2, tags = new { title = "Director's Commentary" } },
            new { codec_type = "audio", index = 3, disposition = new { descriptions = 1 } },
            new { codec_type = "subtitle", index = 4, tags = new { title = "Commentaires audio sous-titres" } }
        });
        var droppedCommentary = CommentaryFilterPlanner.IdentifyCommentaryTracks(commentaryStreams, checkAudio: true, checkSubtitles: true);
        Check(!droppedCommentary.Contains(1), "Main audio track not dropped.");
        Check(droppedCommentary.Contains(2) && droppedCommentary.Contains(3) && droppedCommentary.Contains(4), "Commentary and descriptive tracks dropped.");

        // 6. Flow validation with ADR 0070 processing blocks
        // 6. Flow validation with ADR 0070 processing blocks
        var baseManifest = registry.Plugins.Single(plugin => plugin.Id == "base");
        var videoManifest = registry.Plugins.Single(plugin => plugin.Id == "video");
        var audioManifest = registry.Plugins.Single(plugin => plugin.Id == "audio");
        var cropComp = videoManifest.Components.Single(c => c.Id == "video-auto-crop");
        var sizeGuardComp = videoManifest.Components.Single(c => c.Id == "video-file-size-guard");
        var langFilterComp = audioManifest.Components.Single(c => c.Id == "audio-filter-languages");
        var deintComp = videoManifest.Components.Single(c => c.Id == "video-deinterlace");
        var commComp = audioManifest.Components.Single(c => c.Id == "audio-remove-commentary");

        Dictionary<string, JsonElement> Config(FlowComponentDescriptor comp, Dictionary<string, JsonElement>? overrides = null)
        {
            var dict = comp.ConfigurationSchema.Fields.Where(f => f.DefaultValue is not null).ToDictionary(f => f.Id, f => f.DefaultValue!.Value);
            if (overrides is not null) foreach (var kv in overrides) dict[kv.Key] = kv.Value;
            return dict;
        }

        var flowFileFlows = new FlowDefinition(1, Guid.NewGuid(), 1, "FileFlows processing pipeline", "video", [
            new("input", null, new("base", baseManifest.Version, input.Id, input.Version, 1), Config(input)),
            new("crop", null, new("video", videoManifest.Version, cropComp.Id, cropComp.Version, 1), Config(cropComp)),
            new("deint", null, new("video", videoManifest.Version, deintComp.Id, deintComp.Version, 1), Config(deintComp)),
            new("lang", null, new("audio", audioManifest.Version, langFilterComp.Id, langFilterComp.Version, 1), Config(langFilterComp)),
            new("comm", null, new("audio", audioManifest.Version, commComp.Id, commComp.Version, 1), Config(commComp)),
            new("guard", null, new("video", videoManifest.Version, sizeGuardComp.Id, sizeGuardComp.Version, 1), Config(sizeGuardComp, new() { ["minReductionPercent"] = J(10) }))
        ], [
            new("e1", "input", "success", "crop"),
            new("e2", "crop", "cropped", "deint"),
            new("e3", "crop", "no-crop", "deint"),
            new("e4", "deint", "deinterlaced", "lang"),
            new("e5", "deint", "progressive", "lang"),
            new("e6", "lang", "filtered", "comm"),
            new("e7", "lang", "unchanged", "comm"),
            new("e8", "comm", "removed", "guard"),
            new("e9", "comm", "unchanged", "guard")
        ]);
        Check(validator.Validate(flowFileFlows).IsValid, "FileFlows pipeline with all 5 new processing blocks validates.");

        var legacyFlow = new FlowDefinition(1, Guid.NewGuid(), 1, "Legacy FileFlows pipeline", "video", [
            new("input", null, new("base", "1.6.0", input.Id, input.Version, 1), Config(input)),
            new("crop", null, new("base", "1.6.0", "base-auto-crop", "1.0.0", 1), Config(cropComp)),
            new("deint", null, new("base", "1.6.0", "base-deinterlace-video", "1.0.0", 1), Config(deintComp)),
            new("lang", null, new("base", "1.6.0", "base-filter-languages", "1.0.0", 1), Config(langFilterComp)),
            new("comm", null, new("base", "1.6.0", "base-remove-commentary", "1.0.0", 1), Config(commComp)),
            new("guard", null, new("base", "1.6.0", "base-file-size-guard", "1.0.0", 1), Config(sizeGuardComp, new() { ["minReductionPercent"] = J(10) }))
        ], flowFileFlows.Connections);
        Check(validator.Validate(legacyFlow).IsValid, "Legacy FileFlows pipeline with base-* aliases validates.");
    }
    private static void ExpectError(Action action,string message)
    {
        try { action(); }
        catch(ConditionEvaluationException) { return; }
        throw new InvalidOperationException(message);
    }
}
