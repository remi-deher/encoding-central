using System.Diagnostics;
using System.Text.Json;

namespace Encoding.WorkerAgent.Runtime;

/// <summary>État d'un verrou d'écriture de source : <c>Orphaned</c> n'est affirmé que si le propriétaire est prouvé disparu (même hôte, processus absent ou réutilisé).</summary>
internal enum SourceWriteLockState { Free, Held, Unverifiable, Orphaned }

/// <summary>Contenu du fichier de verrou : identifie le propriétaire pour permettre la détection d'un verrou laissé par un arrêt brutal.</summary>
internal sealed record SourceWriteLockInfo(string Owner, string Host, int ProcessId, long ProcessStartedUtcTicks, Guid? ExecutionId, DateTimeOffset AcquiredAt);

internal sealed record SourceWriteLockStatus(SourceWriteLockState State, string LockPath, SourceWriteLockInfo? Info);

/// <summary>
/// Exclusion au chemin partagé de la source, indépendante des identités de worker. Un propriétaire arrêté brutalement laisse un verrou visible :
/// il n'expire jamais sur minuterie et n'est jamais supprimé silencieusement. <see cref="Inspect"/> le classe (vivant, invérifiable, orphelin) ;
/// seul <see cref="ReleaseOrphan"/>, appelé explicitement par un opérateur, retire un verrou prouvé orphelin.
/// </summary>
internal static class SharedSourceWriteLock
{
    public static string LockPath(string original) => Path.Combine(Path.GetDirectoryName(original)!, Path.GetFileNameWithoutExtension(original) + ".encoding-write.lock");

    public static IDisposable Acquire(string original, Guid? executionId = null)
    {
        var path = LockPath(original);
        FileStream stream;
        try { stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
        catch (IOException) when (File.Exists(path))
        {
            var status = Inspect(original);
            throw status.State == SourceWriteLockState.Orphaned ? new OrphanSourceWriteLockException(status) : new SafeMediaPaths.MediaLockBusyException();
        }
        using var process = Process.GetCurrentProcess();
        var info = new SourceWriteLockInfo(Guid.NewGuid().ToString("N"), Environment.MachineName, process.Id, process.StartTime.ToUniversalTime().Ticks, executionId, DateTimeOffset.UtcNow);
        try
        {
            stream.Write(JsonSerializer.SerializeToUtf8Bytes(info)); stream.Flush(true);
            return new Held(path, stream, info.Owner);
        }
        catch { stream.Dispose(); File.Delete(path); throw; }
    }

    /// <summary>Classe le verrou sans jamais le modifier. Un contenu illisible ou d'un autre hôte reste « invérifiable » : possiblement vivant, donc respecté.</summary>
    public static SourceWriteLockStatus Inspect(string original)
    {
        var path = LockPath(original);
        if (!File.Exists(path)) return new(SourceWriteLockState.Free, path, null);
        SourceWriteLockInfo? info;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            info = JsonSerializer.Deserialize<SourceWriteLockInfo>(stream);
        }
        catch (FileNotFoundException) { return new(SourceWriteLockState.Free, path, null); }
        catch (IOException) { return new(SourceWriteLockState.Held, path, null); } // ouvert en exclusivité par son propriétaire : vivant
        catch (JsonException) { return new(SourceWriteLockState.Unverifiable, path, null); } // ancien format ou écriture interrompue
        if (info is null || string.IsNullOrEmpty(info.Owner)) return new(SourceWriteLockState.Unverifiable, path, info);
        if (!string.Equals(info.Host, Environment.MachineName, StringComparison.Ordinal)) return new(SourceWriteLockState.Unverifiable, path, info);
        return new(OwnerProcessIsAlive(info) ? SourceWriteLockState.Held : SourceWriteLockState.Orphaned, path, info);
    }

    /// <summary>Retire le verrou uniquement s'il est encore prouvé orphelin et appartient à <paramref name="expectedOwner"/> (action explicite d'un opérateur).</summary>
    public static bool ReleaseOrphan(string original, string expectedOwner)
    {
        var status = Inspect(original);
        if (status.State != SourceWriteLockState.Orphaned || status.Info?.Owner != expectedOwner) return false;
        File.Delete(status.LockPath); return true;
    }

    private static bool OwnerProcessIsAlive(SourceWriteLockInfo info)
    {
        try
        {
            using var process = Process.GetProcessById(info.ProcessId);
            if (process.HasExited) return false;
            // Un identifiant de processus réutilisé a une date de démarrage différente : l'ancien propriétaire a disparu.
            return Math.Abs(process.StartTime.ToUniversalTime().Ticks - info.ProcessStartedUtcTicks) < TimeSpan.FromSeconds(2).Ticks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        // Toute autre erreur (droits, plateforme) : on ne peut pas prouver l'absence, on considère le verrou vivant.
        catch (Exception) { return true; }
    }

    private sealed class Held(string path, FileStream stream, string owner) : IDisposable
    {
        public void Dispose()
        {
            stream.Dispose();
            try { if (File.Exists(path) && JsonSerializer.Deserialize<SourceWriteLockInfo>(File.ReadAllBytes(path))?.Owner == owner) File.Delete(path); }
            catch (JsonException) { }
        }
    }
}

/// <summary>Verrou laissé par un propriétaire disparu : le média reste bloqué (jamais retiré automatiquement) jusqu'à une intervention explicite de l'opérateur.</summary>
internal sealed class OrphanSourceWriteLockException(SourceWriteLockStatus status) : SafeMediaPaths.MediaLockBusyException(Describe(status))
{
    public SourceWriteLockStatus Status { get; } = status;
    private static string Describe(SourceWriteLockStatus status) =>
        $"Verrou d'écriture orphelin ({Path.GetFileName(status.LockPath)}) : le processus {status.Info?.ProcessId} de l'hôte {status.Info?.Host} (exécution {status.Info?.ExecutionId:D}) n'existe plus. Intervention de l'opérateur requise.";
}

internal sealed class SourceWriteDeferredException() : IOException("Écriture différée : autorisation du central indisponible.");
