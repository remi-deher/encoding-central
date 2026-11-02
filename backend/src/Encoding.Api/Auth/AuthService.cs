using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Encoding.Contracts.Auth;

namespace Encoding.Api.Auth;

public enum SetupOutcome { Created, AlreadyConfigured }

public sealed record LoginResult(CurrentUserResponse User, string SessionToken);

public sealed partial class AuthService(
    UserRepository users,
    AuthOptions options,
    IServiceProvider? services = null)
{
    private T? Get<T>() where T : class => services?.GetService(typeof(T)) as T;
    public const int MinimumPasswordLength = 12;
    public const int MaximumPasswordLength = 256;

    private static readonly string DummyHash = PasswordHasher.Hash("mot de passe factice pour égaliser les temps de réponse");

    [GeneratedRegex("^[A-Za-z0-9._@-]{1,64}$")]
    private static partial Regex UsernamePattern();

    public static bool IsValidUsername(string? username) => UsernamePattern().IsMatch(username?.Trim() ?? string.Empty);

    public static Dictionary<string, string[]> ValidateNewUser(string? username, string? password)
    {
        var errors = new Dictionary<string, string[]>();
        if (!IsValidUsername(username))
            errors["username"] = ["Le nom d’utilisateur compte de 1 à 64 caractères : lettres, chiffres, point, tiret, tiret bas ou @."];
        foreach (var (key, value) in ValidatePassword("password", password)) errors[key] = value;
        return errors;
    }

    public static Dictionary<string, string[]> ValidatePassword(string field, string? password)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrEmpty(password) || password.Length < MinimumPasswordLength)
            errors[field] = [$"Le mot de passe doit contenir au moins {MinimumPasswordLength} caractères."];
        else if (password.Length > MaximumPasswordLength)
            errors[field] = [$"Le mot de passe ne peut pas dépasser {MaximumPasswordLength} caractères."];
        return errors;
    }

    public string? LastCreatedPassword { get; private set; }

    public async Task<bool> SetupRequiredAsync(CancellationToken ct) => !await users.AnyAsync(ct);

    public async Task<(SetupOutcome Outcome, UserResponse? User)> SetupAsync(SetupRequest request, CancellationToken ct)
    {
        var username = string.IsNullOrWhiteSpace(request.Username) ? "admin" : request.Username.Trim();
        var password = string.IsNullOrWhiteSpace(request.Password) ? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) : request.Password;
        LastCreatedPassword = password;
        var user = await users.CreateFirstAdministratorAsync(username, PasswordHasher.Hash(password), ct);
        return user is null ? (SetupOutcome.AlreadyConfigured, null) : (SetupOutcome.Created, user);
    }

    /// <summary>Ouvre une session ; l’échec est identique pour un compte inconnu, un mauvais mot de passe, un compte inactif ou verrouillé.</summary>
    public async Task<LoginResult?> LoginAsync(string username, string password, CancellationToken ct)
    {
        var credentials = await users.FindCredentialsAsync(username.Trim(), ct);
        var verified = PasswordHasher.Verify(password, credentials?.PasswordHash ?? DummyHash, out var needsRehash);
        if (credentials is null) return null;
        if (credentials.LockedUntil is { } lockedUntil && lockedUntil > DateTimeOffset.UtcNow) return null;
        if (!verified || !credentials.IsActive)
        {
            if (credentials.IsActive) await users.RecordFailureAsync(credentials.Id, options.MaxFailedAttempts, ct);
            return null;
        }

        await users.RecordSuccessAsync(credentials.Id, needsRehash ? PasswordHasher.Hash(password) : null, ct);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        await users.CreateSessionAsync(credentials.Id, TokenHash(token), options, ct);
        return new LoginResult(new CurrentUserResponse(credentials.Id, credentials.Username, credentials.Role), token);
    }

    public Task LogoutAsync(string token, CancellationToken ct) => users.DeleteSessionAsync(TokenHash(token), ct);

    public async Task<bool> ReauthenticateAsync(Guid userId, string password, CancellationToken ct)
    {
        var credentials = await users.FindCredentialsAsync(userId, ct);
        if (credentials is null || !credentials.IsActive
            || credentials.LockedUntil is { } until && until > DateTimeOffset.UtcNow)
            return false;
        if (!PasswordHasher.Verify(password, credentials.PasswordHash, out var needsRehash))
        {
            await users.RecordFailureAsync(userId, options.MaxFailedAttempts, ct);
            return false;
        }
        await users.RecordSuccessAsync(userId, needsRehash ? PasswordHasher.Hash(password) : null, ct);
        return true;
    }

    /// <summary>Change le mot de passe de l’utilisateur courant après vérification de l’actuel ; ses autres sessions sont révoquées.</summary>
    public async Task<bool> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, string sessionToken, CancellationToken ct)
    {
        var credentials = await users.FindCredentialsAsync(userId, ct);
        if (credentials is null || !PasswordHasher.Verify(currentPassword, credentials.PasswordHash, out _)) return false;
        return await users.SetPasswordAsync(userId, PasswordHasher.Hash(newPassword), TokenHash(sessionToken), ct);
    }

    public Task<bool> ResetPasswordAsync(Guid userId, string newPassword, CancellationToken ct) =>
        users.SetPasswordAsync(userId, PasswordHasher.Hash(newPassword), null, ct);

    public static UserRole ParseRole(string role) => UserRepository.ParseRole(role);

    public Task<IReadOnlyList<UserResponse>> ListUsersAsync(CancellationToken ct) =>
        users.ListAsync(ct);

    public Task<UserResponse?> CreateUserAsync(string username, string password, UserRole role, CancellationToken ct) =>
        users.CreateAsync(username.Trim(), PasswordHasher.Hash(password), role, ct);

    public Task<UserChangeResult> UpdateUserAsync(Guid id, UserRole role, bool isActive, CancellationToken ct) =>
        users.UpdateAsync(id, role, isActive, ct);

    public Task<UserChangeResult> DeleteUserAsync(Guid id, CancellationToken ct) =>
        users.DeleteAsync(id, ct);

    public async Task<IReadOnlyList<ApiTokenResponse>> ListTokensAsync(CancellationToken ct)
    {
        var repo = Get<ApiTokenRepository>();
        return repo is not null ? await repo.ListAsync(ct) : [];
    }

    public async Task<(ApiTokenResponse Summary, string Plaintext)> CreateTokenAsync(
        string name,
        string role,
        DateTimeOffset? expiresAt,
        IReadOnlyList<string>? scopes,
        CancellationToken ct)
    {
        var repo = Get<ApiTokenRepository>() ?? throw new InvalidOperationException("ApiTokenRepository non disponible.");
        return await repo.CreateTokenAsync(name, role, expiresAt, ct, scopes);
    }

    public async Task<bool> RevokeTokenAsync(Guid id, CancellationToken ct)
    {
        var repo = Get<ApiTokenRepository>();
        return repo is not null && await repo.RevokeAsync(id, ct);
    }

    public async Task<bool> DeleteTokenAsync(Guid id, CancellationToken ct)
    {
        var repo = Get<ApiTokenRepository>();
        return repo is not null && await repo.DeleteAsync(id, ct);
    }

    public static byte[] TokenHash(string token) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
}
