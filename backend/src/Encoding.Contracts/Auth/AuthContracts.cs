namespace Encoding.Contracts.Auth;

public enum UserRole { Admin, Viewer }

public sealed record CurrentUserResponse(Guid Id, string Username, UserRole Role);

public sealed record AuthStatusResponse(bool SetupRequired, CurrentUserResponse? User);

public sealed record UserResponse(Guid Id, string Username, UserRole Role, bool IsActive, DateTimeOffset CreatedAt);

public sealed record SetupRequest(
    string? Username = null,
    string? Password = null,
    string? SetupToken = null,
    string? NodeDisplayName = null,
    string? Mode = null,
    string? ClusterName = null);

/// <summary>
/// Fiche du nœud local montrée par l'assistant de premier démarrage, avant toute session (liste blanche de champs) :
/// aucune clé de jumelage, aucun code de vérification, aucun identifiant de cluster.
/// <c>Inventory</c> et <c>Tools</c> valent null tant que l'agent local n'a pas répondu (état inconnu, pas « absent »).
/// </summary>
public sealed record SetupNodeResponse(
    string? Name,
    string? DisplayName,
    bool AgentAvailable,
    Encoding.Contracts.Nodes.NodeInventory? Inventory,
    IReadOnlyList<SetupNodeTool>? Tools,
    IReadOnlyList<SetupNodeAddress> Addresses,
    IReadOnlyList<string> Urls);

/// <summary>Outil détecté réduit à ce qu'il faut pour déduire l'accélération matérielle (sans version ni empreinte).</summary>
public sealed record SetupNodeTool(
    string ToolId,
    IReadOnlyList<string> AccelerationTypes,
    IReadOnlyList<Encoding.Contracts.Nodes.NodeEncoderCapability> EncoderChecks);

/// <summary>Adresse IPv4/IPv6 assignée à une interface vue par le processus du nœud (dans un conteneur : son réseau à lui).</summary>
public sealed record SetupNodeAddress(string Interface, string Address);

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


/// <summary>Jeton connu d'un nœud : identifiant et état de révocation seulement, jamais de valeur ni de condensat.</summary>
public sealed record ApiTokenReplicationEntry(Guid Id, bool Revoked);
/// <summary>État des jetons déclaré par un nœud ; <c>Tokens</c> vaut null pour un nœud injoignable (état inconnu).</summary>
public sealed record ApiTokenReplicationNode(Guid NodeId, string Name, bool Reachable, IReadOnlyList<ApiTokenReplicationEntry>? Tokens);
public sealed record ApiTokenReplicationView(IReadOnlyList<ApiTokenReplicationNode> Nodes);
