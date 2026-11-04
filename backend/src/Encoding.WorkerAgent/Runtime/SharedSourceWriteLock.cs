using System.Diagnostics;
using System.Text.Json;

namespace Encoding.WorkerAgent.Runtime;

/// <summary>État d'un verrou d'écriture de source : <c>Orphaned</c> n'est affirmé que si le propriétaire est prouvé disparu (même hôte, processus absent ou réutilisé) ; <c>ProbablyOrphaned</c> désigne un fichier vide ou tronqué (coupure avant l'écriture de l'identité), ancien et non verrouillé.</summary>
internal enum SourceWriteLockState { Free, Held, Unverifiable, Orphaned, ProbablyOrphaned }

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
            throw status.State is SourceWriteLockState.Orphaned or SourceWriteLockState.ProbablyOrphaned ? new OrphanSourceWriteLockException(status) : new SafeMediaPaths.MediaLockBusyException();
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
        catch (UnauthorizedAccessException) { return new(SourceWriteLockState.Unverifiable, path, null); } // illisible : on ne peut rien affirmer
        catch (JsonException) { return ClassifyUnreadable(path); } // vide, tronqué ou ancien format
        if (info is null || string.IsNullOrEmpty(info.Owner)) return ClassifyUnreadable(path);
        if (!string.Equals(info.Host, Environment.MachineName, StringComparison.Ordinal)) return new(SourceWriteLockState.Unverifiable, path, info);
        return new(OwnerProcessIsAlive(info) ? SourceWriteLockState.Held : SourceWriteLockState.Orphaned, path, info);
    }

    /// <summary>Âge minimal d'un verrou sans identité lisible avant de le dire « probablement orphelin » (laisse le temps à un propriétaire vivant d'écrire son identité).</summary>
    internal static TimeSpan MinimumUnreadableAge { get; } = TimeSpan.FromMinutes(5);

    private static SourceWriteLockStatus ClassifyUnreadable(string path)
    {
        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < MinimumUnreadableAge) return new(SourceWriteLockState.Unverifiable, path, null);
        try
        {
            // Si l'ouverture exclusive réussit, plus personne ne tient le fichier ouvert. Elle ne prouve rien (ancien format possible) : jamais de suppression automatique.
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return new(SourceWriteLockState.ProbablyOrphaned, path, null);
        }
        catch (IOException) { return new(SourceWriteLockState.Held, path, null); }
        catch (UnauthorizedAccessException) { return new(SourceWriteLockState.Unverifiable, path, null); }
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
        status.Info is null
            ? $"Verrou d'écriture probablement orphelin ({Path.GetFileName(status.LockPath)}) : fichier sans identité de propriétaire, non utilisé et ancien. Intervention de l'opérateur requise."
            : $"Verrou d'écriture orphelin ({Path.GetFileName(status.LockPath)}) : le processus {status.Info?.ProcessId} de l'hôte {status.Info?.Host} (exécution {status.Info?.ExecutionId:D}) n'existe plus. Intervention de l'opérateur requise.";
}

internal sealed class SourceWriteDeferredException() : IOException("Écriture différée : autorisation du central indisponible.");
