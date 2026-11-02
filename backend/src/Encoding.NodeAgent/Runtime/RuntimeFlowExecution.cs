using System.Text.Json;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Flows.Conditions;
using Encoding.NodeAgent.Runtime.Media;

namespace Encoding.NodeAgent.Runtime;

/// <summary>
/// Exécute un flow pour un bail donné : renouvellement du bail, préparation de la source, enchaînement des blocs,
/// puis clôture. L'état d'une exécution vit dans cette instance, jamais dans le singleton <see cref="RuntimeFlowRunner"/>.
/// </summary>
internal sealed partial class RuntimeFlowExecution(NodeAgentOptions options, MediaScanner scanner, MediaActions actions, ProcessToolRunner tools,
    PluginProcessHost plugins, PublicationGuardRunner guards, Encoding.NodeAgent.Services.IToolCapabilityProbe capabilities,
    LocalPublicationArtifactStore localPublicationArtifacts, ILogger logger, RuntimeLease lease, RuntimeClient client,
    CancellationToken stoppingToken) : IDisposable
{
    private readonly RuntimeCommandCapture commandCapture = RuntimeCommandCapture.Begin(lease.ExecutionId);
    private readonly CancellationTokenSource job = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
    private readonly SemaphoreSlim leaseGate = new(1, 1);
    private readonly string directory = Path.Combine(options.DataDirectory, "executions", lease.ExecutionId.ToString("D"), lease.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private readonly FlowExecutionState state = new(lease.Revision);

    public void Dispose()
    {
        job.Dispose();
        leaseGate.Dispose();
        commandCapture.Dispose();
    }

    public async Task RunAsync()
    {
        Directory.CreateDirectory(directory);
        if (lease.Probe is { } wanted) { await ProbeAsync(lease, wanted, client, directory, stoppingToken); return; }
        var renewal = StartRenewal();
        try
        {
            await InitializeAsync();
            var steps = await RunStepsAsync();
            ApplyStepOutcome(steps);
            if (steps.StopsLocalRun) return;
            await FinishAsync();
        }
        catch (OperationCanceledException) when (job.IsCancellationRequested)
        {
            if (state.IsCancelled && !state.IsLost) await FailureAsync("cancelled", "Annulation confirmée après arrêt des processus.");
        }
        catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // Un 409 peut signaler un checkpoint ou une complétion refusé par le contrat alors que le bail
            // est toujours valide. Traiter ce cas comme une perte de bail ferait reboucler indéfiniment
            // les médias invalides. FailureAsync renouvelle d'abord le bail et confirme un échec terminal ;
            // si le bail a réellement changé d'époque, le renouvellement sera refusé et l'ancien worker
            // ne pourra rien modifier.
            logger.LogWarning(exception, "Complétion de l’exécution {ExecutionId} refusée par le cluster ; tentative de clôture en échec.", lease.ExecutionId);
            state.MarkLost();
            await FailureAsync("failed", "cluster_refused : Le cluster a refusé une étape ou une complétion de l’exécution.");
        }
        catch (HttpRequestException exception) when (exception.StatusCode is null || (int)exception.StatusCode >= 500)
        {
            // Un 409 peut aussi être un checkpoint refusé alors que le bail est intact : rejouer ne le rendrait pas valide.
            var refused = false;
            if (exception.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                try
                {
                    using var probe = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await client.PostAsync<RuntimeLeaseStatus>($"executions/{lease.ExecutionId:D}/renew", new RuntimeFence(lease.Epoch), probe.Token);
                    refused = true;
                }
                catch (Exception probeException) { logger.LogDebug(probeException, "Sonde de renouvellement après conflit indisponible pour {ExecutionId}.", lease.ExecutionId); }
            }
            if (refused)
            {
                logger.LogWarning("Exécution {ExecutionId} : le serveur a refusé une étape alors que le bail est valide ; échec.", lease.ExecutionId);
                await FailureAsync("failed", "cluster_refused : Le serveur a refusé le résultat d’une étape ; l’exécution est en échec.");
            }
            else
            {
                state.MarkLost();
                logger.LogWarning(exception, "Exécution {ExecutionId} interrompue pendant un échange HTTP ({StatusCode}) ; reprise après expiration du bail.", lease.ExecutionId, exception.StatusCode);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning("Exécution {ExecutionId} interrompue : {Type}", lease.ExecutionId, exception.GetType().Name);
            if (!state.IsLost)
            {
                var code = exception.Message.StartsWith("placement_impossible:", StringComparison.Ordinal) ? "placement_impossible" : exception switch
                {
                    LibraryAccessException or UnauthorizedAccessException => "permission_denied",
                    MediaToolException => "media_tool_failed",
                    OperationCanceledException or TimeoutException => "operation_timeout",
                    _ => lease.Flow.Nodes.FirstOrDefault(node => node.Id == state.ActiveBlock)?.Component.PluginId
                        is "plex" or "sonarr" or "radarr" or "jellyfin" ? "integration_infrastructure_failed" : "execution_infrastructure_failed"
                };
                var detail = code == "placement_impossible" || exception is MediaToolException or FlowArtifactReferenceException or LibraryAccessException
                    ? exception.Message : "Vérifiez les outils, accès et intégrations de cette étape.";
                await FailureAsync("failed", $"{code} : bloc {state.ActiveBlock ?? "préparation"}. "
                    + (state.PublicationConfirmed ? "Publication média confirmée ; échec d’une étape ultérieure. " : "Aucune nouvelle confirmation de publication obtenue. ") + detail);
            }
        }
        finally { job.Cancel(); await renewal; }
    }

    private Task StartRenewal()
    {
        return Task.Run(async () =>
        {
            var consecutiveFailures = 0;
            try
            {
                while (!job.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), job.Token);
                    try
                    {
                        await RenewAsync(job.Token);
                        consecutiveFailures = 0;
                    }
                    catch (OperationCanceledException) when (job.IsCancellationRequested) { break; }
                    catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
                    {
                        logger.LogWarning("Bail de l’exécution {ExecutionId} révoqué par le cluster (HTTP 409).", lease.ExecutionId);
                        state.MarkLost();
                        job.Cancel();
                        break;
                    }
                    catch (Exception exception)
                    {
                        consecutiveFailures++;
                        logger.LogWarning("Renouvellement du bail de l’exécution {ExecutionId} en échec ({Failures} consécutifs) : {Type} {Message}.",
                            lease.ExecutionId, consecutiveFailures, exception.GetType().Name, exception.Message);
                        if (consecutiveFailures >= 5)
                        {
                            logger.LogError("Bail de l’exécution {ExecutionId} considéré comme perdu après 5 échecs consécutifs.", lease.ExecutionId);
                            state.MarkLost();
                            job.Cancel();
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (job.IsCancellationRequested) { }
        }, CancellationToken.None);
    }

    private async Task InitializeAsync()
    {
        state.SourceSha = lease.SourceSha256;
        state.DirectRead = lease.DirectRead && lease.SourceNodeId == client.NodeId && lease.SourceRoot is not null;
        while (state.SourceSha is null && !state.DirectRead)
        {
            var source = await client.GetAsync<RuntimeSourceState>($"executions/{lease.ExecutionId:D}/source?epoch={lease.Epoch}", job.Token);
            state.SourceSha = source.SourceSha256; if (state.SourceSha is null) await Task.Delay(TimeSpan.FromSeconds(2), job.Token);
        }
        state.Extension = Path.GetExtension(lease.MediaPath); if (string.IsNullOrWhiteSpace(state.Extension)) state.Extension = ".mkv";
        state.Original = state.DirectRead ? SafeMediaPaths.Resolve(lease.SourceRoot!, lease.MediaPath) : Path.Combine(directory, "source" + state.Extension);
        if (lease.VerifyPublicationAccess && lease.SourceNodeId == client.NodeId && lease.SourceRoot is not null)
            await LibraryAccessVerifier.VerifyPublicationAsync(lease.SourceRoot, job.Token, lease.VerifyReplacementAccess ? lease.MediaPath : null);
        state.Observed = state.DirectRead ? MediaFileStamp.Read(state.Original) : null;
        state.Context = lease.Checkpoint?.Context ?? new(state.SourceSha ?? "", state.Extension, new Dictionary<string, JsonElement>(), null, null, [],
            DirectSource: state.Observed is null ? null : new(state.Observed.Bytes, state.Observed.LastWriteUtcTicks));
        state.PlacementPlan = lease.PlacementPlan;
        if (state.Context.DirectSource is { } prior)
        {
            if (state.DirectRead) new MediaFileStamp(prior.Bytes, prior.LastWriteUtcTicks).Verify(state.Original);
            else state.Context = state.Context with { CurrentSha256 = state.SourceSha!, DirectSource = null };
        }
        state.Variables = new Dictionary<string, JsonElement>(state.Context.Variables);
        state.PublicationConfirmed = state.Variables.ContainsKey("runtime.published");
        state.Next = lease.Checkpoint?.NextNodeId;
        if (lease.Checkpoint is null) state.Next = lease.EntryNodeId;
        state.Terminal = lease.Checkpoint?.TerminalResult; state.LastOutput = lease.Checkpoint?.OutputId;
        state.Current = state.DirectRead ? state.Original : Path.Combine(directory, "current" + state.Context.Extension);
        state.Materialized = false;
        state.CurrentStamp = null;
        if (state.DirectRead && state.Context.BeforeMetadata is null)
        {
            // Source lisible en place : le sondage ne coûte aucune copie, on publie donc les métadonnées d'origine tout de suite.
            try
            {
                state.Observed!.Verify(state.Original);
                state.Context = state.Context with { BeforeMetadata = await scanner.ScanAsync(state.Original, job.Token) };
                state.Observed.Verify(state.Original);
            }
            catch (Exception) when (!job.IsCancellationRequested) { state.Context = state.Context with { BeforeMetadata = null }; }
            await ReportSourceMetadataAsync();
        }
        if (state.PlacementPlan is not null)
        {
            state.PlacementPlan = await client.PostAsync<FlowPlacementPlan>($"executions/{lease.ExecutionId:D}/placement-plan", new RuntimeFence(lease.Epoch), job.Token) ?? state.PlacementPlan;
            if (state.PlacementPlan.Strategy == FlowPlacementPlan.StrategyImpossible)
                throw new IOException("placement_impossible: " + state.PlacementPlan.Reason);
        }
    }

    private async Task<RuntimeLeaseStatus> RenewAsync(CancellationToken ct)
    {
        await leaseGate.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var status = (await client.PostAsync<RuntimeLeaseStatus>($"executions/{lease.ExecutionId:D}/renew", new RuntimeFence(lease.Epoch, commandCapture.ForHeartbeat()), timeout.Token))!;
            state.ApplyLeaseRenewal(status.Revision, status.CancelRequested, job.Cancel);
            return status;
        }
        finally { leaseGate.Release(); }
    }
    private async Task EnsureFilesAsync()
    {
        if (state.Materialized) return;
        if (state.DirectRead)
        {
            state.Observed!.Verify(state.Original);
            await client.PostAsync<object>($"executions/{lease.ExecutionId:D}/source/prepare", new RuntimeFence(lease.Epoch), job.Token);
            while (state.SourceSha is null)
            {
                state.SourceSha = (await client.GetAsync<RuntimeSourceState>($"executions/{lease.ExecutionId:D}/source?epoch={lease.Epoch}", job.Token)).SourceSha256;
                if (state.SourceSha is null) await Task.Delay(TimeSpan.FromMilliseconds(250), job.Token);
            }
            state.Observed.Verify(state.Original);
            state.DirectRead = false;
            state.Original = Path.Combine(directory, "source" + state.Extension);
            state.Current = Path.Combine(directory, "current" + state.Context.Extension);
            state.Context = state.Context with { CurrentSha256 = state.SourceSha, DirectSource = null };
        }
        await client.DownloadAsync(lease.ExecutionId, lease.Epoch, state.SourceSha!, state.Original, job.Token);
        if (state.Context.CurrentSha256 == state.SourceSha)
        {
            if (state.Original != state.Current && !File.Exists(state.Current))
            {
                File.Copy(state.Original, state.Current, true);
            }
        }
        else await client.DownloadAsync(lease.ExecutionId, lease.Epoch, state.Context.CurrentSha256, state.Current, job.Token);
        foreach (var variable in state.Variables.Where(pair => pair.Key.StartsWith("artifact.", StringComparison.Ordinal)))
        {
            var sha = variable.Value.GetProperty("sha256").GetString()!;
            var name = variable.Value.GetProperty("name").GetString()!;
            var artifactDirectory = Path.Combine(directory, "artifacts", sha); Directory.CreateDirectory(artifactDirectory);
            await client.DownloadAsync(lease.ExecutionId, lease.Epoch, sha, SafeMediaPaths.Resolve(artifactDirectory, Path.GetFileName(name)), job.Token);
        }
        state.Materialized = true;
        state.CurrentStamp = MediaFileStamp.Read(state.Current);
    }
    // Les métadonnées d'origine sont visibles dans l'activité dès le sondage, sans attendre la fin d'une étape.
    private async Task ReportSourceMetadataAsync()
    {
        if (state.Context.BeforeMetadata is not { } metadata) return;
        try
        {
            state.PlacementPlan = await client.PostAsync<FlowPlacementPlan>(
                $"executions/{lease.ExecutionId:D}/source-metadata", new ReportSourceMetadata(lease.Epoch, metadata), job.Token) ?? state.PlacementPlan;
        }
        catch (Exception) when (!job.IsCancellationRequested) { }
        if (state.PlacementPlan?.Strategy == FlowPlacementPlan.StrategyImpossible)
            throw new IOException("placement_impossible: " + state.PlacementPlan.Reason);
    }
    private bool AudioAlreadyCompliant(IReadOnlyDictionary<string, System.Text.Json.JsonElement> configuration)
    {
        if (state.Context.BeforeMetadata is not { } metadata || state.Context.Plan.Count > 0 || FfmpegBuilderPolicy.Active(state.Variables)) return false;
        try { return AudioRulePlanner.Plan(metadata, AudioRuleOptions.From(configuration)).Steps("probe", AudioRuleOptions.From(configuration)).Count == 0; }
        catch (AudioRuleException) { return false; }
    }
    // Nommage et sous-titres : planification à blanc sur la source directe ; aucun téléchargement si rien ne change.
    private async Task<bool> SourceAlreadyConformsAsync(string nodeId, string id, IReadOnlyDictionary<string, System.Text.Json.JsonElement> configuration)
    {
        var naming = id is "base-track-names" or "subtitles-track-names" || FlowOperationConfiguration.IsNamingOperation(id);
        var subtitles = id is "base-subtitle-manage" or "subtitles-manage" || FlowOperationConfiguration.IsSubtitleOperation(id);
        if (!state.DirectRead || !(naming || subtitles) || state.Context.BeforeMetadata is not { } metadata
            || state.Context.Plan.Count > 0 || FfmpegBuilderPolicy.Active(state.Variables)) return false;
        if (naming && MediaActions.Text(configuration, "mode", "apply") == "snapshot") return false;
        try
        {
            state.Observed!.Verify(state.Original);
            var outcome = naming
                ? await actions.TrackNamesAsync(configuration, metadata,
                    state.Variables.TryGetValue("tracks.origin", out var saved) ? saved : null,
                    state.Original, directory, nodeId, job.Token, planOnly: true)
                : await actions.SubtitleManageAsync(configuration, metadata, state.Original, directory, nodeId, job.Token, planOnly: true);
            state.Observed.Verify(state.Original);
            if (outcome.NeedsChanges || outcome.Primary is not null) return false;
            state.ConformingNodes.Add(nodeId); return true;
        }
        catch (Exception) when (!job.IsCancellationRequested) { return false; }
    }
    private async Task EnsureScanAsync()
    {
        if (state.DirectRead) state.Observed!.Verify(state.Original);
        else await EnsureFilesAsync();
        if (state.Context.BeforeMetadata is null)
        {
            state.Context = state.Context with { BeforeMetadata = await scanner.ScanAsync(state.Original, job.Token) };
            await ReportSourceMetadataAsync();
        }
        if (state.Context.CurrentMetadata is null) state.Context = state.Context with { CurrentMetadata = state.Current == state.Original
            || state.Context.CurrentSha256 == state.SourceSha ? state.Context.BeforeMetadata : await scanner.ScanAsync(state.Current, job.Token) };
        if (state.DirectRead) state.Observed!.Verify(state.Original);
    }
    private async Task<string> ExtraAsync(string path)
    {
        if (FlowArtifactReference.TryParse(path, out var reference))
        {
            await EnsureFilesAsync();
            var artifact = reference.Resolve(state.Variables);
            var sha = artifact.GetProperty("sha256").GetString()!;
            var folder = Path.Combine(directory, "artifacts", sha); Directory.CreateDirectory(folder);
            var target = SafeMediaPaths.Resolve(folder, Path.GetFileName(artifact.GetProperty("name").GetString()!));
            if (!File.Exists(target)) await client.DownloadAsync(lease.ExecutionId, lease.Epoch, sha, target, job.Token);
            return target;
        }
        if (path.StartsWith(FlowArtifactReference.Prefix, StringComparison.Ordinal))
            throw new FlowArtifactReferenceException("Référence de fichier produit invalide.");
        path = path.Replace('\\', '/');
        RuntimeSourceState sourceState;
        while (true)
        {
            sourceState = await client.GetAsync<RuntimeSourceState>($"executions/{lease.ExecutionId:D}/source?epoch={lease.Epoch}", job.Token);
            if (sourceState.Inputs?.TryGetValue(path, out _) == true) break;
            await Task.Delay(TimeSpan.FromSeconds(2), job.Token);
        }
        var file = Path.Combine(directory, "input-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path)))[..16] + Path.GetExtension(path));
        await client.DownloadAsync(lease.ExecutionId, lease.Epoch, sourceState.Inputs![path], file, job.Token); return file;
    }
    private async Task EnsurePublicationIntegrityAsync()
    {
        await EnsureFilesAsync();
        if (await SafeMediaPaths.DigestAsync(state.Current, job.Token) != state.Context.CurrentSha256)
            throw new IOException("Le résultat a changé après son contrôle ; publication refusée.");
        if (!MediaIntegrityPolicy.IsExplicit(lease.Flow))
        {
            await scanner.ValidateAsync(state.Current, lease.Flow.MediaType, state.Context.BeforeMetadata, job.Token);
            return;
        }
        if (!state.Variables.TryGetValue(MediaIntegrityPolicy.ReceiptVariable, out var value)
            || value.Deserialize<MediaIntegrityReport>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) is not { Output: "valid" } receipt
            || receipt.Sha256 != state.Context.CurrentSha256)
            throw new IOException("Contrôle média explicite manquant ou invalide pour le résultat courant.");
        await scanner.ValidateAsync(state.Current, lease.Flow.MediaType, state.Context.BeforeMetadata, job.Token);
    }
    private async Task ApplyOutcomeAsync(MediaActionOutcome outcome, string node)
    {
        var artifactIndex = 0;
        foreach (var additional in outcome.Additional)
        {
            var sha = await SafeMediaPaths.DigestAsync(additional, job.Token);
            await client.PutAsync(lease.ExecutionId, lease.Epoch, additional, sha, false, job.Token);
            var description = JsonSerializer.SerializeToElement(new { sha256 = sha, name = Path.GetFileName(additional) });
            state.Variables["artifact." + node + "." + Path.GetFileName(additional)] = description;
            state.Variables[new FlowArtifactReference(node, artifactIndex++).VariableKey] = description;
        }
        if (outcome.Primary is { } candidate)
        {
            var metadata = await scanner.ValidateAsync(candidate, lease.Flow.MediaType, state.Context.BeforeMetadata, job.Token);
            var sha = await SafeMediaPaths.DigestAsync(candidate, job.Token);
            await client.PutAsync(lease.ExecutionId, lease.Epoch, candidate, sha, false, job.Token);
            state.Current = candidate; state.Context = state.Context with { CurrentSha256 = sha, Extension = Path.GetExtension(candidate), CurrentMetadata = metadata };
            state.CurrentStamp = MediaFileStamp.Read(state.Current);
        }
        if (outcome.VideoEncodings is { Count: > 0 })
            state.Context = state.Context with { VideoEncodings = [.. (state.Context.VideoEncodings ?? []), .. outcome.VideoEncodings] };
    }
    private async Task FailureAsync(string state, string detail)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var status = await client.PostAsync<RuntimeLeaseStatus>($"executions/{lease.ExecutionId:D}/renew", new RuntimeFence(lease.Epoch), timeout.Token);
            await client.PostAsync<object>($"executions/{lease.ExecutionId:D}/finish", new FinishRuntimeExecution(lease.Epoch, Guid.NewGuid(), status!.Revision, state, detail, null, null,
                Commands: commandCapture.Pending()), timeout.Token);
        }
        catch (Exception) { logger.LogWarning("Le résultat de l’exécution {ExecutionId} n’a pas pu être confirmé par le cluster.", lease.ExecutionId); }
    }

    private async Task FinishAsync()
    {
        await EnsureScanAsync(); state.Context = state.Context with { Variables = state.Variables };
        if (state.DirectRead) state.Observed!.Verify(state.Original);
        var failed = ExecutionOutcomes.IsFailure(state.Terminal, state.LastOutput);
        if (state.Context.Plan.Count > 0 && !failed) throw new IOException("Le flow se termine avec un plan non exécuté.");

        // Après une publication par bloc (ADR 0003), la référence est le fichier publié : la fin du flow ne republie pas.
        var publishedSha = state.Variables.TryGetValue("runtime.published", out var publication) && publication.TryGetProperty("sha256", out var publishedDigest) ? publishedDigest.GetString() : null;
        var changed = !failed && state.Terminal is not (ExecutionOutcomes.Unchanged or ExecutionOutcomes.Deferred) && state.Context.CurrentSha256 != (publishedSha ?? state.SourceSha ?? "");
        if (changed && publishedSha is not null) throw new IOException("Le média a été modifié après sa publication ; ce résultat ne peut pas être publié une seconde fois.");
        if (changed && state.Variables.ContainsKey("runtime.inPlaceEdited"))
            throw new IOException("La source a été modifiée sur place ; un résultat publié ensuite ne correspondrait plus à sa version d’origine.");
        if (changed) await EnsurePublicationIntegrityAsync();
        if (changed && lease.SourceNodeId == client.NodeId && lease.SourceRoot is not null)
            localPublicationArtifacts.Register(lease.ExecutionId, lease.Epoch, state.Context.CurrentSha256, state.Current);
        await client.PostAsync<object>($"executions/{lease.ExecutionId:D}/finish", new FinishRuntimeExecution(lease.Epoch, Guid.NewGuid(),
            state.Revision, failed ? ExecutionStates.Failed : state.Terminal == ExecutionOutcomes.Deferred ? ExecutionStates.Deferred : ExecutionStates.Succeeded,
            failed ? "Le flow a atteint une fin en échec." : "Flow terminé ; scans média réalisés.", changed ? state.Context.CurrentSha256 : null,
                changed ? state.Context.Extension : null, state.Context.BeforeMetadata, state.Context.CurrentMetadata,
                state.Terminal == ExecutionOutcomes.Deferred ? (state.Variables.TryGetValue("runtime.deferMinutes", out var delay) ? delay.GetInt32() : 2) : null,
            commandCapture.Pending()), job.Token);
        if (changed)
        {
            while (true)
            {
                var status = await client.ExecutionAsync(lease.ExecutionId, job.Token); if (ExecutionStates.IsTerminal(status.State)) break;
                await Task.Delay(TimeSpan.FromSeconds(2), job.Token);
            }
        }
    }


    /// <summary>Évalue la condition de plugin d'une attente avec le contexte figé du checkpoint ; aucune étape n'est créée.</summary>
    private async Task ProbeAsync(RuntimeLease lease, RuntimeWaitProbe probe, RuntimeClient client, string directory, CancellationToken ct)
    {
        string? output = null;
        try
        {
            var node = lease.Flow.Nodes.Single(item => item.Id == probe.BlockNodeId);
            var context = lease.Checkpoint?.Context ?? throw new InvalidOperationException("Sonde sans checkpoint.");
            var invocation = new FlowStepInvocation(1, Guid.NewGuid(), lease.ExecutionId, lease.Attempt, client.NodeId, lease.Epoch, node.Id, node.Component, node.Configuration,
                new(lease.SourceSha256, "current" + context.Extension, context.BeforeMetadata, context.CurrentMetadata, new Dictionary<string,
                    JsonElement>(context.Variables), lease.MediaPath, lease.LibraryId),
                DateTimeOffset.UtcNow.AddSeconds(45), lease.PluginConfigurations?.GetValueOrDefault(node.Component.PluginId));
            var response = await plugins.ExecuteAsync(invocation, directory, ct);
            output = response.OutputId;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception exception) { logger.LogInformation("Sonde d'attente {ExecutionId} sans résultat : {Type}.", lease.ExecutionId, exception.GetType().Name); }
        try { await client.PostAsync<object>($"executions/{lease.ExecutionId:D}/wait/probe", new ReportWaitProbe(lease.Epoch, output), ct); }
        catch (HttpRequestException exception) { logger.LogInformation("Résultat de sonde non confirmé ({Status}) ; le bail expirera.", exception.StatusCode); }
    }

    /// <summary>Dernière ligne non vide de la sortie d'erreur d'un script (secrets masqués, 300 caractères au plus), pour que l'échec dise pourquoi.</summary>
    private static string LastErrorLine(string error)
    {
        var line = error.Split('\n').Select(item => item.Trim()).LastOrDefault(item => item.Length > 0 && !item.StartsWith("[tools]", StringComparison.Ordinal)) ?? "";
        line = RuntimeCommandCapture.Redact(line);
        return line.Length > 300 ? line[..300] + "…" : line;
    }

    private static bool IsFfmpegPlan(string id) => FlowMediaValues.IsFfmpegPlan(id);
    private async Task<JsonElement> ScriptAsync(string id, IReadOnlyDictionary<string, JsonElement> config, object context, string directory, CancellationToken ct)
    {
        var python = id == "base-script" && MediaActions.Text(config, "language") == "python";
        var code = MediaActions.Text(config, "code");
        if (id == "base-condition")
        {
            var expression = "(function(){ const context=JSON.parse("
                + JsonSerializer.Serialize(JsonSerializer.Serialize(context))
                + "); const variables=context.variables; return (" + MediaActions.Text(config, "expression") + "); })()";
            code = "const vm=require('vm'); return {outputId: vm.runInNewContext("
                + JsonSerializer.Serialize(expression)
                + ",Object.create(null),{timeout:5000,contextCodeGeneration:{strings:false,wasm:false}})?'yes':'no'};";
        }
        var file = Path.Combine(directory, "script-" + Guid.NewGuid().ToString("N") + (python ? ".py" : ".js"));
        var body = ScriptTools.Compose(python, id == "base-script" && MediaActions.Flag(config, "useTools"), code);
        await File.WriteAllTextAsync(file, body, ct);
        try
        {
            var executable = ProcessToolRunner.Find(python ? "python3" : "node") ?? throw new IOException("Interpréteur indisponible.");
            var result = await tools.RunAsync(executable, [file], directory,
                TimeSpan.FromSeconds(id == "base-condition" ? 30 : int.Parse(MediaActions.Number(config, "timeoutSeconds", 60))), ct,
                JsonSerializer.Serialize(context), 1048576, true);
            if (result.ExitCode == 79) throw new IOException("Isolation du processus indisponible.");
            if (result.ExitCode != 0) throw new MediaToolException(python ? "python" : "javascript", result.ExitCode, LastErrorLine(result.Error));
            using var document = JsonDocument.Parse(result.Output); return document.RootElement.Clone();
        }
        finally { File.Delete(file); }
    }
}
