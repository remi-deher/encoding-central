using System.Text.Json;
using System.Text.RegularExpressions;

namespace Encoding.Api.Updates;

public sealed record ReleaseNote(string Version, DateTimeOffset PublishedAt, string Notes);
public sealed record ReleaseManifest(string Version, string Repository, string Commit, ReleaseNote[] Releases);
public sealed record UpdateStatus(string InstalledVersion, string? LatestVersion, bool? UpdateAvailable,
    string Status, DateTimeOffset? CheckedAt, ReleaseNote[] Releases);

/// <summary>Release metadata is local to this installation; it is never reconciled with cluster peers.</summary>
public sealed class ReleaseService
{
    private readonly IHttpClientFactory clients;
    public ReleaseService(IHttpClientFactory clients) : this(clients, ReadInstalled()) { }
    internal ReleaseService(IHttpClientFactory clients, ReleaseManifest installed)
    {
        this.clients = clients;
        this.installed = installed;
    }
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ReleaseManifest installed;
    private readonly SemaphoreSlim gate = new(1, 1);
    private UpdateStatus? cached;

    public ReleaseManifest Installed => installed;

    private static ReleaseManifest ReadInstalled() =>
        JsonSerializer.Deserialize<ReleaseManifest>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "release-manifest.json")), JsonOptions)
        ?? throw new InvalidDataException("Manifeste de release vide.");

    public static Version? ParseVersion(string? value) => value is not null
        && Regex.IsMatch(value, @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$", RegexOptions.CultureInvariant)
        && Version.TryParse(value, out var version) ? version : null;

    public async Task<UpdateStatus> GetUpdatesAsync(CancellationToken ct)
    {
        if (ParseVersion(installed.Version) is not { } current)
            return new(installed.Version, null, null, "development", null, installed.Releases);
        if (!Regex.IsMatch(installed.Repository, @"^[A-Za-z0-9][\w.-]*/[A-Za-z0-9][\w.-]*$", RegexOptions.CultureInvariant))
            return new(installed.Version, null, null, "unconfigured", null, installed.Releases);

        await gate.WaitAsync(ct);
        try
        {
            if (cached?.CheckedAt > DateTimeOffset.UtcNow.AddMinutes(-10)) return cached;
            var checkedAt = DateTimeOffset.UtcNow;
            try
            {
                using var client = clients.CreateClient("release-updates");
                using var response = await client.GetAsync(
                    $"https://github.com/{installed.Repository}/releases/latest/download/release-manifest.json", ct);
                response.EnsureSuccessStatusCode();
                var remote = await response.Content.ReadFromJsonAsync<ReleaseManifest>(JsonOptions, ct);
                if (remote is null || remote.Repository != installed.Repository
                    || ParseVersion(remote.Version) is not { } latest || remote.Releases is null
                    || !remote.Releases.Any(r => r is not null && r.Version == remote.Version)
                    || remote.Releases.Any(r => r is null || ParseVersion(r.Version) is null || r.Notes is null))
                    throw new InvalidDataException("Manifeste distant invalide.");
                var releases = remote.Releases.Concat(installed.Releases).DistinctBy(r => r.Version)
                    .OrderByDescending(r => ParseVersion(r.Version)).ToArray();
                return cached = new(installed.Version, remote.Version, latest > current,
                    latest > current ? "available" : "current", checkedAt, releases);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException
                || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                return cached = new(installed.Version, null, null, "unavailable", checkedAt, installed.Releases);
            }
        }
        finally { gate.Release(); }
    }
}
