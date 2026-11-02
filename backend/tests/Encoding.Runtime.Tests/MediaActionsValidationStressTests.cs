using System.Text.Json;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Nodes;
using Encoding.NodeAgent.Runtime;
using Encoding.NodeAgent.Runtime.Media;
using Encoding.NodeAgent.Runtime.Media.Handlers;
using Encoding.NodeAgent.Services;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class MediaActionsValidationStressTests
{
    private sealed class StubCapabilities : IToolCapabilityProbe
    {
        public Task<IReadOnlyList<NodeToolCapability>> DiscoverAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<NodeToolCapability>>([]);
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static Dictionary<string, JsonElement> Config(object value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;

    private static MediaActions CreateMediaActions(IProcessToolRunner? tools = null, IEnumerable<IMediaActionHandler>? handlers = null)
    {
        var toolRunner = tools ?? new ProcessToolRunner();
        var scanner = new MediaScanner(toolRunner);
        var probe = new StubCapabilities();
        return new MediaActions(toolRunner, scanner, probe, handlers: handlers);
    }

    [Fact]
    public async Task ValidateOutputDetectsVideoCodecMismatch()
    {
        var actions = CreateMediaActions();
        var before = Json(new
        {
            format = new { duration = "120.0" },
            streams = new[] { new { codec_type = "video", codec_name = "h264" } }
        });
        var after = Json(new
        {
            format = new { duration = "120.0" },
            streams = new[] { new { codec_type = "video", codec_name = "h264" } }
        });

        // Test 1: Required codec "hevc" but output is "h264"
        var configHevc = Config(new { requiredVideoCodec = "hevc" });
        var report = await actions.ValidateOutputAsync("out.mkv", "orig.mkv", before, after, configHevc, "/tmp", default);
        Assert.False(report.GetProperty("valid").GetBoolean());
        var issues = report.GetProperty("issues").EnumerateArray().Select(i => i.GetString()!).ToArray();
        Assert.Contains(issues, i => i.Contains("Le codec vidéo ne correspond pas au codec demandé (hevc)"));

        // Test 2: Required codec "hevc-or-source" but output changed from h264 to vp9
        var afterVp9 = Json(new
        {
            format = new { duration = "120.0" },
            streams = new[] { new { codec_type = "video", codec_name = "vp9" } }
        });
        var configHevcOrSource = Config(new { requiredVideoCodec = "hevc-or-source" });
        var reportVp9 = await actions.ValidateOutputAsync("out.mkv", "orig.mkv", before, afterVp9, configHevcOrSource, "/tmp", default);
        Assert.False(reportVp9.GetProperty("valid").GetBoolean());
        var issuesVp9 = reportVp9.GetProperty("issues").EnumerateArray().Select(i => i.GetString()!).ToArray();
        Assert.Contains(issuesVp9, i => i.Contains("Une piste vidéo n’est ni en HEVC ni conservée dans son codec source"));

        // Test 3: Missing video in output
        var afterNoVideo = Json(new
        {
            format = new { duration = "120.0" },
            streams = new[] { new { codec_type = "audio", codec_name = "aac" } }
        });
        var reportNoVideo = await actions.ValidateOutputAsync("out.mkv", "orig.mkv", before, afterNoVideo, Config(new { }), "/tmp", default);
        Assert.False(reportNoVideo.GetProperty("valid").GetBoolean());
        var issuesNoVideo = reportNoVideo.GetProperty("issues").EnumerateArray().Select(i => i.GetString()!).ToArray();
        Assert.Contains(issuesNoVideo, i => i.Contains("Aucune piste vidéo exploitable"));
    }

    [Fact]
    public async Task ValidateOutputDetectsDurationDiscrepancy()
    {
        var actions = CreateMediaActions();
        var before = Json(new
        {
            format = new { duration = "100.0" },
            streams = new[] { new { codec_type = "video", codec_name = "h264" } }
        });

        // Test 1: Duration difference exceeding tolerance (tolerance = 2.0s, delta = 5.5s)
        var afterExceeded = Json(new
        {
            format = new { duration = "105.5" },
            streams = new[] { new { codec_type = "video", codec_name = "h264" } }
        });
        var configTol = Config(new { durationToleranceSeconds = 2.0 });
        var report = await actions.ValidateOutputAsync("out.mkv", "orig.mkv", before, afterExceeded, configTol, "/tmp", default);
        Assert.False(report.GetProperty("valid").GetBoolean());
        var issues = report.GetProperty("issues").EnumerateArray().Select(i => i.GetString()!).ToArray();
        Assert.Contains(issues, i => i.Contains("La durée diffère de") && (i.Contains("5.5") || i.Contains("5,5")) && i.Contains("tolérance : 2"));

        // Test 2: Missing duration in output
        var afterNoDur = Json(new
        {
            format = new { },
            streams = new[] { new { codec_type = "video", codec_name = "h264" } }
        });
        var reportNoDur = await actions.ValidateOutputAsync("out.mkv", "orig.mkv", before, afterNoDur, Config(new { }), "/tmp", default);
        Assert.False(reportNoDur.GetProperty("valid").GetBoolean());
        var issuesNoDur = reportNoDur.GetProperty("issues").EnumerateArray().Select(i => i.GetString()!).ToArray();
        Assert.Contains(issuesNoDur, i => i.Contains("La durée source ou la durée de sortie est absente"));
    }

    [Fact]
    public async Task ValidateOutputDetectsAudioIssues()
    {
        var actions = CreateMediaActions();
        var before = Json(new
        {
            format = new { duration = "60.0" },
            streams = new object[]
            {
                new { codec_type = "video", codec_name = "h264" },
                new { codec_type = "audio", codec_name = "dts", channels = 6, channel_layout = "5.1(side)" }
            }
        });

        // Test 1: Forbidden audio codec
        var afterForbidden = Json(new
        {
            format = new { duration = "60.0" },
            streams = new object[]
            {
                new { codec_type = "video", codec_name = "h264" },
                new { codec_type = "audio", codec_name = "mp3", channels = 2, channel_layout = "stereo" }
            }
        });
        var configForbidden = Config(new { forbiddenAudioCodecs = "mp3,wma" });
        var reportForbidden = await actions.ValidateOutputAsync("out.mkv", "orig.mkv", before, afterForbidden, configForbidden, "/tmp", default);
        Assert.False(reportForbidden.GetProperty("valid").GetBoolean());
        Assert.Contains(reportForbidden.GetProperty("issues").EnumerateArray().Select(i => i.GetString()!),
            i => i.Contains("Codec audio interdit dans la sortie : mp3"));

        // Test 2: Allowed audio codec violation
        var configAllowed = Config(new { allowedAudioCodecs = "aac,opus" });
        var reportAllowed = await actions.ValidateOutputAsync("out.mkv", "orig.mkv", before, afterForbidden, configAllowed, "/tmp", default);
        Assert.False(reportAllowed.GetProperty("valid").GetBoolean());
        Assert.Contains(reportAllowed.GetProperty("issues").EnumerateArray().Select(i => i.GetString()!),
            i => i.Contains("Codec audio non autorisé : mp3"));

        // Test 3: Max audio channels exceeded
        var afterChannels = Json(new
        {
            format = new { duration = "60.0" },
            streams = new object[]
            {
                new { codec_type = "video", codec_name = "h264" },
                new { codec_type = "audio", codec_name = "aac", channels = 8, channel_layout = "7.1" }
            }
        });
        var configMaxChannels = Config(new { maxAudioChannels = 6 });
        var reportChannels = await actions.ValidateOutputAsync("out.mkv", "orig.mkv", before, afterChannels, configMaxChannels, "/tmp", default);
        Assert.False(reportChannels.GetProperty("valid").GetBoolean());
        Assert.Contains(reportChannels.GetProperty("issues").EnumerateArray().Select(i => i.GetString()!),
            i => i.Contains("Une piste audio dépasse la limite de 6 canaux"));

        // Test 4: preserveAudioChannels violation
        var afterChannelMismatch = Json(new
        {
            format = new { duration = "60.0" },
            streams = new object[]
            {
                new { codec_type = "video", codec_name = "h264" },
                new { codec_type = "audio", codec_name = "aac", channels = 2, channel_layout = "stereo" }
            }
        });
        var configPreserveChannels = Config(new { preserveAudioChannels = true });
        var reportMismatch = await actions.ValidateOutputAsync("out.mkv", "orig.mkv", before, afterChannelMismatch, configPreserveChannels, "/tmp", default);
        Assert.False(reportMismatch.GetProperty("valid").GetBoolean());
        Assert.Contains(reportMismatch.GetProperty("issues").EnumerateArray().Select(i => i.GetString()!),
            i => i.Contains("Le nombre de canaux de la piste audio 1 a changé"));

        // Test 5: preserveAudioLayout violation (LFE removed or changed)
        var afterLayoutMismatch = Json(new
        {
            format = new { duration = "60.0" },
            streams = new object[]
            {
                new { codec_type = "video", codec_name = "h264" },
                new { codec_type = "audio", codec_name = "dts", channels = 6, channel_layout = "6.0" }
            }
        });
        var configPreserveLayout = Config(new { preserveAudioLayout = true });
        var reportLayout = await actions.ValidateOutputAsync("out.mkv", "orig.mkv", before, afterLayoutMismatch, configPreserveLayout, "/tmp", default);
        Assert.False(reportLayout.GetProperty("valid").GetBoolean());
        Assert.Contains(reportLayout.GetProperty("issues").EnumerateArray().Select(i => i.GetString()!),
            i => i.Contains("Le canal LFE de la piste audio 1 a changé"));
    }

    [Fact]
    public async Task ValidateOutputDetectsSubtitleIssues()
    {
        var actions = CreateMediaActions();
        var before = Json(new
        {
            format = new { duration = "60.0" },
            streams = new object[]
            {
                new { codec_type = "video", codec_name = "h264" },
                new {
                    codec_type = "subtitle",
                    codec_name = "subrip",
                    tags = new { language = "fra", title = "Complet" },
                    disposition = new { @default = 1, forced = 0, hearing_impaired = 0 }
                }
            }
        });

        // Test 1: Subtitle codec not allowed
        var afterWrongCodec = Json(new
        {
            format = new { duration = "60.0" },
            streams = new object[]
            {
                new { codec_type = "video", codec_name = "h264" },
                new { codec_type = "subtitle", codec_name = "hdmv_pgs_subtitle" }
            }
        });
        var configSub = Config(new { allowedSubtitleCodecs = "subrip,ass" });
        var reportSub = await actions.ValidateOutputAsync("out.mkv", "orig.mkv", before, afterWrongCodec, configSub, "/tmp", default);
        Assert.False(reportSub.GetProperty("valid").GetBoolean());
        Assert.Contains(reportSub.GetProperty("issues").EnumerateArray().Select(i => i.GetString()!),
            i => i.Contains("Codec de sous-titres non autorisé : hdmv_pgs_subtitle"));

        // Test 2: preserveSubtitleCount violation
        var afterNoSubs = Json(new
        {
            format = new { duration = "60.0" },
            streams = new object[] { new { codec_type = "video", codec_name = "h264" } }
        });
        var configPreserveCount = Config(new { preserveSubtitleCount = true });
        var reportNoSubs = await actions.ValidateOutputAsync("out.mkv", "orig.mkv", before, afterNoSubs, configPreserveCount, "/tmp", default);
        Assert.False(reportNoSubs.GetProperty("valid").GetBoolean());
        Assert.Contains(reportNoSubs.GetProperty("issues").EnumerateArray().Select(i => i.GetString()!),
            i => i.Contains("Le nombre de pistes de sous-titres a changé (1 → 0)"));

        // Test 3: preserveSubtitleMetadata flag / language change
        var afterChangedMetadata = Json(new
        {
            format = new { duration = "60.0" },
            streams = new object[]
            {
                new { codec_type = "video", codec_name = "h264" },
                new {
                    codec_type = "subtitle",
                    codec_name = "subrip",
                    tags = new { language = "eng", title = "Complet" },
                    disposition = new { @default = 0, forced = 0, hearing_impaired = 0 }
                }
            }
        });
        var configPreserveMeta = Config(new { preserveSubtitleMetadata = true });
        var reportMeta = await actions.ValidateOutputAsync("out.mkv", "orig.mkv", before, afterChangedMetadata, configPreserveMeta, "/tmp", default);
        Assert.False(reportMeta.GetProperty("valid").GetBoolean());
        var metaIssues = reportMeta.GetProperty("issues").EnumerateArray().Select(i => i.GetString()!).ToArray();
        Assert.Contains(metaIssues, i => i.Contains("La langue de la piste de sous-titres 1 a changé"));
        Assert.Contains(metaIssues, i => i.Contains("Le marqueur par défaut de la piste de sous-titres 1 a changé"));
    }

    [Fact]
    public async Task ValidateOutputDetectsSizeGrowthAndChapters()
    {
        var actions = CreateMediaActions();

        // Test 1: Size growth exceeding percent
        var beforeSize = Json(new
        {
            format = new { duration = "60.0", size = "1000000" },
            streams = new[] { new { codec_type = "video", codec_name = "h264" } }
        });
        var afterGrown = Json(new
        {
            format = new { duration = "60.0", size = "1500000" }, // +50%
            streams = new[] { new { codec_type = "video", codec_name = "h264" } }
        });
        var configGrowth = Config(new { maxOutputSizeGrowthPercent = 20.0 });
        var reportGrowth = await actions.ValidateOutputAsync("out.mkv", "orig.mkv", beforeSize, afterGrown, configGrowth, "/tmp", default);
        Assert.False(reportGrowth.GetProperty("valid").GetBoolean());
        Assert.Contains(reportGrowth.GetProperty("issues").EnumerateArray().Select(i => i.GetString()!),
            i => i.Contains("La sortie dépasse la taille source de plus de 20 %"));

        // Test 2: Chapter count mismatch
        var beforeChapters = Json(new
        {
            format = new { duration = "60.0" },
            chapters = new object[] { new { id = 1 }, new { id = 2 }, new { id = 3 } },
            streams = new[] { new { codec_type = "video", codec_name = "h264" } }
        });
        var afterChapters = Json(new
        {
            format = new { duration = "60.0" },
            chapters = new object[] { new { id = 1 } },
            streams = new[] { new { codec_type = "video", codec_name = "h264" } }
        });
        var configChapters = Config(new { preserveChapterCount = true });
        var reportChapters = await actions.ValidateOutputAsync("out.mkv", "orig.mkv", beforeChapters, afterChapters, configChapters, "/tmp", default);
        Assert.False(reportChapters.GetProperty("valid").GetBoolean());
        Assert.Contains(reportChapters.GetProperty("issues").EnumerateArray().Select(i => i.GetString()!),
            i => i.Contains("Le nombre de chapitres a changé (3 → 1)"));
    }

    [Fact]
    public async Task ValidateOutputPassesWhenEverythingIsValid()
    {
        var actions = CreateMediaActions();
        var before = Json(new
        {
            format = new { duration = "120.0", size = "1000000" },
            chapters = new object[] { new { id = 1 }, new { id = 2 } },
            streams = new object[]
            {
                new { codec_type = "video", codec_name = "hevc" },
                new { codec_type = "audio", codec_name = "aac", channels = 6, channel_layout = "5.1" },
                new {
                    codec_type = "subtitle",
                    codec_name = "subrip",
                    tags = new { language = "fra", title = "Full" },
                    disposition = new { @default = 1, forced = 0, hearing_impaired = 0 }
                }
            }
        });
        var after = Json(new
        {
            format = new { duration = "120.2", size = "800000" },
            chapters = new object[] { new { id = 1 }, new { id = 2 } },
            streams = new object[]
            {
                new { codec_type = "video", codec_name = "hevc" },
                new { codec_type = "audio", codec_name = "aac", channels = 6, channel_layout = "5.1" },
                new {
                    codec_type = "subtitle",
                    codec_name = "subrip",
                    tags = new { language = "fra", title = "Full" },
                    disposition = new { @default = 1, forced = 0, hearing_impaired = 0 }
                }
            }
        });

        var config = Config(new
        {
            requiredVideoCodec = "hevc",
            durationToleranceSeconds = 1.0,
            preserveTrackCounts = true,
            preserveAudioChannels = true,
            preserveAudioLayout = true,
            allowedAudioCodecs = "aac,ac3",
            maxAudioChannels = 6,
            allowedSubtitleCodecs = "subrip,ass",
            preserveSubtitleCount = true,
            preserveSubtitleMetadata = true,
            preserveChapterCount = true,
            maxOutputSizeGrowthPercent = 10.0
        });

        var report = await actions.ValidateOutputAsync("out.mkv", "orig.mkv", before, after, config, "/tmp", default);
        Assert.True(report.GetProperty("valid").GetBoolean());
        Assert.Empty(report.GetProperty("issues").EnumerateArray());
    }

    [Fact]
    public void StrategyHandlersCanHandleMatchesExpectedActionIds()
    {
        var tools = new ProcessToolRunner();
        var scanner = new MediaScanner(tools);
        var probe = new StubCapabilities();
        var actions = new MediaActions(tools, scanner, probe);

        var ffmpeg = new FfmpegEncodingActionHandler(actions);
        var image = new ImageActionHandler(tools, scanner);
        var mkv = new MatroskaActionHandler(tools);
        var extraction = new MediaExtractionActionHandler(tools, scanner);
        var inspection = new MediaInspectionActionHandler(tools, scanner);

        // FfmpegEncodingActionHandler
        Assert.True(ffmpeg.CanHandle("base-transcode-video"));
        Assert.True(ffmpeg.CanHandle("video-transcode"));
        Assert.True(ffmpeg.CanHandle("base-transcode-audio"));
        Assert.True(ffmpeg.CanHandle("audio-transcode"));
        Assert.True(ffmpeg.CanHandle("base-normalize-audio"));
        Assert.True(ffmpeg.CanHandle("audio-normalize"));
        Assert.True(ffmpeg.CanHandle("base-resize-video"));
        Assert.True(ffmpeg.CanHandle("video-resize"));
        Assert.True(ffmpeg.CanHandle("base-remux-media"));
        Assert.True(ffmpeg.CanHandle("video-remux-media"));
        Assert.True(ffmpeg.CanHandle("base-auto-crop"));
        Assert.True(ffmpeg.CanHandle("video-auto-crop"));
        Assert.True(ffmpeg.CanHandle("base-deinterlace-video"));
        Assert.True(ffmpeg.CanHandle("video-deinterlace"));
        Assert.False(ffmpeg.CanHandle("unknown-video-action"));

        // ImageActionHandler
        Assert.True(image.CanHandle("base-convert-image"));
        Assert.True(image.CanHandle("image-convert"));
        Assert.True(image.CanHandle("base-resize-image"));
        Assert.True(image.CanHandle("image-resize"));
        Assert.True(image.CanHandle("base-crop-image"));
        Assert.True(image.CanHandle("image-crop"));
        Assert.False(image.CanHandle("unknown-image-action"));

        // MatroskaActionHandler
        Assert.True(mkv.CanHandle("base-extract-track"));
        Assert.True(mkv.CanHandle("base-extract-attachments"));
        Assert.True(mkv.CanHandle("base-extract-chapters"));
        Assert.True(mkv.CanHandle("mkv-extract-track"));
        Assert.True(mkv.CanHandle("mkv-extract-attachments"));
        Assert.True(mkv.CanHandle("mkv-extract-chapters"));
        Assert.True(mkv.CanHandle("base-edit-track-properties"));
        Assert.True(mkv.CanHandle("base-edit-matroska-metadata"));
        Assert.True(mkv.CanHandle("base-remove-attachments"));
        Assert.True(mkv.CanHandle("base-replace-chapters"));
        Assert.True(mkv.CanHandle("mkv-edit-track-properties"));
        Assert.True(mkv.CanHandle("mkv-edit-matroska-metadata"));
        Assert.True(mkv.CanHandle("mkv-remove-attachments"));
        Assert.True(mkv.CanHandle("mkv-replace-chapters"));
        Assert.True(mkv.CanHandle("base-reorder-tracks"));
        Assert.True(mkv.CanHandle("mkv-reorder-tracks"));
        Assert.True(mkv.CanHandle("base-remux-matroska"));
        Assert.True(mkv.CanHandle("base-remove-tracks"));
        Assert.True(mkv.CanHandle("mkv-remux-matroska"));
        Assert.True(mkv.CanHandle("mkv-remove-tracks"));
        Assert.True(mkv.CanHandle("base-add-audio-track"));
        Assert.True(mkv.CanHandle("base-add-subtitle-track"));
        Assert.True(mkv.CanHandle("audio-add-track"));
        Assert.True(mkv.CanHandle("subtitles-add-track"));
        Assert.False(mkv.CanHandle("unknown-mkv-action"));

        // MediaExtractionActionHandler
        Assert.True(extraction.CanHandle("base-verify-decoding"));
        Assert.True(extraction.CanHandle("video-verify-decoding"));
        Assert.True(extraction.CanHandle("base-video-thumbnail"));
        Assert.True(extraction.CanHandle("video-thumbnail"));
        Assert.True(extraction.CanHandle("base-extract-audio"));
        Assert.True(extraction.CanHandle("audio-extract"));
        Assert.True(extraction.CanHandle("base-extract-subtitles"));
        Assert.True(extraction.CanHandle("subtitles-extract"));
        Assert.False(extraction.CanHandle("unknown-extraction-action"));

        // MediaInspectionActionHandler
        Assert.True(inspection.CanHandle("base-probe-stream"));
        Assert.True(inspection.CanHandle("probe-stream"));
        Assert.True(inspection.CanHandle("base-inspect-hdr"));
        Assert.True(inspection.CanHandle("probe-inspect-media"));
        Assert.True(inspection.CanHandle("base-export-media-report"));
        Assert.True(inspection.CanHandle("probe-export-report"));
        Assert.False(inspection.CanHandle("unknown-inspect-action"));
    }

    [Fact]
    public async Task MediaActionsExecuteAsyncDispatchesOrThrowsForUnknown()
    {
        // 1. DI empty array fallback verification via reflection
        var actionsEmptyArray = CreateMediaActions(handlers: Array.Empty<IMediaActionHandler>());
        var handlersField = typeof(MediaActions).GetField("_handlers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var handlersValueEmpty = (IReadOnlyList<IMediaActionHandler>)handlersField.GetValue(actionsEmptyArray)!;
        Assert.NotNull(handlersValueEmpty);
        Assert.Equal(5, handlersValueEmpty.Count);
        Assert.IsType<MediaInspectionActionHandler>(handlersValueEmpty[0]);
        Assert.IsType<FfmpegEncodingActionHandler>(handlersValueEmpty[1]);
        Assert.IsType<MediaExtractionActionHandler>(handlersValueEmpty[2]);
        Assert.IsType<ImageActionHandler>(handlersValueEmpty[3]);
        Assert.IsType<MatroskaActionHandler>(handlersValueEmpty[4]);

        // Verify dispatch to default handler succeeds rather than throwing 'Action média inconnue.'
        var context = new Dictionary<string, JsonElement>();
        var exEmptyDispatch = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            actionsEmptyArray.ExecuteAsync("probe-validate-output", context, "in.mkv", "/tmp", "node1", _ => Task.FromResult(""), Guid.NewGuid(), default));
        Assert.Contains("Le validateur de sortie doit recevoir les métadonnées source et résultat", exEmptyDispatch.Message);

        var exEmpty = await Assert.ThrowsAsync<IOException>(() =>
            actionsEmptyArray.ExecuteAsync("non-existent-action", context, "in.mkv", "/tmp", "node1", _ => Task.FromResult(""), Guid.NewGuid(), default));
        Assert.Equal("Action média inconnue.", exEmpty.Message);

        // 2. DI null fallback verification via reflection and dispatch
        var actionsNull = CreateMediaActions(handlers: null);
        var handlersValueNull = (IReadOnlyList<IMediaActionHandler>)handlersField.GetValue(actionsNull)!;
        Assert.NotNull(handlersValueNull);
        Assert.Equal(5, handlersValueNull.Count);
        Assert.IsType<MediaInspectionActionHandler>(handlersValueNull[0]);
        Assert.IsType<FfmpegEncodingActionHandler>(handlersValueNull[1]);
        Assert.IsType<MediaExtractionActionHandler>(handlersValueNull[2]);
        Assert.IsType<ImageActionHandler>(handlersValueNull[3]);
        Assert.IsType<MatroskaActionHandler>(handlersValueNull[4]);

        var exNullDispatch = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            actionsNull.ExecuteAsync("probe-validate-output", context, "in.mkv", "/tmp", "node1", _ => Task.FromResult(""), Guid.NewGuid(), default));
        Assert.Contains("Le validateur de sortie doit recevoir les métadonnées source et résultat", exNullDispatch.Message);

        var exNull = await Assert.ThrowsAsync<IOException>(() =>
            actionsNull.ExecuteAsync("non-existent-action", context, "in.mkv", "/tmp", "node1", _ => Task.FromResult(""), Guid.NewGuid(), default));
        Assert.Equal("Action média inconnue.", exNull.Message);

        // 3. Custom handler dispatch
        var customHandler = new TestStubHandler("custom-action");
        var actionsCustom = CreateMediaActions(handlers: [customHandler]);
        var handlersValueCustom = (IReadOnlyList<IMediaActionHandler>)handlersField.GetValue(actionsCustom)!;
        Assert.Single(handlersValueCustom);
        Assert.Same(customHandler, handlersValueCustom[0]);

        var result = await actionsCustom.ExecuteAsync("custom-action", context, "in.mkv", "/tmp", "node1", _ => Task.FromResult(""), Guid.NewGuid(), default);
        Assert.Equal("custom-dispatched", result.Primary);

        // Custom handler does NOT handle probe-validate-output
        var exCustomUnknown = await Assert.ThrowsAsync<IOException>(() =>
            actionsCustom.ExecuteAsync("probe-validate-output", context, "in.mkv", "/tmp", "node1", _ => Task.FromResult(""), Guid.NewGuid(), default));
        Assert.Equal("Action média inconnue.", exCustomUnknown.Message);

        // 4. probe-validate-output special case in MediaInspectionActionHandler
        var inspection = new MediaInspectionActionHandler(new ProcessToolRunner(), new MediaScanner(new ProcessToolRunner()));
        var actionContext = new MediaActionContext("probe-validate-output", new Dictionary<string, JsonElement>(), "in.mkv", "/tmp", "node1", _ => Task.FromResult(""), Guid.NewGuid());
        var exSpecial = await Assert.ThrowsAsync<InvalidOperationException>(() => inspection.ExecuteAsync(actionContext, default));
        Assert.Contains("Le validateur de sortie doit recevoir les métadonnées source et résultat", exSpecial.Message);
    }

    private sealed class TestStubHandler(string handledId) : IMediaActionHandler
    {
        public bool CanHandle(string actionId) => actionId == handledId;
        public Task<MediaActionOutcome> ExecuteAsync(MediaActionContext context, CancellationToken ct) =>
            Task.FromResult(new MediaActionOutcome("custom-dispatched", []));
    }
}
