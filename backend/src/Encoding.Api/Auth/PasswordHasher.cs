using System.Security.Cryptography;

namespace Encoding.Api.Auth;

/// <summary>PBKDF2-HMAC-SHA512 avec sel aléatoire ; le format stocké porte ses propres paramètres.</summary>
public static class PasswordHasher
{
    private const string Prefix = "pbkdf2-sha512";
    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int KeyBytes = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA512, KeyBytes);
        return $"{Prefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    /// <summary>Vérifie un mot de passe ; <paramref name="needsRehash"/> indique des paramètres devenus plus faibles que ceux en vigueur.</summary>
    public static bool Verify(string password, string stored, out bool needsRehash)
    {
        needsRehash = false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Prefix || !int.TryParse(parts[1], out var iterations) || iterations <= 0) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, expected.Length);
            if (!CryptographicOperations.FixedTimeEquals(actual, expected)) return false;
            needsRehash = iterations < Iterations;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
