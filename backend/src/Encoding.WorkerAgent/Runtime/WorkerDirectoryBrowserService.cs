using Encoding.Contracts.Workers;
using Encoding.WorkerAgent.Services;

namespace Encoding.WorkerAgent.Runtime;

public sealed class WorkerDirectoryBrowserService(
    WorkerAgentOptions options,
    WorkerIdentityStore identities,
    WorkerWakeSignal wake,
    ILogger<WorkerDirectoryBrowserService> logger) : BackgroundService
{
    private const int MaximumEntries = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var waiter = wake.CreateWaiter();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var identity = await identities.GetAsync(stoppingToken);
                if (identity.IsEnrolled)
                {
                    var handled = false;
                    foreach (var serverUrl in identity.GetCandidateServerUrls(options))
                    {
                        try
                        {
                            using var client = new RuntimeClient(identity, serverUrl);
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                            timeout.CancelAfter(TimeSpan.FromSeconds(10));
                            var claim = await client.PostAsync<WorkerDirectoryBrowseClaim>("directory-browser/claim", new { }, timeout.Token);
                            if (claim is not null)
                            {
                                var completion = await Task.Run(() => Browse(claim.Path, stoppingToken), stoppingToken);
                                using var submitTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                                submitTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                                await client.PostAsync<object>($"directory-browser/{claim.Id:D}/complete", completion, submitTimeout.Token);
                                handled = true;
                                break;
                            }
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                        catch (Exception exception) { logger.LogDebug(exception, "Claim exploration de dossiers momentanément indisponible."); }
                    }
                    if (handled) continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogDebug("L’exploration des dossiers est temporairement indisponible ({Type}).", exception.GetType().Name);
            }
            try { await waiter.WaitAsync(2000, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    public static WorkerDirectoryBrowseCompletion Browse(string path, CancellationToken cancellationToken)
    {
        try
        {
            string[] roots = string.IsNullOrWhiteSpace(path)
                ? (OperatingSystem.IsWindows() ? DriveInfo.GetDrives().Select(drive => drive.RootDirectory.FullName).ToArray() : [Path.GetPathRoot(Environment.CurrentDirectory) ?? "/"])
                : [Path.GetFullPath(path)];
            var entries = new List<WorkerDirectoryBrowserEntry>(MaximumEntries);
            var hasMore = false;
            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.Exists(root)) continue;
                for (var current = new DirectoryInfo(root); current is not null; current = current.Parent)
                    if (current.LinkTarget is not null || (current.Attributes & FileAttributes.ReparsePoint) != 0)
                        return new([], false, "Les liens symboliques ne sont pas parcourus par cet explorateur.");
                foreach (var candidate in Directory.EnumerateDirectories(root))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var child = new DirectoryInfo(candidate);
                    if ((child.Attributes & FileAttributes.ReparsePoint) != 0 || child.Name.StartsWith(".encoding", StringComparison.OrdinalIgnoreCase)) continue;
                    if (entries.Count == MaximumEntries) { hasMore = true; break; }
                    entries.Add(new(child.Name, child.FullName));
                }
                if (hasMore) break;
            }
            return new(entries.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray(), hasMore, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException or NotSupportedException)
        {
            return new([], false, exception is UnauthorizedAccessException or System.Security.SecurityException
                ? "L’agent Encoding n’a pas les droits nécessaires pour lire ce dossier."
                : "Ce dossier est inaccessible ou le chemin n’est pas valide.");
        }
    }
}
