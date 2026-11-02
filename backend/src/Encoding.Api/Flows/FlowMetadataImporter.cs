using System.Diagnostics;
using System.Text.Json;
using Encoding.Core;
using Encoding.Api.Plugins;

namespace Encoding.Api.Flows;

/// <summary>Transient, bounded read-only metadata inspection. No library access or runtime execution.</summary>
public sealed class FlowMetadataImporter(ToolStoreService? tools = null)
{
    public const long MaxBytes = 512L * 1024 * 1024;
    private readonly SemaphoreSlim slots = new(2);

    public async Task<JsonElement> ImportAsync(Stream source, CancellationToken cancellationToken)
    {
        if (!await slots.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("Deux lectures MediaInfo sont déjà en cours. Réessayez dans un instant.");
        var directory = Path.Combine(Path.GetTempPath(), "encoding-metadata-" + Guid.NewGuid().ToString("N"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        Process? process = null;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "media");
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920]; long size = 0; int read;
                while ((read = await source.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    size += read;
                    if (size > MaxBytes) throw new InvalidDataException("Le fichier dépasse 512 Mio.");
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                }
                if (size == 0) throw new InvalidDataException("Le fichier est vide.");
            }
            File.SetAttributes(path, FileAttributes.ReadOnly);
            var executable = tools?.ResolveExecutable("mediainfo") ?? FindOnPath("mediainfo")
                ?? throw new InvalidDataException("MediaInfo n'est pas installé sur ce serveur. Installez son paquet depuis le Store.");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            start.ArgumentList.Add("--Output=JSON"); start.ArgumentList.Add(path);
            process = Process.Start(start) ?? throw new InvalidDataException("MediaInfo ne peut pas démarrer.");
            var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, process, timeout.Token);
            var stderr = ReadBoundedAsync(process.StandardError.BaseStream, process, timeout.Token);
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr);
            if (process.ExitCode != 0) throw new InvalidDataException("MediaInfo n’a pas pu analyser le fichier.");
            using var report = JsonDocument.Parse(await stdout);
            return MediaInfoMetadata.Normalize(report.RootElement);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new InvalidDataException("La lecture MediaInfo a dépassé deux minutes."); }
        finally
        {
            try
            {
                if (process is not null)
                {
                    try { if (!process.HasExited) process.Kill(true); }
                    catch (InvalidOperationException) { /* Process exited between inspection and kill. */ }
                    finally { process.Dispose(); }
                }
                if (Directory.Exists(directory))
                {
                    var path = Path.Combine(directory, "media");
                    if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
                    Directory.Delete(directory, true);
                }
            }
            finally { slots.Release(); }
        }
    }
    private static async Task<string> ReadBoundedAsync(Stream source, Process process, CancellationToken ct)
    {
        using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await source.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > 1024 * 1024)
            {
                try { process.Kill(true); } catch (InvalidOperationException) { }
                throw new InvalidDataException("Le rapport MediaInfo dépasse 1 Mio.");
            }
            output.Write(buffer, 0, count);
        }
        return System.Text.Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    }

    private static string? FindOnPath(string executable) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, executable)).FirstOrDefault(File.Exists);
}
