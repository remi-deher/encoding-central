namespace Encoding.NodeAgent.Runtime;

/// <summary>Exclusion at the shared source path, independent of local worker identities. A crashed owner leaves a visible lock; never expire it on a timer.</summary>
internal static class SharedSourceWriteLock
{
    public static IDisposable Acquire(string original)
    {
        var path=Path.Combine(Path.GetDirectoryName(original)!,Path.GetFileNameWithoutExtension(original)+".encoding-write.lock");
        FileStream stream;
        try { stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None); }
        catch(IOException) when(File.Exists(path)) { throw new SafeMediaPaths.MediaLockBusyException(); }
        var owner=Guid.NewGuid().ToString("N");
        try
        {
            stream.Write(System.Text.Encoding.UTF8.GetBytes(owner));stream.Flush(true);
            return new Held(path,stream,owner);
        }
        catch {stream.Dispose();File.Delete(path);throw;}
    }
    private sealed class Held(string path,FileStream stream,string owner) : IDisposable
    {
        public void Dispose() { stream.Dispose();if(File.Exists(path) && File.ReadAllText(path)==owner) File.Delete(path); }
    }
}

internal sealed class SourceWriteDeferredException() : IOException("Écriture différée : autorisation du cluster indisponible.");
