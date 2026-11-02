using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Contracts.Nodes;
using Encoding.Core.Nodes;

namespace Encoding.NodeAgent.Services;

public sealed record WorkerIdentityState(
    Guid Id,
    string? ClusterId,
    string? ClusterName,
    string? ApiBaseUrl,
    string? NodeToken,
    bool Enrolled,
    string? PairingKey = null,
    DateTimeOffset? PairingExpiresAt = null,
    bool PairingPublished = false,
    WorkerIdentityState? Previous = null,
    DateTimeOffset? SwitchDeadline = null,
    string? SwitchFailure = null,
    string? DisplayName = null,
    IReadOnlyList<string>? KnownServerUrls = null)
{
    /// <summary>Vrai tant qu'un changement de cluster attend la confirmation du cluster d'accueil : l'ancien rattachement reste intact (0040).</summary>
    public bool IsSwitching => Previous is not null;

    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(ClusterId)
        && Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out _)
        && !string.IsNullOrWhiteSpace(NodeToken);

    public bool IsEnrolled => Enrolled
        && HasCredentials;

    public bool IsPairingPending => !Enrolled && HasCredentials && PairingPublished && !string.IsNullOrWhiteSpace(PairingKey);

    public IReadOnlyList<string> GetCandidateServerUrls(NodeAgentOptions options)
    {
        var urls = new List<string>();
        void Add(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return;
            var normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";
            if (!urls.Contains(normalized, StringComparer.OrdinalIgnoreCase)) urls.Add(normalized);
        }
        if (IsEnrolled && options.Roles.Contains(NodeRole.Server) && options.ClusterApiBaseUrl is { } localUrl && LocalEnrollment.IsLoopback(localUrl))
            Add(localUrl);
        Add(ApiBaseUrl);
        if (IsEnrolled && KnownServerUrls is not null)
        {
            foreach (var url in KnownServerUrls) Add(url);
        }
        return urls;
    }
}

public sealed class WorkerIdentityStore(NodeAgentOptions options)
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
                await using var stream = File.OpenRead(filePath);
                state = await JsonSerializer.DeserializeAsync<WorkerIdentityState>(stream, JsonOptions, cancellationToken);
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
                await using var stream = File.OpenRead(filePath);
                state = await JsonSerializer.DeserializeAsync<WorkerIdentityState>(stream, JsonOptions, cancellationToken);
            }
            var token = state?.NodeToken ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var effectiveDisplayName = displayName ?? state?.DisplayName;
            state = new WorkerIdentityState(state?.Id ?? Guid.NewGuid(), clusterId, clusterName, apiBaseUrl, token, false,
                Previous: state?.Previous, SwitchDeadline: state?.SwitchDeadline, DisplayName: effectiveDisplayName);
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
                await using var stream = File.OpenRead(filePath);
                state = await JsonSerializer.DeserializeAsync<WorkerIdentityState>(stream, JsonOptions, cancellationToken);
            }
            if (state?.IsEnrolled == true)
                throw new InvalidOperationException("Ce worker est déjà rattaché à un cluster.");

            var token = state?.NodeToken ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var canReuseKey = state?.ClusterId == clusterId
                && state.PairingExpiresAt > DateTimeOffset.UtcNow
                && !string.IsNullOrWhiteSpace(state.PairingKey);
            var pairingKey = canReuseKey ? state!.PairingKey : Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
            var effectiveDisplayName = displayName ?? state?.DisplayName;
            state = new WorkerIdentityState(state?.Id ?? Guid.NewGuid(), clusterId, clusterName, apiBaseUrl, token, false, pairingKey, expiresAt,
                Previous: state?.Previous, SwitchDeadline: state?.SwitchDeadline, DisplayName: effectiveDisplayName);
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

    /// <summary>Oublie le rattachement actuel et repart d'une identité neuve : un nœud retiré ne se réactive jamais (0017), il revient sous un nouvel identifiant.</summary>
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
    /// Première phase d'un changement de cluster (0040) : l'identité actuelle est mise de côté, intacte, et une identité neuve attend
    /// l'accueil du nouveau cluster. Sans confirmation avant l'échéance, <see cref="RollbackSwitchAsync"/> rétablit l'ancienne.
    /// </summary>
    public async Task BeginSwitchAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await LoadAsync(cancellationToken);
            if (!current.IsEnrolled) throw new InvalidOperationException("Ce worker n'est rattaché à aucun cluster.");
            if (current.IsSwitching) throw new InvalidOperationException("Un changement de cluster est déjà en cours.");
            state = new WorkerIdentityState(Guid.NewGuid(), null, null, null, null, false,
                Previous: current with { Previous = null, SwitchDeadline = null, SwitchFailure = null },
                SwitchDeadline: DateTimeOffset.UtcNow.Add(timeout),
                DisplayName: current.DisplayName);
            await SaveAsync(state, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Rétablit l'ancien rattachement, conservé tel quel, et retient la raison pour l'afficher.</summary>
    public async Task<bool> RollbackSwitchAsync(string reason, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await LoadAsync(cancellationToken);
            if (current.Previous is null) return false;
            state = current.Previous with { SwitchFailure = reason };
            await SaveAsync(state, cancellationToken);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Abandonne un rattachement qui n'a pas abouti (refus, cluster injoignable, clé expirée). Pendant un changement de cluster, l'ancien rattachement est rétabli
    /// (<see cref="RollbackSwitchAsync"/>) ; pour un nœud autonome, l'identité en attente est effacée pour qu'il redevienne autonome au lieu de rester bloqué
    /// sur un cluster qui ne l'a jamais accepté. L'identifiant du nœud est conservé : le cluster ne l'a jamais enregistré.
    /// </summary>
    public async Task<bool> AbandonPendingAsync(string reason, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await LoadAsync(cancellationToken);
            if (current.Previous is not null) state = current.Previous with { SwitchFailure = reason };
            else if (current.IsEnrolled || !current.HasCredentials) return false;
            else state = new WorkerIdentityState(current.Id, null, null, null, null, false, SwitchFailure: reason, DisplayName: current.DisplayName);
            await SaveAsync(state, cancellationToken);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Seconde phase : le nouveau cluster a répondu. Retourne l'ancien rattachement pour que l'appelant prévienne son cluster, puis l'oublie.</summary>
    public async Task<WorkerIdentityState?> CommitSwitchAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await LoadAsync(cancellationToken);
            var previous = current.Previous;
            state = current with { Previous = null, SwitchDeadline = null, SwitchFailure = null };
            await SaveAsync(state, cancellationToken);
            return previous;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<WorkerIdentityState> LoadAsync(CancellationToken cancellationToken)
    {
        if (state is not null) return state;
        Directory.CreateDirectory(options.DataDirectory);
        if (File.Exists(filePath))
        {
            await using var stream = File.OpenRead(filePath);
            state = await JsonSerializer.DeserializeAsync<WorkerIdentityState>(stream, JsonOptions, cancellationToken);
        }
        return state ??= new WorkerIdentityState(Guid.NewGuid(), null, null, null, null, false);
    }

    public async Task MarkEnrolledAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (state is null) throw new InvalidOperationException("L’identité locale du worker est absente.");
            state = state with { Enrolled = true, PairingKey = null, PairingExpiresAt = null, PairingPublished = false, SwitchFailure = null };
            await SaveAsync(state, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task UpdateKnownServersAsync(IReadOnlyList<string>? serverUrls, CancellationToken cancellationToken)
    {
        if (serverUrls is not { Count: > 0 }) return;
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await LoadAsync(cancellationToken);
            if (!current.IsEnrolled) return;
            var merged = new List<string>();
            void Add(string? url)
            {
                if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
                var normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";
                if (!merged.Contains(normalized, StringComparer.OrdinalIgnoreCase)) merged.Add(normalized);
            }
            if (current.KnownServerUrls is not null)
                foreach (var url in current.KnownServerUrls) Add(url);
            foreach (var url in serverUrls) Add(url);
            var unchanged = current.KnownServerUrls is not null
                && current.KnownServerUrls.Count == merged.Count
                && current.KnownServerUrls.SequenceEqual(merged, StringComparer.OrdinalIgnoreCase);
            if (unchanged) return;
            state = current with { KnownServerUrls = merged };
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
