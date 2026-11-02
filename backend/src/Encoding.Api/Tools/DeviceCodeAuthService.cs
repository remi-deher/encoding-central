using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using System.Text.RegularExpressions;
using Encoding.Contracts.Tools;

namespace Encoding.Api.Tools;

public sealed partial class DeviceCodeAuthService(AiCliRuntime runtime, ILogger<DeviceCodeAuthService>? logger = null,
    TimeProvider? timeProvider = null, AiQuotaService? quotas = null, AiModelCatalogService? models = null) : IDisposable
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ILogger<DeviceCodeAuthService> log = logger ?? NullLogger<DeviceCodeAuthService>.Instance;
    private readonly ConcurrentDictionary<string, Session> sessions = new();
    private readonly ConcurrentDictionary<string, long> connectionRevisions = new();
    private readonly ConcurrentDictionary<string, StatusCacheEntry> statusCache = new();
    private readonly SemaphoreSlim gate = new(1);

    public string ConnectionRevision(string id)
    {
        _ = AiCliRuntime.Executable(id);
        return $"{id}:{connectionRevisions.GetOrAdd(id, 0)}";
    }

    private void ConnectionChanged(string id)
    {
        connectionRevisions.AddOrUpdate(id, 1, static (_, current) => current + 1);
        if (statusCache.TryGetValue(id, out var cached))
        {
            lock (cached.Sync)
            {
                cached.Generation++;
                cached.Status = null;
            }
        }
        quotas?.Invalidate(id);
        models?.Invalidate(id);
    }

    public async Task<IReadOnlyList<AiToolInfo>> ListToolsAsync(CancellationToken ct = default)
    {
        return await Task.WhenAll(new[] { "codex-cli", "claude-cli" }.Select(async id =>
            new AiToolInfo(id, id == "codex-cli" ? "Codex CLI" : "Claude Code", "Compte CLI sur ce nœud", "CLI",
                runtime.Find(id) is not null, await GetCachedStatusAsync(id, ct), ["ai-text-generation", "subtitles-translation"])));
    }

    public async Task<DeviceAuthStatus> GetStatusAsync(string id, CancellationToken ct = default)
    {
        _ = AiCliRuntime.Executable(id);
        ct.ThrowIfCancellationRequested();
        if (sessions.TryGetValue(id, out var session))
        {
            if (session.State == "pending" && DateTimeOffset.UtcNow >= session.ExpiresAt)
            { session.State = "expired"; session.Cancellation.Cancel(); }
            if (session.FinishedAt is { } finishedAt && finishedAt.AddMinutes(5) <= clock.GetUtcNow())
                ((ICollection<KeyValuePair<string, Session>>)sessions).Remove(new(id, session));
            else
            {
                var account = await ReadCachedCliStatusAsync(id, ct);
                // The CLI can persist credentials before its interactive login process exits.
                // Treat the independently verified account state as success immediately so the
                // UI does not keep reporting a pending attempt or cancel a completed login.
                MarkAuthorizedSession(id, session, account);
                return account with { AttemptState = session.State, AttemptErrorMessage = session.Error };
            }
        }
        return await ReadCachedCliStatusAsync(id, ct);
    }

    private Task<DeviceAuthStatus> GetCachedStatusAsync(string id, CancellationToken ct)
    {
        _ = AiCliRuntime.Executable(id);
        ct.ThrowIfCancellationRequested();
        if (sessions.TryGetValue(id, out var session))
        {
            if (session.State == "pending" && DateTimeOffset.UtcNow >= session.ExpiresAt)
            { session.State = "expired"; session.Cancellation.Cancel(); }
            if (session.FinishedAt is { } finishedAt && finishedAt.AddMinutes(5) <= clock.GetUtcNow())
                ((ICollection<KeyValuePair<string, Session>>)sessions).Remove(new(id, session));
            else if (session.State == "authorized")
                return Task.FromResult(new DeviceAuthStatus(id, "authorized", AttemptState: "authorized"));
            else
            {
                if (session.State == "pending") QueuePendingStatusCheck(id, session);
                return Task.FromResult(new DeviceAuthStatus(id, session.State, ErrorMessage: session.Error));
            }
        }
        if (runtime.Find(id) is null) return Task.FromResult(new DeviceAuthStatus(id, "unconfigured", ErrorMessage: "Outil non installé."));
        var entry = statusCache.GetOrAdd(id, _ => new());
        DeviceAuthStatus? snapshot;
        var refresh = false;
        lock (entry.Sync)
        {
            snapshot = entry.Status;
            if (!entry.RefreshQueued && (snapshot is null || entry.ExpiresAt <= clock.GetUtcNow()))
            {
                entry.RefreshQueued = true;
                refresh = true;
            }
        }
        if (refresh) _ = RefreshStatusInBackground(id, entry);
        return Task.FromResult(snapshot ?? new DeviceAuthStatus(id, "checking", ErrorMessage: "Vérification de la connexion en cours."));
    }

    public void InvalidateStatus(string id)
    {
        if (!statusCache.TryGetValue(id, out var entry)) return;
        lock (entry.Sync)
        {
            entry.Generation++;
            entry.Status = null;
            entry.ExpiresAt = DateTimeOffset.MinValue;
        }
    }

    private async Task<DeviceAuthStatus> ReadCachedCliStatusAsync(string id, CancellationToken ct)
    {
        var entry = statusCache.GetOrAdd(id, _ => new());
        await entry.Gate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                int generation;
                lock (entry.Sync)
                {
                    if (entry.Status is { } cached && entry.ExpiresAt > clock.GetUtcNow()) return cached;
                    generation = entry.Generation;
                }
                var status = await CheckCliStatusAsync(id, ct);
                lock (entry.Sync)
                {
                    if (generation == entry.Generation)
                    {
                        entry.Status = status;
                        entry.ExpiresAt = clock.GetUtcNow().AddSeconds(15);
                        return status;
                    }
                }
            }
            return new(id, "failed", ErrorMessage: "Connexion modifiée pendant le contrôle. Actualisez le statut.");
        }
        finally { entry.Gate.Release(); }
    }

    private async Task RefreshStatusInBackground(string id, StatusCacheEntry entry)
    {
        try { await ReadCachedCliStatusAsync(id, CancellationToken.None); }
        catch (Exception) { }
        finally { lock (entry.Sync) entry.RefreshQueued = false; }
    }

    private void QueuePendingStatusCheck(string id, Session session)
    {
        if (Interlocked.Exchange(ref session.StatusCheckQueued, 1) != 0) return;
        _ = VerifyPendingStatusInBackground(id, session);
    }

    private async Task VerifyPendingStatusInBackground(string id, Session session)
    {
        try
        {
            var account = await ReadCachedCliStatusAsync(id, CancellationToken.None);
            MarkAuthorizedSession(id, session, account);
        }
        catch (Exception) { }
        finally { Interlocked.Exchange(ref session.StatusCheckQueued, 0); }
    }

    private void MarkAuthorizedSession(string id, Session session, DeviceAuthStatus account)
    {
        if (session.State != "pending" || account.State != "authorized") return;
        session.State = "authorized";
        session.Error = null;
        session.FinishedAt ??= clock.GetUtcNow();
        ConnectionChanged(id);
    }

    private async Task<DeviceAuthStatus> CheckCliStatusAsync(string id, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        if (runtime.Find(id) is null)
        {
            await LogStatusDiagnosticAsync(id, "cli_missing", timer.ElapsedMilliseconds);
            return new(id, "unconfigured", ErrorMessage: "Outil non installé.");
        }
        if (!runtime.CanRun)
        {
            await LogStatusDiagnosticAsync(id, "sandbox_unavailable", timer.ElapsedMilliseconds);
            return new(id, "failed", ErrorMessage: "Landlock est requis pour exécuter les outils IA sur ce nœud Linux.");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var status = await runtime.RunAsync(id, id == "codex-cli" ? ["login", "status"] : ["auth", "status"], timeout.Token);
            if (status.ExitCode != 0)
            {
                log.Log(status.ExitCode == 1 ? LogLevel.Debug : LogLevel.Warning, "AI CLI status {Provider}: exit={ExitCode}, stdoutChars={OutputChars}, stderrChars={ErrorChars}",
                    id, status.ExitCode, status.Output.Length, status.Error.Length);
                // Exit 1 is the documented unauthenticated status. Other exits indicate a runtime failure.
                await LogStatusDiagnosticAsync(id, status.ExitCode == 1 ? "auth_missing" : "status_exit", timer.ElapsedMilliseconds);
                return status.ExitCode == 1 ? new(id, "unconfigured") : new(id, "failed",
                    ErrorMessage: $"La CLI a quitté avec le code {status.ExitCode}. Consultez les journaux système de ce nœud.");
            }
            if (id == "claude-cli")
            {
                using var doc = JsonDocument.Parse(status.Output);
                if (!doc.RootElement.TryGetProperty("loggedIn", out var loggedIn) || loggedIn.ValueKind != JsonValueKind.True)
                    return new(id, "unconfigured");
            }
            log.LogDebug("AI CLI status provider={Provider}, category=authorized, version={Version}, durationMs={DurationMs}, attemptId=status",
                id, await runtime.VersionForDiagnosticsAsync(id), timer.ElapsedMilliseconds);
            return new(id, "authorized");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning("AI CLI status {Provider}: failure={Failure}", id, ex.GetType().Name);
            await LogStatusDiagnosticAsync(id, ex is OperationCanceledException ? "status_timeout" : "status_failure", timer.ElapsedMilliseconds);
            return new(id, "failed", ErrorMessage: ex is OperationCanceledException
                ? "La CLI n’a pas répondu au contrôle de connexion sous 10 secondes."
                : "Le contrôle de connexion CLI a échoué. Consultez les journaux système de ce nœud.");
        }
    }

    private async Task LogStatusDiagnosticAsync(string id, string category, long durationMs) =>
        log.LogWarning("AI CLI status provider={Provider}, category={Category}, version={Version}, durationMs={DurationMs}, attemptId=status",
            id, category, await runtime.VersionForDiagnosticsAsync(id), durationMs);

    public async Task<DeviceAuthStartResponse> StartDeviceAuthAsync(string id, CancellationToken ct)
    {
        _ = AiCliRuntime.Executable(id);
        if (runtime.Find(id) is null) throw new InvalidOperationException("La CLI n’est pas installée sur ce nœud.");
        if (!runtime.CanRun) throw new InvalidOperationException("Landlock est requis pour exécuter les outils IA sur ce nœud Linux.");
        Session session;
        await gate.WaitAsync(ct);
        try
        {
            if (!sessions.TryGetValue(id, out session!) || session.State != "pending" || session.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                await StopSessionAsync(id);
                session = new(id);
                sessions[id] = session;
                ConnectionChanged(id);
                session.Run = RunLoginAsync(session);
            }
        }
        finally { gate.Release(); }
        try { return new(id, await session.Prompt.Task.WaitAsync(TimeSpan.FromSeconds(30), clock, ct), session.Id); }
        catch (TimeoutException)
        {
            session.State = "failed";
            session.FinishedAt = clock.GetUtcNow();
            session.Error = session.HasOutput
                ? "La CLI a produit une sortie, mais aucune URL de connexion reconnue n’a été détectée sous 30 secondes. Consultez les journaux système de ce nœud."
                : "La CLI n’a produit aucune sortie sous 30 secondes. Vérifiez son accès au service d’authentification depuis ce nœud.";
            log.LogWarning("AI CLI auth {Attempt}: timeout after {ElapsedMs} ms, outputReceived={HasOutput}, urlRecognized={HasUrl}, codeRecognized={HasCode}",
                session.Id, session.Timer.ElapsedMilliseconds, session.HasOutput, session.HasUrl, session.HasCode);
            session.Cancellation.Cancel();
            throw new InvalidOperationException(session.Error);
        }
    }

    private async Task RunLoginAsync(Session session)
    {
        IReadOnlyList<string> args = session.ToolId == "codex-cli" ? ["login", "--device-auth"] : ["auth", "login"];
        log.LogInformation("AI CLI auth {Attempt}: starting {Provider} login", session.Id, session.ToolId);
        AiCliProcess? process = null;
        try
        {
            var outputLines = new AuthOutputLines();
            string? recognizedUrl = null;
            string? recognizedCode = null;
            var sync = new object();
            void ProcessLine(string line)
            {
                var clean = AnsiSanitizer.Sanitize(line);
                recognizedUrl ??= FindVerificationUrl(session.ToolId, clean);
                var code = UserCode().Match(clean);
                if (session.ToolId == "codex-cli" && code.Success) recognizedCode ??= code.Value;
                session.HasUrl |= recognizedUrl is not null;
                session.HasCode |= recognizedCode is not null;
                if (recognizedUrl is not null && (session.ToolId == "claude-cli" || recognizedCode is not null)
                    && session.Prompt.TrySetResult(new(
                        recognizedCode,
                        recognizedUrl,
                        Math.Max(1, (int)(session.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds),
                        5,
                        session.ToolId == "claude-cli")))
                    log.LogInformation("AI CLI auth {Attempt}: prompt recognized after {ElapsedMs} ms", session.Id, session.Timer.ElapsedMilliseconds);
            }
            lock (sync)
            {
                process = runtime.StartInteractive(session.ToolId, args, chunk =>
                {
                    lock (sync)
                    {
                        if (!session.HasOutput && !string.IsNullOrWhiteSpace(chunk))
                        {
                            session.HasOutput = true;
                            log.LogInformation("AI CLI auth {Attempt}: first output after {ElapsedMs} ms", session.Id, session.Timer.ElapsedMilliseconds);
                        }
                        foreach (var line in outputLines.Push(chunk)) ProcessLine(line);
                    }
                });
                session.Process = process;
            }
            if (session.ToolId == "codex-cli") process.CloseInput();
            var result = await process.WaitAsync(session.Cancellation.Token);
            lock (sync)
                if (outputLines.Flush() is { } lastLine) ProcessLine(lastLine);
            session.Process = null;
            var promptRecognized = session.Prompt.Task.IsCompletedSuccessfully;
            var verified = result.ExitCode == 0 && promptRecognized
                ? await CheckCliStatusAsync(session.ToolId, session.Cancellation.Token)
                : null;
            session.State = session.State == "authorized" || verified?.State == "authorized"
                ? "authorized"
                : "failed";
            if (session.State == "failed") session.Error = result.ExitCode != 0
                ? $"La CLI a terminé avec le code {result.ExitCode}. Consultez les journaux système de ce nœud."
                : promptRecognized
                    ? verified?.ErrorMessage ?? "La CLI n’a pas confirmé la connexion. Relancez l’association."
                    : $"La CLI a terminé avec le code {result.ExitCode} sans fournir d’invitation de connexion reconnue. Consultez les journaux système de ce nœud.";
            log.Log(session.State == "authorized" ? LogLevel.Information : LogLevel.Warning,
                "AI CLI auth {Attempt}: provider={Provider}, exit={ExitCode}, elapsedMs={ElapsedMs}, stdoutChars={OutputChars}, stderrChars={ErrorChars}, urlRecognized={HasUrl}, codeRecognized={HasCode}, inputSubmitted={InputSubmitted}",
                session.Id, session.ToolId, result.ExitCode, session.Timer.ElapsedMilliseconds, result.Output.Length, result.Error.Length, session.HasUrl, session.HasCode, session.InputSubmitted);
        }
        catch (OperationCanceledException)
        {
            if (session.State == "pending") session.State = "expired";
            log.LogInformation("AI CLI auth {Attempt}: cancelled after {ElapsedMs} ms, state={State}", session.Id, session.Timer.ElapsedMilliseconds, session.State);
        }
        catch (Exception ex)
        {
            session.State = "failed";
            session.Error = $"Le processus {session.ToolId} a échoué. Consultez les journaux système de ce nœud.";
            // No exception message or raw CLI output: either can contain credentials.
            log.LogWarning("AI CLI auth {Attempt}: failure={Failure}, elapsedMs={ElapsedMs}", session.Id, ex.GetType().Name, session.Timer.ElapsedMilliseconds);
        }
        finally
        {
            session.Process = null;
            process?.Dispose();
            session.FinishedAt ??= clock.GetUtcNow();
            ConnectionChanged(session.ToolId);
            session.Prompt.TrySetException(new InvalidOperationException(session.Error ?? "Aucun lien de connexion disponible."));
        }
    }

    public async Task SubmitAuthInputAsync(string id, string input, CancellationToken ct = default, string? attemptId = null)
    {
        if (id != "claude-cli") throw new ArgumentException("Cette CLI n’attend pas de code de retour.");
        var value = input?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 2048 || value.Any(char.IsControl))
            throw new ArgumentException("Le code de connexion Claude est invalide.");
        if (!sessions.TryGetValue(id, out var session)) throw new InvalidOperationException("Aucune connexion Claude n’est en attente.");
        if (attemptId is not null && !StringComparer.Ordinal.Equals(attemptId, session.Id))
            throw new InvalidOperationException("Cette fenêtre de connexion ne correspond plus à la tentative active.");
        await session.InputGate.WaitAsync(ct);
        try
        {
            // Prompt.Task is completed here — read the result without blocking an incomplete awaiter.
            if (session.State != "pending" || !session.Prompt.Task.IsCompletedSuccessfully)
                throw new InvalidOperationException("La connexion Claude n’attend plus de code.");
            var prompt = session.Prompt.Task.GetAwaiter().GetResult();
            if (!prompt.RequiresInput)
                throw new InvalidOperationException("La connexion Claude n’attend plus de code.");
            if (session.InputSubmitted) throw new InvalidOperationException("Un code a déjà été transmis à Claude.");
            if (session.Process is null) throw new InvalidOperationException("La session Claude n’accepte plus de code.");
            session.InputSubmitted = true;
            try { await session.Process.WriteInputLineAsync(value, ct); }
            catch
            {
                session.InputSubmitted = false;
                throw new InvalidOperationException("Impossible de transmettre le code à Claude Code.");
            }
        }
        finally { session.InputGate.Release(); }
    }

    public async Task SaveClaudeTokenAsync(string token, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (runtime.Find("claude-cli") is null) throw new InvalidOperationException("Claude Code n’est pas installé.");
            var path = Path.Combine(runtime.Profile("claude-cli"), "oauth-token.protected");
            await StopSessionAsync("claude-cli");
            ConnectionChanged("claude-cli");
            var previous = File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null;
            try
            {
                runtime.SaveClaudeToken(token);
                sessions.TryRemove("claude-cli", out _);
                InvalidateStatus("claude-cli");
                var status = await ReadCachedCliStatusAsync("claude-cli", ct);
                if (status.State != "authorized")
                {
                    log.LogWarning("AI CLI Claude: token validation failed, state={State}, runtimeFailure={RuntimeFailure}", status.State, status.ErrorMessage is not null);
                    throw new InvalidOperationException(status.ErrorMessage ?? "Le jeton n’a pas été reconnu par Claude Code.");
                }
                log.LogInformation("AI CLI Claude: token recognized by local status (no remote inference performed)");
            }
            catch
            {
                if (previous is null) runtime.DeleteClaudeToken();
                else await File.WriteAllBytesAsync(path, previous, CancellationToken.None);
                ConnectionChanged("claude-cli");
                throw;
            }
        }
        finally { gate.Release(); }
    }

    public async Task<bool> RevokeAuthAsync(string id, CancellationToken ct)
    {
        _ = AiCliRuntime.Executable(id);
        await gate.WaitAsync(ct);
        try
        {
            await StopSessionAsync(id);
            ConnectionChanged(id);
            if (id == "claude-cli") runtime.DeleteClaudeToken();
            if (runtime.Find(id) is not null)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var result = await runtime.RunAsync(id, id == "codex-cli" ? ["logout"] : ["auth", "logout"], timeout.Token);
                if (result.ExitCode != 0) throw new InvalidOperationException("La CLI n’a pas confirmé la déconnexion.");
            }
            // Legacy markers are never accepted as proof of authentication.
            File.Delete(Path.Combine(runtime.Profile(id), "credentials.json"));
            InvalidateStatus(id);
            return true;
        }
        finally { gate.Release(); }
    }

    public async Task CancelAsync(string id, string? attemptId = null)
    {
        _ = AiCliRuntime.Executable(id);
        await gate.WaitAsync();
        try
        {
            if (sessions.TryGetValue(id, out var session)
                && (attemptId is null || StringComparer.Ordinal.Equals(attemptId, session.Id)))
            {
                await StopSessionAsync(id);
                ConnectionChanged(id);
            }
        }
        finally { gate.Release(); }
    }

    private async Task StopSessionAsync(string id)
    {
        if (!sessions.TryRemove(id, out var session)) return;
        session.State = "expired";
        session.Cancellation.Cancel();
        if (session.Run is not null) await session.Run;
    }

    private static string? FindVerificationUrl(string id, string text)
    {
        var match = (id == "codex-cli" ? CodexVerificationUrl() : ClaudeVerificationUrl()).Match(text);
        if (!match.Success) return null;
        var candidate = match.Value.TrimEnd('.', ',', ';', '!', ')', ']');
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        var allowed = id == "codex-cli"
            ? uri.Host is "auth.openai.com" or "chatgpt.com"
            : uri.Host is "claude.com" or "console.anthropic.com";
        return allowed ? candidate : null;
    }
    [GeneratedRegex(@"https://(?:auth\.openai\.com|chatgpt\.com)/[a-zA-Z0-9/_-]*device[a-zA-Z0-9/_-]*")]
    private static partial Regex CodexVerificationUrl();
    [GeneratedRegex(@"https://(?:claude\.com|console\.anthropic\.com)/[^\s<>""']+")]
    private static partial Regex ClaudeVerificationUrl();
    [GeneratedRegex(@"\b[A-Z0-9]{4}-[A-Z0-9]{4,5}\b")]
    private static partial Regex UserCode();

    private sealed class AuthOutputLines
    {
        private const int MaximumLineLength = 16384;
        private readonly System.Text.StringBuilder line = new();
        private bool discardLine;
        private bool previousWasCarriageReturn;

        public IReadOnlyList<string> Push(string chunk)
        {
            var completed = new List<string>();
            foreach (var character in chunk)
            {
                if (character is '\r' or '\n')
                {
                    if (character == '\n' && previousWasCarriageReturn)
                    {
                        previousWasCarriageReturn = false;
                        continue;
                    }
                    if (!discardLine) completed.Add(line.ToString());
                    line.Clear();
                    discardLine = false;
                    previousWasCarriageReturn = character == '\r';
                    continue;
                }
                previousWasCarriageReturn = false;
                if (discardLine) continue;
                if (line.Length == MaximumLineLength)
                {
                    line.Clear();
                    discardLine = true;
                    continue;
                }
                line.Append(character);
            }
            return completed;
        }

        public string? Flush()
        {
            if (discardLine || line.Length == 0) return null;
            var result = line.ToString();
            line.Clear();
            return result;
        }
    }

    public void Dispose() { foreach (var session in sessions.Values) session.Cancellation.Cancel(); }
    private sealed class Session
    {
        public Session(string toolId) => ToolId = toolId;
        public readonly string ToolId;
        public readonly string Id = Guid.NewGuid().ToString("N");
        public readonly Stopwatch Timer = Stopwatch.StartNew();
        public volatile bool HasOutput;
        public volatile bool HasUrl;
        public volatile bool HasCode;
        public readonly DateTimeOffset ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15);
        public readonly CancellationTokenSource Cancellation = new(TimeSpan.FromMinutes(15));
        public readonly TaskCompletionSource<DeviceAuthPrompt> Prompt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly SemaphoreSlim InputGate = new(1);
        public volatile string State = "pending";
        public volatile bool InputSubmitted;
        public int StatusCheckQueued;
        public AiCliProcess? Process;
        public string? Error;
        public Task? Run;
        public DateTimeOffset? FinishedAt;
    }

    private sealed class StatusCacheEntry
    {
        public readonly SemaphoreSlim Gate = new(1);
        public readonly object Sync = new();
        public DeviceAuthStatus? Status;
        public DateTimeOffset ExpiresAt;
        public int Generation;
        public bool RefreshQueued;
    }
}
