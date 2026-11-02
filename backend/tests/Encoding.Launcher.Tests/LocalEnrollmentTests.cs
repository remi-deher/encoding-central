using Encoding.Core.Nodes;

namespace Encoding.Launcher.Tests;

internal static class LocalEnrollmentTests
{
    public static Task RunAsync()
    {
        var directory = Directory.CreateTempSubdirectory("launcher-enroll-").FullName;
        try
        {
            var path = Path.Combine(directory, LocalEnrollment.FileName);
            Check.That(LocalEnrollment.Read(path) is null, "Sans fichier, aucun secret local n’existe.");
            Check.That(!LocalEnrollment.Matches(path, "x"), "Sans fichier, aucun secret n’est accepté.");

            LocalEnrollment.Create(path);
            var first = LocalEnrollment.Read(path)!;
            Check.That(first.Length >= 40, "Le secret est aléatoire et long.");
            Check.That(LocalEnrollment.Matches(path, first), "Le secret du fichier est accepté.");
            Check.That(!LocalEnrollment.Matches(path, first + "x") && !LocalEnrollment.Matches(path, "") && !LocalEnrollment.Matches(path, null), "Un autre secret, vide ou absent est refusé.");
            if (!OperatingSystem.IsWindows())
                Check.That(File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Le secret n’est lisible que par son propriétaire.");

            LocalEnrollment.Create(path);
            Check.That(LocalEnrollment.Read(path) != first, "Chaque démarrage du lanceur renouvelle le secret.");
            Check.That(!LocalEnrollment.Matches(path, first), "L’ancien secret ne fonctionne plus.");
            Check.That(!LocalEnrollment.Matches(null, first), "Sans fichier configuré, aucun secret n’est accepté.");

            Check.That(LocalEnrollment.IsLoopback("http://127.0.0.1:8080/") && LocalEnrollment.IsLoopback("http://localhost:8080/"), "Le serveur du conteneur est reconnu comme local.");
            Check.That(!LocalEnrollment.IsLoopback("http://192.168.1.50:8080/") && !LocalEnrollment.IsLoopback("not a url"), "Un cluster distant ne reçoit jamais le secret.");
            Console.WriteLine("PASS: secret d’enrôlement du nœud local.");
        }
        finally { Directory.Delete(directory, true); }
        return Task.CompletedTask;
    }
}
