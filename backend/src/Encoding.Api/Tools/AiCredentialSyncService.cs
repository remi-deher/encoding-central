using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Text.Json;
using Encoding.Contracts.Tools;

namespace Encoding.Api.Tools;

/// <summary>
/// Garde les connexions IA identiques sur tous les nœuds (Codex CLI, Claude Code, clés OpenAI et Anthropic). Chaque nœud compare sa connexion locale
/// à la dernière valeur synchronisée : un changement local (connexion, renouvellement, déconnexion) est publié ; un changement distant est
/// appliqué. Si les deux ont changé, la modification la plus récente l’emporte. Les secrets ne passent jamais par les journaux.
/// </summary>
public sealed class AiCredentialSyncService(AiCredentialStore store, AiCliRuntime runtime, AiSettingsService settings,
    ILogger<AiCredentialSyncService> logger) : BackgroundService
{
    internal sealed record Source(string Provider, Func<string?> Read, Action<string?> Write, Func<DateTimeOffset?> Modified);

    private readonly Source[] sources =
    [
        new("codex-cli", () => ReadFile(runtime.CodexAuthPath), value => WriteFile(runtime.CodexAuthPath, value), () => Modified(runtime.CodexAuthPath)),
        new("claude-cli", runtime.ReadClaudeToken, value => { if (value is null) runtime.DeleteClaudeToken(); else runtime.SaveClaudeToken(value); },
            () => Modified(Path.Combine(runtime.Profile("claude-cli"), "oauth-token.protected"))),
        new("openai", () => settings.GetSettings().OpenAiApiKey, settings.SetOpenAiApiKey, () => Modified(Path.Combine(runtime.Root, "settings.json"))),
        new("anthropic", () => settings.GetSettings().AnthropicApiKey, settings.SetAnthropicApiKey, () => Modified(Path.Combine(runtime.Root, "settings.json")))
    ];
    private readonly ConcurrentDictionary<string, AiCredentialStatus> statuses = new();

    /// <summary>État de réplication de chaque connexion sur ce nœud, sans secret ni condensat.</summary>
    public IReadOnlyList<AiCredentialStatus> Statuses() => sources
        .Select(source => statuses.TryGetValue(source.Provider, out var status) ? status : new(source.Provider, "pending", null, null)).ToArray();

    private string StatePath => Path.Combine(runtime.Root, "ai-credentials-sync.json");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        do
        {
            try { await SyncOnceAsync(stoppingToken); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Ni le message ni le contenu ne sont journalisés : ils peuvent citer un secret.
                logger.LogWarning("Synchronisation des connexions IA impossible ({Type}).", exception.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task SyncOnceAsync(CancellationToken ct)
    {
        var state = LoadState();
        foreach (var source in sources)
        {
            try { await SyncAsync(source, state, ct); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var previous = statuses.GetValueOrDefault(source.Provider);
                statuses[source.Provider] = new(source.Provider, "pending", previous?.Revision, previous?.SyncedAt);
                logger.LogWarning("Connexion IA {Provider} non synchronisée ({Type}).", source.Provider, exception.GetType().Name);
            }
        }
        SaveState(state);
    }

    private async Task SyncAsync(Source source, Dictionary<string, string?> state, CancellationToken ct)
    {
        var local = source.Read();
        if (source.Provider == "codex-cli" && local is not null && !IsJson(local)) return; // écriture de la CLI en cours
        var row = await store.GetAsync(source.Provider, ct);
        var localHash = Hash(local);
        var remoteHash = Hash(row?.Secret);
        state.TryGetValue(source.Provider, out var synced);
        if (localHash == remoteHash) { state[source.Provider] = localHash; Record(source.Provider, local, row); return; }
        var localChanged = localHash != synced;
        var remoteChanged = remoteHash != synced;
        if (localChanged && remoteChanged)
        {
            // Modification concurrente : la plus récente gagne. Une déconnexion locale date de maintenant.
            var localTime = local is null ? DateTimeOffset.UtcNow : source.Modified() ?? DateTimeOffset.MinValue;
            localChanged = row is null || localTime >= row.UpdatedAt;
            remoteChanged = !localChanged;
        }
        if (localChanged)
        {
            await store.PutAsync(source.Provider, local, ct);
            state[source.Provider] = localHash;
            Record(source.Provider, local, await store.GetAsync(source.Provider, ct), DateTimeOffset.UtcNow);
            logger.LogInformation("Connexion IA {Provider} publiée vers le cluster.", source.Provider);
        }
        else if (remoteChanged)
        {
            source.Write(row?.Secret);
            state[source.Provider] = remoteHash;
            Record(source.Provider, row?.Secret, row, DateTimeOffset.UtcNow);
            logger.LogInformation("Connexion IA {Provider} reçue du cluster.", source.Provider);
        }
    }

    private void Record(string provider, string? secret, AiCredentialRow? row, DateTimeOffset? changedAt = null)
    {
        var previous = statuses.GetValueOrDefault(provider);
        statuses[provider] = new(provider, secret is null ? "absent" : "synced", row?.Revision,
            changedAt ?? (previous is { State: "synced" } && previous.Revision == row?.Revision ? previous.SyncedAt : row?.UpdatedAt));
    }

    private static string? Hash(string? value) => value is null ? null : Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    private static bool IsJson(string text)
    {
        try { using var _ = JsonDocument.Parse(text); return true; }
        catch (JsonException) { return false; }
    }
    private static string? ReadFile(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
    private static DateTimeOffset? Modified(string path) => File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null;

    private static void WriteFile(string path, string? value)
    {
        if (value is null) { File.Delete(path); return; }
        var temporary = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temporary, options))
        using (var writer = new StreamWriter(stream)) writer.Write(value);
        File.Move(temporary, path, true);
    }

    private Dictionary<string, string?> LoadState()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(StatePath)) ?? []; }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    private void SaveState(Dictionary<string, string?> state)
    {
        // Seuls des condensats sont conservés pour détecter les changements, jamais les secrets.
        Directory.CreateDirectory(runtime.Root);
        var temporary = StatePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state));
        File.Move(temporary, StatePath, true);
    }
}
