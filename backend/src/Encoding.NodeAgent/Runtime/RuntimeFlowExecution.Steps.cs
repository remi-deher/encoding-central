using System.Text.Json;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Flows.Conditions;
using Encoding.NodeAgent.Runtime.Media;

namespace Encoding.NodeAgent.Runtime;

internal sealed partial class RuntimeFlowExecution
{
    private sealed class StepContext(FlowNodeDefinition node, StartedRuntimeStep started)
    {
        public FlowNodeDefinition Node => node;
        public StartedRuntimeStep Started => started;
        public string Id { get; set; } = "";
        public IReadOnlyDictionary<string, JsonElement> Config { get; set; } = new Dictionary<string, JsonElement>();
        public string? Output { get; set; } = "success";
        public RuntimeHandoffSelector? Handoff { get; set; }
        public RuntimeWait? Wait { get; set; }
    }

    /// <summary>Applique une décision d'étape sur <see cref="FlowExecutionState"/> (position, sorties, terminal).</summary>
    private void ApplyStepOutcome(StepOutcome outcome)
    {
        if (outcome.Kind == StepOutcomeKind.Fail) return;
        state.Context = state.Context with { Variables = new Dictionary<string, JsonElement>(state.Variables) };
        state.Next = outcome.NextNodeId;
        if (outcome.Terminal is not null) state.Terminal = outcome.Terminal;
        state.LastOutput = outcome.Output;
    }

    // Continue = parcours local terminé ou étape achevée ; HandoffOrWait = cession/attente (pas de FinishAsync ici).
    private async Task<StepOutcome> RunStepsAsync()
    {
        while (state.Next is not null)
        {
            job.Token.ThrowIfCancellationRequested(); var node = lease.Flow.Nodes.Single(n => n.Id == state.Next);
            state.ActiveBlock = node.Id;
            var started = (await client.PostAsync<StartedRuntimeStep>($"executions/{lease.ExecutionId:D}/steps/start", new StartRuntimeStep(lease.Epoch, node.Id), job.Token))!;
            if (started.Completed is { } completed)
            {
                state.Context = completed.Context;
                state.Variables = new(state.Context.Variables);
                state.Next = completed.NextNodeId;
                state.Terminal = completed.TerminalResult;
                state.LastOutput = completed.OutputId;
                state.Current = state.DirectRead ? state.Original : Path.Combine(directory, "current" + state.Context.Extension);
                state.Materialized = false;
                continue;
            }
            using var commandStep = commandCapture.BeginStep(node.Id, started.InvocationId);
            state.Terminal = null;
            var step = new StepContext(node, started);
            var executed = await ExecuteStepAsync(step);
            if (executed.Kind == StepOutcomeKind.Fail) return executed;
            var completedStep = await CompleteStepAsync(step);
            ApplyStepOutcome(completedStep);
            if (completedStep.StopsLocalRun) return completedStep;
        }
        return StepOutcome.Continue(state.LastOutput, null, state.Terminal);
    }

    private async Task<StepOutcome> ExecuteStepAsync(StepContext ctx)
    {
        var node = ctx.Node; var started = ctx.Started;
        try
        {
            var id = node.Component.ComponentId; var c = FlowOperationConfiguration.Resolve(id, node.Configuration); ctx.Id = id; ctx.Config = c;
            if (FfmpegBuilderPolicy.Active(state.Variables) && id != FfmpegBuilderPolicy.Execute
                && !FfmpegBuilderPolicy.CanPass(node.Component.PluginId, id))
                throw new IOException("Fermez la préparation FFmpeg avant cette opération autonome.");
            // External metadata-only plugins receive no path to the direct source.
            if (state.DirectRead && !DirectMediaInspection.Allows(node.Component.PluginId, id, c, state.Context.Plan.Count, FfmpegBuilderPolicy.Active(state.Variables))
                && !(id is "base-audio-rules" or "audio-rules" or "audio-convert-by-rules" && AudioAlreadyCompliant(c))
                && !(await SourceAlreadyConformsAsync(node.Id, id, c)))
            {
                var launch = plugins.Installed.SingleOrDefault(plugin => plugin.Id == node.Component.PluginId && plugin.Version == node.Component.PluginVersion);
                var descriptor = launch is null ? null : (await plugins.DescribeAsync(launch,
                    job.Token)).Components.SingleOrDefault(component => component.Id == id && component.Version == node.Component.ComponentVersion);
                if (!DirectMediaInspection.AllowsMetadataOnly(descriptor)) await EnsureFilesAsync();
            }
            var outcome = await DispatchStepAsync(ctx);
            if (outcome.Kind != StepOutcomeKind.Continue) return outcome;
            // Planning can inspect metadata first. Materialize only after it finds real work.
            if (state.DirectRead && state.Context.Plan.Count > 0) await EnsureFilesAsync();
            if (state.Materialized && state.CurrentStamp != MediaFileStamp.Read(state.Current))
            {
                await EnsureScanAsync(); await ApplyOutcomeAsync(new(state.Current, []), node.Id);
            }
            return StepOutcome.Continue(ctx.Output, terminal: state.Terminal);
        }
        catch (AudioRuleException exception)
        {
            logger.LogInformation("Règles audio {Node} non applicables : {Detail}", node.Id, exception.Message);
            ctx.Output = "error";
            return StepOutcome.Continue(ctx.Output);
        }
        catch (PublishResultException exception)
        {
            logger.LogWarning("Publication {Node} impossible : {Detail}", node.Id, exception.Message);
            ctx.Output = "error";
            return StepOutcome.Continue(ctx.Output);
        }
        catch (InPlaceEditException exception)
        {
            logger.LogWarning("Modification sur place {Node} refusée : {Detail}", node.Id, exception.Message);
            ctx.Output = "error";
            return StepOutcome.Continue(ctx.Output);
        }
        catch (SubtitleException exception)
        {
            logger.LogInformation("Sous-titres ou polices {Node} non applicables : {Detail}", node.Id, exception.Message);
            ctx.Output = "error";
            return StepOutcome.Continue(ctx.Output);
        }
        catch (ArgumentException exception) when (node.Component.PluginId == "base" && node.Component.ComponentId is "base-disk-lock" or "base-disk-unlock")
        {
            logger.LogInformation("Verrou de disque {Node} invalide : {Detail}", node.Id, exception.Message);
            ctx.Output = "error";
            return StepOutcome.Continue(ctx.Output);
        }
        catch (ConditionEvaluationException exception)
        {
            logger.LogInformation("Condition {Node} impossible à évaluer : {Detail}", node.Id, exception.Message);
            ctx.Output = "error";
            return StepOutcome.Continue(ctx.Output);
        }
        catch (MediaToolException exception)
        {
            logger.LogInformation("Échec de l’outil pendant l’étape {Node}: {Detail}", node.Id, exception.Message);
            // Une sortie « error » non reliée n'existe pas pour le serveur : l'échec est terminal (0015) au lieu d'un checkpoint refusé qui rejouerait l'exécution.
            if (node.Component.PluginId is not ("base" or "conditions" or "probe") || FlowPath.NextNodeId(lease.Flow, node.Id, "error") is null) throw;
            ctx.Output = "error";
            await client.DownloadAsync(lease.ExecutionId, lease.Epoch, state.Context.CurrentSha256, state.Current, job.Token);
            return StepOutcome.Continue(ctx.Output);
        }
    }

    private async Task<StepOutcome> DispatchStepAsync(StepContext ctx)
    {
        if (await DispatchFlowControlStepAsync(ctx) is { } controlOutcome) return controlOutcome;
        if (await DispatchConditionAndProbeStepAsync(ctx) is { } probeOutcome) return probeOutcome;
        if (await DispatchAiAndPluginStepAsync(ctx) is { } aiOutcome) return aiOutcome;
        return await DispatchMediaOperationStepAsync(ctx);
    }

    private async Task<StepOutcome?> DispatchFlowControlStepAsync(StepContext ctx)
    {
        var id = ctx.Id; var c = ctx.Config; var node = ctx.Node;
        if (id.StartsWith("base-input-", StringComparison.Ordinal))
        {
            if (id != "base-input-file") await EnsureScanAsync();
            return StepOutcome.Continue();
        }
        if (id == "base-variable")
        {
            state.Variables[MediaActions.Text(c, "name")] = FlowVariableValue.Parse(MediaActions.Text(c, "value"));
            return StepOutcome.Continue();
        }
        if (id == FfmpegBuilderPolicy.Start) return await StartFfmpegBuilderAsync(ctx);
        if (id == FfmpegBuilderPolicy.Execute) return await ExecuteFfmpegBuilderAsync(ctx);
        if (id == "base-prepare-processing") return await PrepareProcessingAsync(ctx);
        if (id == "base-complete-flow")
        {
            await EnsureScanAsync(); ctx.Output = null; state.Terminal = MediaActions.Text(c, "result", "unchanged");
            return StepOutcome.Continue();
        }
        if (id == "base-defer-processing")
        {
            await EnsureScanAsync(); ctx.Output = null; state.Terminal = "deferred";
            state.Variables["runtime.deferMinutes"] = JsonSerializer.SerializeToElement(int.Parse(MediaActions.Number(c, "delayMinutes", 2)));
            return StepOutcome.Continue();
        }
        if (id is "base-disk-lock" or "base-disk-unlock") return await DiskLockAsync(ctx);
        if (RuntimeWaitPolicy.IsWait(node)) { ctx.Wait = RuntimeWaitPolicy.Resolve(node); ctx.Output = "resumed"; return StepOutcome.Continue(); }
        if (RuntimeHandoffPolicy.IsHandoff(node)) return await PrepareHandoffAsync(ctx);
        if (node.Component.PluginId == "nodes" && node.Component.ComponentId == RuntimeRetryPolicy.ComponentId) return StepOutcome.Continue();
        if (node.Component.PluginId == "nodes" && id == "nodes-library-access") return await CheckLibraryAccessAsync(ctx);
        if (node.Component.PluginId == "nodes" && (node.Component.ComponentId is "nodes-run-on-node" or "nodes-require-capability" or "nodes-require-direct-access")) return StepOutcome.Continue();
        return null;
    }

    private async Task<StepOutcome?> DispatchConditionAndProbeStepAsync(StepContext ctx)
    {
        var id = ctx.Id; var c = ctx.Config; var node = ctx.Node;
        if (id is "base-script" or "base-condition") return await RunScriptAsync(ctx);
        if (node.Component.PluginId == "conditions" && node.Component.ComponentId == "conditions-match") return await MatchConditionsAsync(ctx);
        if (node.Component.PluginId == "conditions" && MediaConditionEvaluator.IsMediaCondition(node.Component.ComponentId))
        {
            if (state.Context.CurrentMetadata is null) await EnsureScanAsync();
            ctx.Output = MediaConditionEvaluator.Evaluate(node.Component.ComponentId, c, state.Context.CurrentMetadata);
            return StepOutcome.Continue();
        }
        if (node.Component.PluginId == "probe" && id == MediaIntegrityPolicy.Component) return await CheckMediaIntegrityAsync(ctx);
        if (node.Component.PluginId is "base" or "probe" && id != "probe-validate-output"
            && (id.StartsWith("base-probe-", StringComparison.Ordinal) || id.StartsWith("probe-", StringComparison.Ordinal)
                && !id.StartsWith("probe-inspect-", StringComparison.Ordinal) && id != "probe-export-report"))
        {
            await EnsureScanAsync();
            state.Variables[MediaActions.Text(c, "resultVariable", "result")] = FlowMediaValues.Probe(id, state.Context.CurrentMetadata!.Value, int.Parse(MediaActions.Number(c, "trackId")));
            return StepOutcome.Continue();
        }
        if (node.Component.PluginId == "probe" && id == "probe-validate-output") return await ValidateOutputAsync(ctx);
        return null;
    }

    private async Task<StepOutcome?> DispatchAiAndPluginStepAsync(StepContext ctx)
    {
        var id = ctx.Id; var node = ctx.Node;
        if (node.Component.PluginId == "ai" && id == "ai-metadata-enricher") return await EnrichMetadataWithAiAsync(ctx);
        if (node.Component.PluginId == "ai" && id == "ai-translate-subtitles") return await TranslateSubtitlesWithAiAsync(ctx);
        if (node.Component.PluginId is not ("base" or "video" or "audio" or "subtitles" or "mkv" or "images" or "probe" or "ai"))
            return await RunExternalPluginAsync(ctx);
        return null;
    }

    private async Task<StepOutcome> DispatchMediaOperationStepAsync(StepContext ctx)
    {
        var id = ctx.Id; var node = ctx.Node;
        if (id == "base-publish-result") return await PublishResultAsync(ctx);
        if (id == "mkv-edit-in-place") return await EditInPlaceAsync(ctx);
        if (state.DirectRead && state.ConformingNodes.Contains(node.Id)) { ctx.Output = "unchanged"; return StepOutcome.Continue(); }
        if (id is "base-track-names" or "subtitles-track-names" || FlowOperationConfiguration.IsNamingOperation(id)) return await ApplyTrackNamesAsync(ctx);
        if (id is "base-subtitle-manage" or "subtitles-manage" || FlowOperationConfiguration.IsSubtitleOperation(id)) return await ManageSubtitlesAsync(ctx);
        if (id is "base-add-fonts" or "subtitles-add-fonts" or "base-assemble-media") return await AddFontsOrAssembleAsync(ctx);
        if (id is "base-audio-rules" or "audio-rules" or "audio-convert-by-rules") return await ApplyAudioRulesAsync(ctx);
        if (id is "base-auto-crop" or "video-auto-crop") return await DetectAutoCropAsync(ctx);
        if (id is "base-deinterlace-video" or "video-deinterlace") return await DetectDeinterlaceAsync(ctx);
        if (id is "base-file-size-guard" or "video-file-size-guard") return await GuardFileSizeAsync(ctx);
        if (id is "base-filter-languages" or "audio-filter-languages") return await FilterLanguagesAsync(ctx);
        if (id is "base-remove-commentary" or "audio-remove-commentary") return await RemoveCommentaryAsync(ctx);
        if (id == "base-execute-processing") return await ExecuteProcessingAsync(ctx);
        return await ApplyGenericOperationAsync(ctx);
    }

    // Publie le checkpoint ; HandoffOrWait arrête la marche locale après confirmation cluster.
    private async Task<StepOutcome> CompleteStepAsync(StepContext ctx)
    {
        var node = ctx.Node; var started = ctx.Started;
        state.Context = state.Context with { Variables = new Dictionary<string, JsonElement>(state.Variables) };
        var nextNodeId = FlowPath.NextNodeId(lease.Flow, node.Id, ctx.Output);
        if (nextNodeId is null) ctx.Wait = null;
        var placementHandoff = false;
        if (ctx.Handoff is null && nextNodeId is not null && state.PlacementPlan?.NodeAssignments?.TryGetValue(nextNodeId, out var plannedWorker) == true
            && plannedWorker != client.NodeId)
        {
            if (state.DirectRead) await EnsureFilesAsync();
            ctx.Handoff = new RuntimeHandoffSelector("node", plannedWorker);
            placementHandoff = true;
        }
        if (ctx.Handoff is not null && state.DirectRead) await EnsureFilesAsync();
        state.Context = state.Context with { Variables = new Dictionary<string, JsonElement>(state.Variables) };
        var outcome = ctx.Handoff is not null || ctx.Wait is not null
            ? StepOutcome.HandoffOrWait(ctx.Output, nextNodeId, state.Terminal, ctx.Handoff, ctx.Wait, placementHandoff)
            : StepOutcome.Continue(ctx.Output, nextNodeId, state.Terminal);
        var checkpoint = new RuntimeCheckpoint(
            node.Id, started.InvocationId, outcome.Output, outcome.NextNodeId, state.Context,
            outcome.Terminal ?? state.Terminal, outcome.Handoff, outcome.Wait, outcome.PlacementHandoff);
        var eventId = Guid.NewGuid();
        await leaseGate.WaitAsync(job.Token);
        try
        {
            var request = new CompleteRuntimeStep(lease.Epoch, eventId, state.Revision, checkpoint,
                commandCapture.ForStep(node.Id, started.InvocationId),
                node.Component.ComponentId == "ai-translate-subtitles" && node.Component.PluginId == "ai");
            for (var retry = 0; ; retry++)
            {
                try
                {
                    await client.PostAsync<object>($"executions/{lease.ExecutionId:D}/steps/complete", request, job.Token);
                    commandCapture.MarkPersisted(node.Id, started.InvocationId);
                    break;
                }
                catch (HttpRequestException exception) when ((outcome.Handoff is not null || outcome.Wait is not null) && retry < 2
                    && (exception.StatusCode is null || (int)exception.StatusCode >= 500))
                { await Task.Delay(TimeSpan.FromSeconds(1), job.Token); }
            }
            if (outcome.Kind == StepOutcomeKind.HandoffOrWait)
            {
                logger.LogInformation(outcome.Wait is not null ? "Exécution {ExecutionId} mise en attente par le bloc {Node}." : "Exécution {ExecutionId} cédée après le bloc {Node}.",
                    lease.ExecutionId, node.Id);
                job.Cancel();
                return outcome;
            }
        }
        finally { leaseGate.Release(); }
        await RenewAsync(job.Token);
        return outcome;
    }

    private async Task<StepOutcome> StartFfmpegBuilderAsync(StepContext ctx)
    {
        var node = ctx.Node; var c = ctx.Config;
        if (FfmpegBuilderPolicy.Active(state.Variables) || state.Context.Plan.Count > 0)
            throw new IOException("Une préparation non exécutée ne peut pas être écrasée.");
        await EnsureScanAsync();
        state.Variables[FfmpegBuilderPolicy.StateKey] = JsonSerializer.SerializeToElement(node.Id);
        state.Variables["plan.tool"] = JsonSerializer.SerializeToElement("ffmpeg");
        state.Variables["plan.preserveUnchangedTracks"] = JsonSerializer.SerializeToElement(MediaActions.Flag(c, "preserveUnchangedTracks", true));

        return StepOutcome.Continue();
    }

    private async Task<StepOutcome> ExecuteFfmpegBuilderAsync(StepContext ctx)
    {
        var node = ctx.Node;
        if (!FfmpegBuilderPolicy.Active(state.Variables)) throw new IOException("Aucun builder FFmpeg ouvert.");
        await EnsureScanAsync();
        var inputContext = state.Context;
        var input = state.Current;
        try
        {
            if (state.Context.Plan.Count == 0) ctx.Output = "skipped";
            else
            {
                if (state.Context.Plan.Any(step => !IsFfmpegPlan(step.ComponentId)))
                    throw new IOException("Le builder contient une opération incompatible avec FFmpeg.");
                var outcome = await actions.FfmpegPlanAsync(state.Context.Plan, state.Current, directory, node.Id, client.NodeId, job.Token,
                    !state.Variables.TryGetValue("plan.preserveUnchangedTracks", out var preserve) || preserve.GetBoolean(), knownMetadata: state.Context.CurrentMetadata);
                if (outcome.Primary is null) ctx.Output = "skipped";
                await ApplyOutcomeAsync(outcome, node.Id);
            }
        }
        catch (Exception exception) when (exception is MediaToolException or IOException)
        {
            state.Context = inputContext; state.Current = input; state.CurrentStamp = state.Materialized ? MediaFileStamp.Read(state.Current) : null;
            ctx.Output = "error";
            logger.LogWarning("Échec du builder {Node}, entrée validée conservée : {Detail}", node.Id, exception.Message);
            if (FlowPath.NextNodeId(lease.Flow, node.Id, "error") is null) throw;
        }
        state.Context = state.Context with { Plan = [] };
        FfmpegBuilderPolicy.Close(state.Variables);

        return StepOutcome.Continue();
    }

    private async Task<StepOutcome> PrepareProcessingAsync(StepContext ctx)
    {
        var c = ctx.Config;
        await EnsureScanAsync(); state.Context = state.Context with { Plan = [] };
        state.Variables["plan.tool"] = JsonSerializer.SerializeToElement(MediaActions.Text(c, "tool", "ffmpeg"));
        state.Variables["plan.preserveUnchangedTracks"] = JsonSerializer.SerializeToElement(MediaActions.Flag(c, "preserveUnchangedTracks", true));

        return StepOutcome.Continue();
    }

    private async Task<StepOutcome> DiskLockAsync(StepContext ctx)
    {
        var id = ctx.Id; var c = ctx.Config;
        var disk = DiskLockKey.Normalize(MediaActions.Text(c, "disk"));
        var request = new DiskLockRequest(lease.Epoch, disk);
        if (id == "base-disk-lock")
        {
            var taken = (await client.PostAsync<DiskLockResponse>($"executions/{lease.ExecutionId:D}/disk-locks/acquire", request, job.Token))!;
            ctx.Output = taken.Acquired ? "acquired" : "busy";
            state.Variables["disk." + disk] = JsonSerializer.SerializeToElement(new { acquired = taken.Acquired, holder = taken.HolderExecutionId });
        }
        else await client.PostAsync<object>($"executions/{lease.ExecutionId:D}/disk-locks/release", request, job.Token);

        return StepOutcome.Continue();
    }

    private async Task<StepOutcome> RunScriptAsync(StepContext ctx)
    {
        var node = ctx.Node; var id = ctx.Id; var c = ctx.Config;
        await EnsureFilesAsync();
        if (id == "base-condition") await EnsureScanAsync();
        var result = await ScriptAsync(id, c, new { state.Variables, metadata = state.Context.CurrentMetadata, sourceMetadata = state.Context.BeforeMetadata,
            currentFile = Path.GetFileName(state.Current) }, directory, job.Token);
        ctx.Output = result.TryGetProperty("outputId", out var selected) ? selected.GetString() : "success";
        if (result.TryGetProperty("variables", out var changes)) foreach (var change in changes.EnumerateObject()) state.Variables[change.Name] = change.Value.Clone();
        if (result.TryGetProperty("candidate", out var candidate)) { await EnsureScanAsync(); await ApplyOutcomeAsync(new(SafeMediaPaths.Resolve(directory, candidate.GetString()!), []), node.Id); }

        return StepOutcome.Continue();
    }

    private async Task<StepOutcome> MatchConditionsAsync(StepContext ctx)
    {
        var c = ctx.Config;
        var tree = ConditionTree.Parse(c["rules"], lease.Flow.MediaType);
        if (ConditionTree.RequiresMetadata(tree) && (state.Context.CurrentMetadata is null || state.Context.BeforeMetadata is null)) await EnsureScanAsync();
        ctx.Output = ConditionTree.Evaluate(tree, new(state.Context with { Variables = state.Variables }, lease.SourceNodeId, client.NodeId,
            lease.LibraryId, lease.MediaPath, state.SourceSha ?? "")) ? "yes" : "no";

        return StepOutcome.Continue();
    }

    private async Task<StepOutcome> CheckMediaIntegrityAsync(StepContext ctx)
    {
        var c = ctx.Config;
        await EnsureScanAsync();
        await EnsureFilesAsync();
        var mode = MediaActions.Text(c, "mode", "quick");
        var acceleration = MediaActions.Text(c, "acceleration", "auto");
        var cacheKey = "integrity." + state.Context.CurrentSha256 + "." + mode + "." + acceleration;
        MediaIntegrityReport report;
        if (state.Variables.TryGetValue(cacheKey, out var saved)
            && saved.Deserialize<MediaIntegrityReport>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) is { Output: "valid" } cached
            && await SafeMediaPaths.DigestAsync(state.Current, job.Token) == state.Context.CurrentSha256) report = cached;
        else report = await new MediaIntegrityChecker(tools, scanner).CheckAsync(state.Current, state.Context.CurrentSha256,
            lease.Flow.MediaType, mode, acceleration, await capabilities.DiscoverAsync(job.Token), job.Token);
        ctx.Output = report.Output;
        var serialized = JsonSerializer.SerializeToElement(report, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        state.Variables[MediaIntegrityPolicy.ReceiptVariable] = serialized;
        if (report.Output == "valid")
        {
            state.Variables[cacheKey] = serialized;
        }

        return StepOutcome.Continue();
    }

    private async Task<StepOutcome> ValidateOutputAsync(StepContext ctx)
    {
        var node = ctx.Node; var c = ctx.Config;
        await EnsureScanAsync();
        var report = await actions.ValidateOutputAsync(state.Current, state.Original, state.Context.BeforeMetadata!.Value, state.Context.CurrentMetadata!.Value, c, directory, job.Token);
        ctx.Output = report.GetProperty("valid").GetBoolean() ? "valid" : "invalid";
        state.Variables["validation." + node.Id] = report;
        var issues = report.GetProperty("issues").EnumerateArray().Select(issue => issue.GetString()).Where(issue => !string.IsNullOrWhiteSpace(issue)).ToArray();
        logger.LogInformation("Contrôles après assemblage {Node} : {Result}{Issues}", node.Id, ctx.Output, issues.Length == 0 ? "" : " — " + string.Join("; ", issues));

        return StepOutcome.Continue();
    }

    private async Task<StepOutcome> PrepareHandoffAsync(StepContext ctx)
    {
        var node = ctx.Node;
        ctx.Handoff = RuntimeHandoffPolicy.Resolve(node, lease.PluginConfigurations?.GetValueOrDefault("nodes"));
        if (ctx.Handoff.Mode == "node" && ctx.Handoff.TargetNodeId == client.NodeId)
            throw new IOException("Le relais doit sélectionner un autre worker.");

        return StepOutcome.Continue();
    }

    private async Task<StepOutcome> CheckLibraryAccessAsync(StepContext ctx)
    {
        var node = ctx.Node; var c = ctx.Config;
        // Ce worker est le nœud d'accès du fichier et le monte directement (accès par transfert : « non »).
        var direct = lease.SourceNodeId == client.NodeId && lease.SourceRoot is not null;
        if (direct && RuntimeHandoffPolicy.LibraryAccessNeedsEncoder(node))
            direct = Encoding.Contracts.Flows.MediaCodecPolicy.ProfileEncoderUsable(lease.Flow, (await capabilities.DiscoverAsync(job.Token)).FirstOrDefault(c => c.ToolId == "ffmpeg"));
        ctx.Output = direct ? "yes" : "no";

        return StepOutcome.Continue();
    }

    private async Task<StepOutcome> EnrichMetadataWithAiAsync(StepContext ctx)
    {
        var node = ctx.Node; var started = ctx.Started;
        await EnsureScanAsync();
        var response = await client.PostAsync<RuntimeAiMetadataEnrichmentResponse>(
            $"executions/{lease.ExecutionId:D}/ai/metadata/enrich",
            new RuntimeAiMetadataEnrichmentRequest(lease.Epoch, started.InvocationId, node.Id,
                Path.GetFileName(state.Current), lease.Flow.MediaType, state.Context.CurrentMetadata!.Value), job.Token);
        if (response is not { Success: true, Metadata: { } metadata }) ctx.Output = response?.ErrorCode ?? "error";
        else
        {
            var sidecarName = Path.GetFileNameWithoutExtension(state.Current) + ".metadata.json";
            var sidecar = Path.Combine(directory, sidecarName);
            await File.WriteAllTextAsync(sidecar, JsonSerializer.Serialize(metadata, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), job.Token);
            await ApplyOutcomeAsync(new(null, [sidecar]), node.Id);
        }

        return StepOutcome.Continue();
    }

    private async Task<StepOutcome> TranslateSubtitlesWithAiAsync(StepContext ctx)
    {
        var node = ctx.Node; var c = ctx.Config; var started = ctx.Started;
        await EnsureScanAsync();
        try
        {
            var translate = new Func<int, IReadOnlyList<RuntimeAiSubtitleItem>, Task<RuntimeAiSubtitleTranslationResponse>>(
                async (batchIndex, items) => await client.PostAsync<RuntimeAiSubtitleTranslationResponse>(
                    $"executions/{lease.ExecutionId:D}/ai/subtitles/translate",
                    new RuntimeAiSubtitleTranslationRequest(lease.Epoch, started.InvocationId, node.Id, batchIndex, items), job.Token)
                    ?? new RuntimeAiSubtitleTranslationResponse(false, ErrorCode: "error"));
            var outcome = lease.Flow.MediaType == "video"
                ? await SubtitleTranslation.TranslateEmbeddedAsync(c, state.Current, directory, node.Id, state.Context.CurrentMetadata!.Value, tools, translate, job.Token)
                : await SubtitleTranslation.TranslateAsync(c, state.Current, directory, node.Id, translate, job.Token);
            if (outcome.ErrorCode is { } aiError) ctx.Output = aiError;
            else if (outcome.File is { } translated) await ApplyOutcomeAsync(new(translated, []), node.Id);
            else ctx.Output = "unchanged";
        }
        catch (HttpRequestException exception) when ((int?)exception.StatusCode == 429)
        {
            ctx.Output = "rateLimit";
            logger.LogInformation("Le fournisseur IA a limité le bloc de traduction {Node}; le fichier source est conservé.", node.Id);
        }

        return StepOutcome.Continue();
    }

    private async Task<StepOutcome> RunExternalPluginAsync(StepContext ctx)
    {
        var node = ctx.Node; var c = ctx.Config; var started = ctx.Started;
        await EnsureScanAsync();
        var invocation = new FlowStepInvocation(1, started.InvocationId, lease.ExecutionId, lease.Attempt, client.NodeId, lease.Epoch, node.Id, node.Component, c,
            new(state.SourceSha, state.DirectRead ? null : Path.GetFileName(state.Current), state.Context.BeforeMetadata, state.Context.CurrentMetadata, state.Variables, lease.MediaPath,
                lease.LibraryId), DateTimeOffset.UtcNow.AddHours(6),
            lease.PluginConfigurations?.GetValueOrDefault(node.Component.PluginId));
        var response = await plugins.ExecuteAsync(invocation, directory, job.Token);
        ctx.Output = response.OutputId; state.Terminal = response.TerminalResult?.ToString().ToLowerInvariant();
        foreach (var change in response.VariableChanges) state.Variables[change.Key] = change.Value;
        if (response.CandidateArtifactId is { } candidate) await ApplyOutcomeAsync(new(SafeMediaPaths.Resolve(directory, candidate), []), node.Id);

        return StepOutcome.Continue();
    }
}
