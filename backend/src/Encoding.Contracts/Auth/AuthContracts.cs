namespace Encoding.Contracts.Auth;

public enum UserRole { Admin, Viewer }

public sealed record CurrentUserResponse(Guid Id, string Username, UserRole Role);

public sealed record AuthStatusResponse(bool SetupRequired, CurrentUserResponse? User);

/// <summary>
/// Réponse de l'initialisation : superset de <see cref="CurrentUserResponse"/>. <c>GeneratedPassword</c> n'est renseigné que
/// si le serveur a généré le mot de passe (aucun fourni) ; il n'est communiqué qu'une fois, dans cette réponse.
/// </summary>
public sealed record SetupResponse(Guid Id, string Username, UserRole Role, string? GeneratedPassword = null);

public sealed record UserResponse(Guid Id, string Username, UserRole Role, bool IsActive, DateTimeOffset CreatedAt);

public sealed record SetupRequest(
    string? Username = null,
    string? Password = null,
    string? SetupToken = null,
    string? WorkerDisplayName = null,
    string? CentralName = null);

/// <summary>
/// Fiche du worker local montrée par l'assistant de premier démarrage, avant toute session (liste blanche de champs) :
/// aucune clé de jumelage, aucun code de vérification, aucun identifiant de central.
/// <c>Inventory</c> et <c>Tools</c> valent null tant que l'agent local n'a pas répondu (état inconnu, pas « absent »).
/// </summary>
public sealed record SetupWorkerResponse(
    string? Name,
    string? DisplayName,
    bool AgentAvailable,
    Encoding.Contracts.Workers.WorkerInventory? Inventory,
    IReadOnlyList<SetupWorkerTool>? Tools,
    IReadOnlyList<SetupWorkerAddress> Addresses,
    IReadOnlyList<string> Urls);

/// <summary>Outil détecté réduit à ce qu'il faut pour déduire l'accélération matérielle (sans version ni empreinte).</summary>
public sealed record SetupWorkerTool(
    string ToolId,
    IReadOnlyList<string> AccelerationTypes,
    IReadOnlyList<Encoding.Contracts.Workers.WorkerEncoderCapability> EncoderChecks);

/// <summary>Adresse IPv4/IPv6 assignée à une interface vue par le processus du worker (dans un conteneur : son réseau à lui).</summary>
public sealed record SetupWorkerAddress(string Interface, string Address);

public sealed record LoginRequest(string Username, string Password);

public sealed record CreateUserRequest(string Username, string Password, UserRole Role);

public sealed record UpdateUserRequest(UserRole Role, bool IsActive);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record ResetPasswordRequest(string NewPassword);

public sealed record EnrollmentWindowResponse(bool IsOpen, DateTimeOffset? OpenUntil);

public sealed record OpenEnrollmentWindowRequest(int Minutes);

/// <summary>Mode « accepter la première liaison » (0040) : actif jusqu'à la première liaison acceptée ou jusqu'à son expiration.</summary>
public sealed record FirstLinkResponse(bool IsActive, DateTimeOffset? ExpiresAt);

public sealed record ApiTokenResponse(
    Guid Id,
    string Name,
    string TokenPrefix,
    string Role,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    IReadOnlyList<string>? Scopes = null);

public sealed record CreateApiTokenRequest(
    string Name,
    string? Role = "admin",
    DateTimeOffset? ExpiresAt = null,
    IReadOnlyList<string>? Scopes = null,
    string? CurrentPassword = null);

public sealed record ReauthenticateRequest(string CurrentPassword);

public sealed record CreatedApiTokenResponse(
    Guid Id,
    string Name,
    string TokenPrefix,
    string Role,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    string Token,
    IReadOnlyList<string>? Scopes = null);


