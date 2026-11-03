using System.Security.Cryptography;

namespace Encoding.Core.Workers;

/// <summary>
/// Code court à comparer à l'œil lors d'un rattachement (0040) : le worker et le central d'accueil le calculent chacun de leur côté
/// à partir de l'identifiant du worker et de l'empreinte de son jeton. Aucun secret ne circule.
/// </summary>
public static class WorkerVerificationCode
{
    // Sans caractères ambigus (0/O, 1/I).
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const int Length = 6;

    public static string FromToken(Guid workerId, string token) =>
        FromTokenHash(workerId, SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    public static string FromTokenHash(Guid workerId, byte[] tokenHash)
    {
        var digest = SHA256.HashData([.. workerId.ToByteArray(), .. tokenHash]);
        var chars = new char[Length];
        for (var index = 0; index < Length; index++) chars[index] = Alphabet[digest[index] % Alphabet.Length];
        return new string(chars);
    }
}
