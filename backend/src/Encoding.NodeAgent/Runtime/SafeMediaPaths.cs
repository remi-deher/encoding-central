using System.Security.Cryptography;

namespace Encoding.NodeAgent.Runtime;

public static class SafeMediaPaths
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,byte> ActiveLocks=new(StringComparer.OrdinalIgnoreCase);
    public static string Resolve(string root,string relative)
    {
        var segments=Encoding.Contracts.Libraries.MediaPathRules.Normalize(relative).Split('/');
        var directory=Path.GetFullPath(root);
        if(new DirectoryInfo(directory).LinkTarget is not null) throw new IOException("Le point d’accès ne doit pas être un lien symbolique.");
        var current=directory;
        foreach(var segment in segments)
        {
            current=Path.Combine(current,segment);
            if(File.Exists(current) && new FileInfo(current).LinkTarget is not null || Directory.Exists(current) && new DirectoryInfo(current).LinkTarget is not null)
                throw new IOException("Les liens symboliques ne sont pas acceptés dans un chemin média.");
        }
        return current;
    }
    public static async Task<string> DigestAsync(string path,CancellationToken ct)
    {
        await using var file=File.OpenRead(path); return Convert.ToHexStringLower(await SHA256.HashDataAsync(file,ct));
    }
    public static async Task CopyVerifiedAsync(string source,string target,string sha,CancellationToken ct)
    {
        var partial=target+".copy.partial";
        try
        {
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using(var input=File.OpenRead(source))
            await using(var output=new FileStream(partial,FileMode.Create,FileAccess.Write,FileShare.None,81920,FileOptions.Asynchronous))
            {
                var buffer=new byte[81920]; int count;
                while((count=await input.ReadAsync(buffer,ct))>0)
                { hash.AppendData(buffer,0,count); await output.WriteAsync(buffer.AsMemory(0,count),ct); }
                await output.FlushAsync(ct); output.Flush(true);
            }
            if(Convert.ToHexStringLower(hash.GetHashAndReset())!=sha) throw new IOException("La copie locale ne correspond pas à la source validée.");
            File.Move(partial,target,true);
        }
        finally { if(File.Exists(partial)) File.Delete(partial); }
    }
    public sealed class MediaLockBusyException() : IOException("Ce média est déjà utilisé par un autre traitement.");
    private sealed class MediaLock(string path,FileStream stream) : IDisposable
    {
        public void Dispose() { stream.Dispose(); ActiveLocks.TryRemove(path,out _); }
    }
    public static IDisposable Lock(string path,string lockDirectory)
    {
        if(OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Le verrouillage média requiert Linux ou Windows.");
        if(!ActiveLocks.TryAdd(path,0)) throw new MediaLockBusyException();
        FileStream stream;
        try {
            Directory.CreateDirectory(lockDirectory);
            var key=Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(path))));
            stream=new FileStream(Path.Combine(lockDirectory,key+".lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.ReadWrite);
        }
        catch { ActiveLocks.TryRemove(path,out _); throw; }
        try { stream.Lock(0,1); return new MediaLock(path,stream); }
        catch(IOException) { stream.Dispose(); ActiveLocks.TryRemove(path,out _); throw new MediaLockBusyException(); }
        catch { stream.Dispose(); ActiveLocks.TryRemove(path,out _); throw; }
    }
}
