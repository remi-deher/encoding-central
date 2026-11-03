using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Contracts.Workers;
using Encoding.Core.Workers;

namespace Encoding.WorkerAgent.Services;

public sealed record WorkerIdentityState(
    Guid Id,
    string? CentralId,
    string? CentralName,
    string? ApiBaseUrl,
    string? WorkerToken,
    bool Enrolled,
    string? PairingKey = null,
    DateTimeOffset? PairingExpiresAt = null,
    bool PairingPublished = false,
    string? EnrollmentFailure = null,
    string? DisplayName = null,
    IReadOnlyList<string>? KnownServerUrls = null)
{
    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(CentralId)
        && Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out _)
        && !string.IsNullOrWhiteSpace(WorkerToken);

    public bool IsEnrolled => Enrolled
        && HasCredentials;

    public bool IsPairingPending => !Enrolled && HasCredentials && PairingPublished && !string.IsNullOrWhiteSpace(PairingKey);

    public IReadOnlyList<string> GetCandidateServerUrls(WorkerAgentOptions options)
    {
        var urls = new List<string>();
        void Add(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return;
            var normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";
            if (!urls.Contains(normalized, StringComparer.OrdinalIgnoreCase)) urls.Add(normalized);
        }
        if (IsEnrolled && options.Roles.Contains(NodeRole.Server) && options.CentralApiBaseUrl is { } localUrl && LocalEnrollment.IsLoopback(localUrl))
            Add(localUrl);
        Add(ApiBaseUrl);
        if (IsEnrolled && KnownServerUrls is not null)
        {
            foreach (var url in KnownServerUrls) Add(url);
        }
        return urls;
    }
}

public sealed class WorkerIdentityStore(WorkerAgentOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string filePath = Path.Combine(options.DataDirectory, "node-identity.json");
    private WorkerIdentityState? state;

    public Guid? CurrentId => state?.Id;
    public string? CurrentDisplayName => state?.DisplayName ?? options.Name;

    public async Task<WorkerIdentityState> GetAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (state is not null) return state;
            Directory.CreateDirectory(options.DataDirectory);
            if (File.Exists(filePath))
            {
                state = await ReadFileAsync(cancellationToken);
            }
            state ??= new WorkerIdentityState(Guid.NewGuid(), null, null, null, null, false);
            if (!File.Exists(filePath)) await SaveAsync(state, cancellationToken);
            return state;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<WorkerIdentityState> SaveEnrollmentAsync(
        string clusterId,
        string clusterName,
        string apiBaseUrl,
        CancellationToken cancellationToken,
        string? displayName = null)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(options.DataDirectory);
            if (state is null && File.Exists(filePath))
            {
                state = await ReadFileAsync(cancellationToken);
            }
            var token = state?.WorkerToken ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var effectiveDisplayName = displayName ?? state?.DisplayName;
            state = new WorkerIdentityState(state?.Id ?? Guid.NewGuid(), clusterId, clusterName, apiBaseUrl, token, false,
                DisplayName: effectiveDisplayName);
            await SaveAsync(state, cancellationToken);
            return state;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<WorkerIdentityState> SavePairingAsync(
        string clusterId,
        string clusterName,
        string apiBaseUrl,
        CancellationToken cancellationToken,
        string? displayName = null)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(options.DataDirectory);
            if (state is null && File.Exists(filePath))
            {
                state = await ReadFileAsync(cancellationToken);
            }
            if (state?.IsEnrolled == true)
                throw new InvalidOperationException("Ce worker est déjà rattaché à un central.");

            var token = state?.WorkerToken ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var canReuseKey = state?.CentralId == clusterId
                && state.PairingExpiresAt > DateTimeOffset.UtcNow
                && !string.IsNullOrWhiteSpace(state.PairingKey);
            var pairingKey = canReuseKey ? state!.PairingKey : Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
            var effectiveDisplayName = displayName ?? state?.DisplayName;
            state = new WorkerIdentityState(state?.Id ?? Guid.NewGuid(), clusterId, clusterName, apiBaseUrl, token, false, pairingKey, expiresAt,
                DisplayName: effectiveDisplayName);
            await SaveAsync(state, cancellationToken);
            return state;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task MarkPairingPublishedAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (state is null || string.IsNullOrWhiteSpace(state.PairingKey))
                throw new InvalidOperationException("La clé de jumelage locale est absente.");
            state = state with { PairingPublished = true };
            await SaveAsync(state, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Oublie le rattachement actuel et repart d'une identité neuve : un worker retiré ne se réactive jamais (0017), il revient sous un nouvel identifiant.</summary>
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(options.DataDirectory);
            var current = await LoadAsync(cancellationToken);
            state = new WorkerIdentityState(Guid.NewGuid(), null, null, null, null, false, DisplayName: current.DisplayName);
            await SaveAsync(state, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Abandonne un rattachement qui n'a pas abouti (refus, central injoignable, clé expirée) : l'identité en attente est effacée pour que le worker
    /// ne reste pas bloqué sur un central qui ne l'a jamais accepté. L'identifiant du worker est conservé : le central ne l'a jamais enregistré.
    /// </summary>
    public async Task<bool> AbandonPendingAsync(string reason, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await LoadAsync(cancellationToken);
            if (current.IsEnrolled || !current.HasCredentials) return false;
            state = new WorkerIdentityState(current.Id, null, null, null, null, false, EnrollmentFailure: reason, DisplayName: current.DisplayName);
            await SaveAsync(state, cancellationToken);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Lit le fichier d'identité. Un fichier écrit avant le vocabulaire central/worker (nodeToken, clusterId, clusterName…) est relu
    /// sous ses nouvelles clés, pour que le worker garde son rattachement.
    /// </summary>
    private async Task<WorkerIdentityState?> ReadFileAsync(CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(filePath, cancellationToken);
        text = text.Replace("\"nodeToken\"", "\"workerToken\"", StringComparison.Ordinal)
            .Replace("\"clusterId\"", "\"centralId\"", StringComparison.Ordinal)
            .Replace("\"clusterName\"", "\"centralName\"", StringComparison.Ordinal);
        return JsonSerializer.Deserialize<WorkerIdentityState>(text, JsonOptions);
    }

    private async Task<WorkerIdentityState> LoadAsync(CancellationToken cancellationToken)
    {
        if (state is not null) return state;
        Directory.CreateDirectory(options.DataDirectory);
        if (File.Exists(filePath))
        {
            state = await ReadFileAsync(cancellationToken);
        }
        return state ??= new WorkerIdentityState(Guid.NewGuid(), null, null, null, null, false);
    }

    public async Task MarkEnrolledAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (state is null) throw new InvalidOperationException("L’identité locale du worker est absente.");
            state = state with { Enrolled = true, PairingKey = null, PairingExpiresAt = null, PairingPublished = false, EnrollmentFailure = null };
            await SaveAsync(state, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task UpdateMasterUrlAsync(string newMasterUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(newMasterUrl) || !Uri.TryCreate(newMasterUrl, UriKind.Absolute, out var uri)) return;
        var normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await LoadAsync(cancellationToken);
            if (string.Equals(current.ApiBaseUrl?.TrimEnd('/') + "/", normalized, StringComparison.OrdinalIgnoreCase)) return;
            state = current with { ApiBaseUrl = normalized };
            await SaveAsync(state, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task SaveAsync(WorkerIdentityState value, CancellationToken cancellationToken)
    {
        var temporaryPath = filePath + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        File.Move(temporaryPath, filePath, overwrite: true);
    }
}
