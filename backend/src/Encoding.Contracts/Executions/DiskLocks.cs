using System.Text.RegularExpressions;

namespace Encoding.Contracts.Executions;

/// <summary>Identifiant d'un disque physique verrouillable, déclaré explicitement par le flow (architecture 0058). Jamais déduit d'un chemin.</summary>
public static partial class DiskLockKey
{
    [GeneratedRegex(@"^[a-z0-9][a-z0-9._ -]{0,63}$", RegexOptions.CultureInvariant)] private static partial Regex Valid();

    /// <summary>Normalise (espaces de bord retirés, minuscules) et valide. Lève <see cref="ArgumentException"/> si l'identifiant est vide, trop long ou contient un séparateur de chemin.</summary>
    public static string Normalize(string? value)
    {
        var key = (value ?? "").Trim().ToLowerInvariant();
        if (!Valid().IsMatch(key)) throw new ArgumentException("Identifiant de disque invalide : lettres, chiffres, point, tiret, tiret bas ou espace, 64 caractères au plus.", nameof(value));
        return key;
    }
}

/// <summary>Demande de prise ou de libération d'un verrou de disque, ceinte par l'époque du bail de l'exécution.</summary>
public sealed record DiskLockRequest(long Epoch, string Disk);

/// <summary><see cref="HolderExecutionId"/> n'est renseigné que quand le disque est occupé par une autre exécution.</summary>
public sealed record DiskLockResponse(bool Acquired, string Disk, Guid? HolderExecutionId = null);
