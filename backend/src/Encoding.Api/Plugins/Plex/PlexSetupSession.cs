using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Encoding.Api.Plugins.Plex;

/// <summary>
/// Session opaque de l'assistant Plex (0008) : jeton du compte plex.tv chiffré et daté, renvoyé au navigateur à la place
/// du jeton. Chiffrée avec Data Protection local ; 15 minutes au plus.
/// </summary>
public sealed class PlexSetupSession(IDataProtectionProvider protection, TimeProvider? clock = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private const string Kind = "plex-setup-session";
    private const string Invalid = "La connexion Plex a expiré ou n’est pas valide. Reconnectez-vous avec Plex.";
    private readonly IDataProtector protector = protection.CreateProtector("Encoding.PlexSetup.v1");
    private readonly TimeProvider time = clock ?? TimeProvider.System;

    public string Protect(string accountToken, string clientIdentifier)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Payload(Kind, accountToken, clientIdentifier, time.GetUtcNow().Add(Lifetime)));
        var encrypted = protector.Protect(payload);
        return Convert.ToBase64String(encrypted).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public (string Token, string ClientIdentifier) Open(string? session)
    {
        if (string.IsNullOrWhiteSpace(session) || session.Length > 8192) throw new PlexSetupException(Invalid);
        try
        {
            var base64 = session.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            var encrypted = Convert.FromBase64String(base64);
            var plain = protector.Unprotect(encrypted);
            var payload = JsonSerializer.Deserialize<Payload>(plain);
            if (payload is null || payload.Kind != Kind || string.IsNullOrEmpty(payload.Token)
                || string.IsNullOrEmpty(payload.ClientIdentifier) || payload.ExpiresAt <= time.GetUtcNow())
                throw new PlexSetupException(Invalid);
            return (payload.Token, payload.ClientIdentifier);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or JsonException or ArgumentException)
        {
            throw new PlexSetupException(Invalid);
        }
    }

    private sealed record Payload(string Kind, string Token, string ClientIdentifier, DateTimeOffset ExpiresAt);
}
