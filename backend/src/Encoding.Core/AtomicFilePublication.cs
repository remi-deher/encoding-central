using System.Runtime.InteropServices;

namespace Encoding.Core;

/// <summary>Publishes a flushed sibling temporary file without replacing an existing result.</summary>
public static class AtomicFilePublication
{
    public static void Publish(string temporary,string target)
    {
        if(Path.GetFullPath(Path.GetDirectoryName(temporary)!)!=Path.GetFullPath(Path.GetDirectoryName(target)!)) throw new IOException("La publication atomique exige le même dossier.");
        File.Move(temporary,target,false);
        Confirm(target);
    }
    public static void Confirm(string target)
    {
        if(OperatingSystem.IsLinux())
        {
            var descriptor=Open(Path.GetDirectoryName(target)!,0);
            if(descriptor<0) throw new IOException("Le dossier publié ne peut pas être synchronisé.");
            try { if(Sync(descriptor)!=0) throw new IOException("La publication ne peut pas être confirmée sur disque."); }
            finally { Close(descriptor); }
        }
    }
    [DllImport("libc",EntryPoint="open",SetLastError=true)] private static extern int Open(string path,int flags);
    [DllImport("libc",EntryPoint="fsync",SetLastError=true)] private static extern int Sync(int descriptor);
    [DllImport("libc",EntryPoint="close",SetLastError=true)] private static extern int Close(int descriptor);
}
