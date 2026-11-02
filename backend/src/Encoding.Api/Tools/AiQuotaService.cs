using System.Collections.Concurrent;
using System.Text.Json;
using Encoding.Contracts.Tools;

namespace Encoding.Api.Tools;

/// <summary>Local account observations, cached for one minute. Credentials and snapshots are not replicated.</summary>
public sealed class AiQuotaService(AiCliRuntime runtime, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1);
        public readonly object Sync = new();
        public AiQuotaSnapshot? Snapshot;
        public int Generation;
    }
    private readonly ConcurrentDictionary<string, Entry> entries = new();

    public void Invalidate(string provider)
    {
        if (!entries.TryGetValue(provider, out var entry)) return;
        lock (entry.Sync) { entry.Generation++; entry.Snapshot = null; }
    }

    public async Task<AiQuotaSnapshot> ReadAsync(string provider, CancellationToken ct)
    {
        if (provider is not ("codex-cli" or "claude-cli")) throw new ArgumentException("Ce fournisseur ne propose pas de quota d’abonnement CLI.");
        var entry = entries.GetOrAdd(provider, _ => new());
        await entry.Gate.WaitAsync(ct);
        try
        {
            AiQuotaSnapshot? previous;
            int generation;
            lock (entry.Sync)
            {
                previous = entry.Snapshot;
                if (previous is not null && previous.CheckedAt.AddMinutes(1) > clock.GetUtcNow()) return Freshness(previous);
                generation = entry.Generation;
            }
            AiQuotaSnapshot snapshot;
            try
            {
                if (runtime.Find(provider) is null) throw new InvalidOperationException("CLI absente.");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                string[] args = provider == "codex-cli" ? ["app-server"]
                    : ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--no-session-persistence",
                        "--tools", "", "--strict-mcp-config", "--setting-sources", "", "--disable-slash-commands"];
                var raw = await runtime.RunProtocolAsync<JsonElement>(provider, args,
                    provider == "codex-cli" ? AiQuotaProtocol.ReadCodexAsync : AiQuotaProtocol.ReadClaudeAsync, timeout.Token);
                var windows = provider == "codex-cli" ? AiQuotaProtocol.ParseCodex(raw) : AiQuotaProtocol.ParseClaude(raw);
                var now = clock.GetUtcNow();
                snapshot = windows.Any(w => w.UsedPercent is not null)
                    ? new(provider, "available", windows, now, now, provider == "claude-cli")
                    : new(provider, "unavailable", windows, windows.Count > 0 ? now : null, now, provider == "claude-cli",
                        "Le compte ou la connexion ne fournit pas de quota d’abonnement. Les autorisations du profil peuvent être insuffisantes.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or JsonException
                or FormatException or IOException or ArgumentException or System.ComponentModel.Win32Exception
                or System.Security.Cryptography.CryptographicException)
            {
                var now = clock.GetUtcNow();
                snapshot = previous?.ObservedAt is not null
                    ? previous with { State = "stale", CheckedAt = now, Message = "Actualisation impossible. Dernière observation conservée ; elle ne garantit pas le quota actuel." }
                    : new(provider, "unavailable", [], null, now, provider == "claude-cli",
                        "Quota indisponible. Vérifiez la connexion, les autorisations du compte et la compatibilité de la CLI.");
            }
            lock (entry.Sync)
            {
                if (generation != entry.Generation)
                    return new(provider, "unavailable", [], null, clock.GetUtcNow(), provider == "claude-cli", "Connexion modifiée. Actualisez les quotas.");
                entry.Snapshot = snapshot;
            }
            return Freshness(snapshot);
        }
        finally { entry.Gate.Release(); }
    }

    private AiQuotaSnapshot Freshness(AiQuotaSnapshot snapshot) => snapshot.State == "available"
        && (snapshot.ObservedAt?.AddMinutes(2) < clock.GetUtcNow() || snapshot.Windows.Any(w => w.ResetsAt <= clock.GetUtcNow()))
        ? snapshot with { State = "stale", Message = "Une fenêtre a expiré ou cette observation est ancienne. Actualisez avant de vous fier au quota." }
        : snapshot;
}
