using System.Text.Json;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Flows.Conditions;
using Encoding.NodeAgent.Runtime.Media;

namespace Encoding.NodeAgent.Runtime;

internal sealed partial class RuntimeFlowExecution
{
    private async Task<StepOutcome> PublishResultAsync(StepContext ctx)
    {
        var node = ctx.Node;
        await EnsureScanAsync();
        if (state.Variables.ContainsKey("runtime.published")) throw new PublishResultException("Le résultat a déjà été publié par un bloc de ce flow.");
        if (state.Context.Plan.Count > 0) throw new PublishResultException("Le plan de traitement n'a pas été exécuté : placez la publication après « Exécuter le traitement ».");
        if (state.Context.CurrentSha256 == state.SourceSha) ctx.Output = "unchanged";
        else
        {
            await EnsurePublicationIntegrityAsync();
            var playback = await guards.CheckForStepAsync(lease.ExecutionId, lease.Epoch, lease.MediaPath, lease.LibraryId, lease.Guards, true, job.Token);
            if (!playback.Allowed) throw new PublishResultException(playback.Detail ?? "Lecture en cours ; le résultat n'a pas été publié.");
            // Rejouable après une reprise (ADR 0004) : une publication déjà demandée ou faite pour ce même résultat est adoptée, jamais refaite.
            var already = await client.PublicationStateAsync(lease.ExecutionId, lease.Epoch, job.Token);
            if (already.Requested && already.Sha256 != state.Context.CurrentSha256) throw new PublishResultException("Un autre résultat a déjà été publié pour cette exécution.");
            if (lease.SourceNodeId == client.NodeId && lease.SourceRoot is not null)
                localPublicationArtifacts.Register(lease.ExecutionId, lease.Epoch, state.Context.CurrentSha256, state.Current);
            if (!already.Requested)
                await client.PostAsync<object>($"executions/{lease.ExecutionId:D}/finish", new FinishRuntimeExecution(lease.Epoch, Guid.NewGuid(), state.Revision, "succeeded",
                    "Résultat validé ; publication demandée par le flow.", state.Context.CurrentSha256, state.Context.Extension, state.Context.BeforeMetadata,
                        state.Context.CurrentMetadata, null, commandCapture.Pending(), true), job.Token);
            RuntimePublicationState published;
            while (!(published = await client.PublicationStateAsync(lease.ExecutionId, lease.Epoch, job.Token)).Published) await Task.Delay(TimeSpan.FromSeconds(2), job.Token);
            state.PublicationConfirmed = true;
            state.Variables["runtime.published"] = JsonSerializer.SerializeToElement(new { path = published.Path, sha256 = state.Context.CurrentSha256 });
            await RenewAsync(job.Token);
            logger.LogInformation("Publication {Node} confirmée : {Path}.", node.Id, published.Path);
            ctx.Output = "published";
        }

        return StepOutcome.Continue();
    }

    private async Task<StepOutcome> EditInPlaceAsync(StepContext ctx)
    {
        var node = ctx.Node; var c = ctx.Config;
        await EnsureScanAsync();
        if (lease.SourceNodeId != client.NodeId || lease.SourceRoot is null)
            throw new InPlaceEditException("La modification sur place exige un worker avec accès direct à la bibliothèque : placez « Exiger un accès direct » (mode strict) avant ce bloc.");
        if (state.Context.Plan.Count > 0 || state.Context.CurrentSha256 != state.SourceSha || state.Variables.ContainsKey("runtime.inPlaceEdited"))
            throw new InPlaceEditException("La modification sur place doit précéder tout autre bloc qui modifie le média, et ne peut être utilisée qu'une fois par flow.");
        var edit = InPlaceTrackEdit.From(c);
        // Lecture en cours (Plex) : attendre sa fin ; la garde est ensuite revérifiée juste avant l'écriture.
        var playbackWait = await guards.CheckForStepAsync(lease.ExecutionId, lease.Epoch, lease.MediaPath, lease.LibraryId, lease.Guards, true, job.Token);
        if (!playbackWait.Allowed) throw new InPlaceEditException(playbackWait.Detail ?? "Lecture en cours ; la source n'a pas été modifiée.");
        var result = await new InPlaceMkvEditor(tools).ApplyAsync(lease.SourceRoot, Path.Combine(options.DataDirectory, "media-locks"), lease.ExecutionId, lease.MediaPath, state.SourceSha,
            edit.Arguments, edit.AlreadyApplied, edit.Verify, directory, MediaActions.Flag(c, "keepBackup"), () => !job.IsCancellationRequested, job.Token,
            async token =>
            {
                if (lease.SourceWriteGuardsSupported) await client.PostAsync<object>($"executions/{lease.ExecutionId:D}/source-write/authorize",
                    new SourceWriteAuthorization(lease.Epoch, lease.SourceRoot), token);
                return (await guards.CheckForStepAsync(lease.ExecutionId, lease.Epoch, lease.MediaPath, lease.LibraryId, lease.Guards, false,
                    token)) is { Allowed: false } last ? last.Detail ?? "Lecture en cours." : null;
            });
        logger.LogInformation("Modification sur place {Node} : {Detail}", node.Id, result.Detail);
        if (result.Status == "unchanged") ctx.Output = "unchanged";
        else state.Variables["runtime.inPlaceEdited"] = JsonSerializer.SerializeToElement(result.NewSha256);

        return StepOutcome.Continue();
    }

    private async Task<StepOutcome> ApplyTrackNamesAsync(StepContext ctx)
    {
        var node = ctx.Node; var c = ctx.Config;
        await EnsureScanAsync();
        var outcome = await actions.TrackNamesAsync(
            c, state.Context.CurrentMetadata!.Value,
            state.Variables.TryGetValue("tracks.origin", out var saved) ? saved : null,
            state.Current, directory, node.Id, job.Token);
        if (MediaActions.Text(c, "mode", "apply") == "snapshot") { if (outcome.Value is { } snapshot) state.Variables["tracks.origin"] = snapshot; }
        else if (outcome.Primary is null) ctx.Output = "unchanged";
        else
        {
            if (outcome.Value is { ValueKind: JsonValueKind.Array } notes) foreach (var note in notes.EnumerateArray()) logger.LogInformation("Noms des pistes : {Note}", note.GetString());
            await ApplyOutcomeAsync(outcome with { Value = null }, node.Id);
        }

        return StepOutcome.Continue();
    }

    private async Task<StepOutcome> ManageSubtitlesAsync(StepContext ctx)
    {
        var node = ctx.Node; var c = ctx.Config;
        await EnsureScanAsync();
        var outcome = await actions.SubtitleManageAsync(c, state.Context.CurrentMetadata!.Value, state.Current, directory, node.Id, job.Token);
        if (outcome.Primary is null) ctx.Output = "unchanged";
        else
        {
            if (outcome.Value is { ValueKind: JsonValueKind.Array } notes) foreach (var note in notes.EnumerateArray()) logger.LogInformation("Sous-titres : {Note}", note.GetString());
            await ApplyOutcomeAsync(outcome with { Value = null }, node.Id);
        }

        return StepOutcome.Continue();
    }

    private async Task<StepOutcome> AddFontsOrAssembleAsync(StepContext ctx)
    {
        var node = ctx.Node; var id = ctx.Id; var c = ctx.Config;
        await EnsureScanAsync();
        string? resourceFolder = null;
        if ((id != "base-assemble-media" || MediaActions.Flag(c, "addFonts")) && MediaActions.Text(c, "resource") is { Length: > 0 } resourceText)
        {
            if (!Encoding.Contracts.Flows.ResourceReference.TryParse(resourceText, out var resourceReference)) throw new SubtitleException("Référence de ressource de polices invalide.");
            try
            {
                var info = await client.ResourceVersionAsync(resourceReference, job.Token);
                resourceFolder = Path.Combine(directory, "resources", resourceReference.Id.ToString("N") + "-" + resourceReference.Version);
                await client.EnsureResourceAsync(info, ResourceCache.Root(options), resourceFolder, job.Token);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException) { throw new SubtitleException("Ressource de polices indisponible : " + exception.Message); }
        }
        var outcome = id == "base-assemble-media"
            ? await actions.AssembleAsync(c, state.Context.CurrentMetadata!.Value, state.Variables.TryGetValue("tracks.origin", out var origin) ? origin : null, state.Current,
                directory, node.Id, job.Token, resourceFolder)
            : await actions.AddFontsAsync(c, state.Current, directory, node.Id, job.Token, resourceFolder);
        if (outcome.Value is { ValueKind: JsonValueKind.Array } added)
            foreach (var note in added.EnumerateArray())
                if (note.GetString() is { Length: > 0 } text)
                    logger.LogInformation(id == "base-assemble-media" ? "Assemblage média : {Note}" : "{Note}", text);
        if (outcome.Primary is null) ctx.Output = "unchanged";
        else await ApplyOutcomeAsync(outcome with { Value = null }, node.Id);

        return StepOutcome.Continue();
    }

    private async Task<StepOutcome> ApplyAudioRulesAsync(StepContext ctx)
    {
        var node = ctx.Node; var c = ctx.Config;
        await EnsureScanAsync();
        var rules = AudioRuleOptions.From(c); var audioPlan = AudioRulePlanner.Plan(state.Context.CurrentMetadata!.Value, rules);
        foreach (var decision in audioPlan.Decisions) logger.LogInformation("Audio piste {Track} : {Note}", decision.AudioIndex, decision.Note);
        var steps = audioPlan.Steps(node.Id, rules);
        if (steps.Count == 0) ctx.Output = "unchanged";
        else if (FfmpegBuilderPolicy.Active(state.Variables) || MediaActions.Text(c, "processingMode", "plan") == "plan")
        {
            if (state.Variables.TryGetValue("plan.tool", out var selected) && selected.GetString() != "ffmpeg")
                throw new MediaToolException("outil du plan incompatible avec cette action", 1);
            state.Context = state.Context with { Plan = [.. state.Context.Plan, .. steps] };
        }
        else await ApplyOutcomeAsync(await actions.FfmpegPlanAsync(steps, state.Current, directory, node.Id, client.NodeId, job.Token, knownMetadata: state.Context.CurrentMetadata), node.Id);

        return StepOutcome.Continue();
    }

    private async Task<StepOutcome> DetectAutoCropAsync(StepContext ctx)
    {
        var node = ctx.Node; var id = ctx.Id; var c = ctx.Config;
        await EnsureScanAsync();
        var duration = 0.0;
        if (state.Context.CurrentMetadata is { } meta && meta.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var durProp)
            && durProp.ValueKind == JsonValueKind.String && double.TryParse(durProp.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var d))
            duration = d;

        var (w, h) = (0, 0);
        if (state.Context.CurrentMetadata is { } m && m.TryGetProperty("streams", out var str))
        {
            foreach (var s in str.EnumerateArray())
            {
                if (s.TryGetProperty("codec_type", out var ctProp) && ctProp.GetString() == "video")
                {
                    if (s.TryGetProperty("width", out var wP)) w = wP.GetInt32();
                    if (s.TryGetProperty("height", out var hP)) h = hP.GetInt32();
                    break;
                }
            }
        }

        var samplePoints = int.Parse(MediaActions.Number(c, "samplePoints", 5));
        var sampleSecs = int.Parse(MediaActions.Number(c, "sampleDurationSeconds", 2));
        var threshold = int.Parse(MediaActions.Number(c, "threshold", 24));
        var roundMul = int.Parse(MediaActions.Number(c, "roundMultiple", 2));

        var crop = await actions.DetectCropAsync(state.Current, directory, samplePoints, sampleSecs, threshold, roundMul, duration, job.Token);
        if (crop is not null && crop.IsSignificant(w, h))
        {
            ctx.Output = "cropped";
            state.Variables["video.crop"] = JsonSerializer.SerializeToElement(crop.ToFilter());
            state.Variables["video.crop.w"] = JsonSerializer.SerializeToElement(crop.Width);
            state.Variables["video.crop.h"] = JsonSerializer.SerializeToElement(crop.Height);
            state.Variables["video.crop.x"] = JsonSerializer.SerializeToElement(crop.X);
            state.Variables["video.crop.y"] = JsonSerializer.SerializeToElement(crop.Y);
            state.Variables["video.crop.filter"] = JsonSerializer.SerializeToElement(crop.ToFilter());

            if (FfmpegBuilderPolicy.Active(state.Variables) || MediaActions.Text(c, "processingMode", "plan") == "plan")
            {
                if (state.Variables.TryGetValue("plan.tool", out var selected) && selected.GetString() != "ffmpeg")
                    throw new MediaToolException("outil du plan incompatible avec cette action", 1);
                var stepCfg = new Dictionary<string, JsonElement>(c) { ["cropFilter"] = JsonSerializer.SerializeToElement(crop.ToFilter()) };
                state.Context = state.Context with { Plan = [.. state.Context.Plan, new RuntimePlanStep(id, stepCfg, node.Id)] };
            }
            else
            {
                var stepCfg = new Dictionary<string, JsonElement>(c) { ["cropFilter"] = JsonSerializer.SerializeToElement(crop.ToFilter()) };
                await ApplyOutcomeAsync(await actions.FfmpegPlanAsync([new(id, stepCfg, node.Id)], state.Current, directory, node.Id, client.NodeId, job.Token,
                    knownMetadata: state.Context.CurrentMetadata), node.Id);
            }
        }
        else ctx.Output = "no-crop";

        return StepOutcome.Continue();
    }

    private async Task<StepOutcome> DetectDeinterlaceAsync(StepContext ctx)
    {
        var node = ctx.Node; var id = ctx.Id; var c = ctx.Config;
        await EnsureScanAsync();
        var frames = int.Parse(MediaActions.Number(c, "detectionFrames", 1000));
        var thresholdPercent = double.Parse(MediaActions.Number(c, "interlaceThresholdPercent", 10), System.Globalization.CultureInfo.InvariantCulture);
        var algo = MediaActions.Text(c, "algorithm", "bwdif");

        var isInterlaced = await actions.DetectInterlacedAsync(state.Current, directory, frames, thresholdPercent, job.Token);
        if (isInterlaced)
        {
            ctx.Output = "deinterlaced";
            state.Variables["video.deinterlace"] = JsonSerializer.SerializeToElement(algo);

            if (FfmpegBuilderPolicy.Active(state.Variables) || MediaActions.Text(c, "processingMode", "plan") == "plan")
            {
                if (state.Variables.TryGetValue("plan.tool", out var selected) && selected.GetString() != "ffmpeg")
                    throw new MediaToolException("outil du plan incompatible avec cette action", 1);
                state.Context = state.Context with { Plan = [.. state.Context.Plan, new RuntimePlanStep(id, c, node.Id)] };
            }
            else
            {
                await ApplyOutcomeAsync(
                    await actions.FfmpegPlanAsync([new(id, c, node.Id)], state.Current, directory, node.Id, client.NodeId, job.Token,
                        knownMetadata: state.Context.CurrentMetadata),
                    node.Id);
            }
        }
        else ctx.Output = "progressive";

        return StepOutcome.Continue();
    }

    private async Task<StepOutcome> GuardFileSizeAsync(StepContext ctx)
    {
        var node = ctx.Node; var c = ctx.Config;
        await EnsureFilesAsync();
        var currentInfo = new FileInfo(state.Current);
        var sourceInfo = new FileInfo(state.Original);
        var currentBytes = currentInfo.Exists ? currentInfo.Length : 0;
        var sourceBytes = sourceInfo.Exists ? sourceInfo.Length : 0;
        var minReduction = decimal.Parse(MediaActions.Number(c, "minReductionPercent", 10), System.Globalization.CultureInfo.InvariantCulture);
        var (reduced, percentChange) = FileSizeGuard.Evaluate(sourceBytes, currentBytes, minReduction);

        state.Variables["runtime.fileReductionPercent"] = JsonSerializer.SerializeToElement(percentChange);
        state.Variables["runtime.currentSizeBytes"] = JsonSerializer.SerializeToElement(currentBytes);
        state.Variables["runtime.sourceSizeBytes"] = JsonSerializer.SerializeToElement(sourceBytes);

        if (reduced) ctx.Output = "reduced";
        else
        {
            ctx.Output = "larger";
            if (MediaActions.Text(c, "actionOnFailure", "branch") == "revert")
            {
                File.Copy(state.Original, state.Current, true);
                var sha = MediaFileStamp.Read(state.Current).ToDigest();
                state.Context = state.Context with { CurrentSha256 = sha, Extension = Path.GetExtension(state.Original), CurrentMetadata = state.Context.BeforeMetadata };
                logger.LogInformation("Garde de taille {Node} : gain insuffisant ({Percent:F1}%), source d'origine restaurée.", node.Id, percentChange);
            }
        }

        return StepOutcome.Continue();
    }

    private async Task<StepOutcome> FilterLanguagesAsync(StepContext ctx)
    {
        var node = ctx.Node; var c = ctx.Config;
        await EnsureScanAsync();
        var outcome = await actions.FilterLanguagesAsync(c, state.Context.CurrentMetadata!.Value, state.Current, directory, node.Id, job.Token);
        if (outcome.Primary is null) ctx.Output = "unchanged";
        else
        {
            ctx.Output = "filtered";
            await ApplyOutcomeAsync(outcome, node.Id);
        }

        return StepOutcome.Continue();
    }

    private async Task<StepOutcome> RemoveCommentaryAsync(StepContext ctx)
    {
        var node = ctx.Node; var c = ctx.Config;
        await EnsureScanAsync();
        var outcome = await actions.RemoveCommentaryAsync(c, state.Context.CurrentMetadata!.Value, state.Current, directory, node.Id, job.Token);
        if (outcome.Primary is null) ctx.Output = "unchanged";
        else
        {
            ctx.Output = "removed";
            await ApplyOutcomeAsync(outcome, node.Id);
        }

        return StepOutcome.Continue();
    }

    private async Task<StepOutcome> ExecuteProcessingAsync(StepContext ctx)
    {
        var node = ctx.Node; var c = ctx.Config;
        await EnsureScanAsync();
        if (state.Context.Plan.Count == 0 && !MediaActions.Flag(c, "forceProcessing")) ctx.Output = "skipped";
        else
        {
            var plan = state.Context.Plan.ToList();
            if (plan.Count == 0 && state.Variables.TryGetValue("plan.tool", out var selectedTool)
                && selectedTool.GetString() == "mkvtoolnix") plan.Add(new("base-remux-matroska", new Dictionary<string, JsonElement>()));
            if (plan.Count == 0) plan.Add(new("video-remux-media", new Dictionary<string, JsonElement> { ["container"] = JsonSerializer.SerializeToElement(state.Context.Extension.TrimStart('.')) }));
            var index = 0;
            while (index < plan.Count)
            {
                if (IsFfmpegPlan(plan[index].ComponentId))
                {
                    var batch = new List<RuntimePlanStep>(); while (index < plan.Count && IsFfmpegPlan(plan[index].ComponentId)) batch.Add(plan[index++]);
                    var outcome = await actions.FfmpegPlanAsync(batch, state.Current, directory, node.Id + index, client.NodeId, job.Token,
                        !state.Variables.TryGetValue("plan.preserveUnchangedTracks", out var preserve) || preserve.GetBoolean(),
                        knownMetadata: state.Context.CurrentMetadata);
                    await ApplyOutcomeAsync(outcome, node.Id);
                }
                else { var step = plan[index++]; await ApplyOutcomeAsync(await actions.ExecuteAsync(step.ComponentId, step.Configuration, state.Current, directory,
                    node.Id + index, ExtraAsync, client.NodeId, job.Token), node.Id); }
            }
            state.Context = state.Context with { Plan = [] };
        }

        return StepOutcome.Continue();
    }

    private async Task<StepOutcome> ApplyGenericOperationAsync(StepContext ctx)
    {
        var node = ctx.Node; var id = ctx.Id; var c = ctx.Config;
        await EnsureScanAsync();
        IReadOnlyList<RuntimePlanStep> requested = [new(id, c, node.Id)];
        if (c.ContainsKey("trackSelection")) requested = MediaTrackSelection.Expand(requested, state.Context.CurrentMetadata!.Value);
        if (id is "base-resize-video" or "video-resize")
        {
            var video = state.Context.CurrentMetadata!.Value.GetProperty("streams").EnumerateArray().FirstOrDefault(stream => MediaTrackSelection.Text(stream, "codec_type") == "video");
            if (video.ValueKind == JsonValueKind.Object && !VideoResizePolicy.ShouldResize(c, MediaTrackSelection.Number(video, "width"), MediaTrackSelection.Number(video, "height"))) requested = [];
        }
        if (requested.Count == 0) ctx.Output = "unchanged";
        else if (FfmpegBuilderPolicy.IsPlanned(id, c, state.Variables))
        {
            if (state.Variables.TryGetValue("plan.tool", out var selected) && (IsFfmpegPlan(id) != (selected.GetString() == "ffmpeg")))
                throw new MediaToolException("outil du plan incompatible avec cette action", 1);
            state.Context = state.Context with { Plan = [.. state.Context.Plan, .. requested] };
        }
        else
        {
            var outcome = await actions.ExecuteAsync(id, c, state.Current, directory, node.Id, ExtraAsync, client.NodeId, job.Token, state.Context.CurrentMetadata);
            if (outcome.Value is { } value) state.Variables[MediaActions.Text(c, "resultVariable", "result")] = value;
            await ApplyOutcomeAsync(outcome, node.Id);
            if (outcome.Primary is null && MediaActions.Flag(c, "refreshMetadata")) state.Context = state.Context with { CurrentMetadata = await scanner.ScanAsync(state.Current, job.Token) };
        }

        return StepOutcome.Continue();
    }
}
