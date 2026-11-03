using System.Security.Claims;
using Encoding.Contracts.Auth;
using Microsoft.AspNetCore.Authorization;

namespace Encoding.Api.Auth;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapSessionRoutes();
        app.MapUserManagementRoutes();
    }

    private static void MapSessionRoutes(this WebApplication app)
    {
        var auth = app.MapGroup("/api/v1/auth").WithTags("Authentification");
        auth.MapGet("/status", async (HttpContext http, AuthService service, CancellationToken ct) =>
        {
            var setupRequired = await service.SetupRequiredAsync(ct);
            return Results.Ok(new AuthStatusResponse(setupRequired, setupRequired ? null : Current(http.User)));
        }).AllowAnonymous().WithName("GetAuthStatus");

        auth.MapPost("/setup", async (SetupRequest request, HttpContext http, AuthService service, AuthOptions options, Encoding.Api.Workers.LocalWorkerRegistry localWorker, CancellationToken ct) =>
        {
            // Jeton vérifié avant toute validation : réponse identique quel que soit l'état du central, sans détail sur le jeton attendu.
            if (!service.IsSetupTokenAccepted(request.SetupToken))
                return RefuseSetupToken(http);
            var errors = new Dictionary<string, string[]>();
            if (!string.IsNullOrWhiteSpace(request.Username) || !string.IsNullOrWhiteSpace(request.Password))
            {
                foreach (var (k, v) in AuthService.ValidateNewUser(request.Username, request.Password))
                    errors[k] = v;
            }
            if (!Encoding.Api.Workers.LocalWorkerRegistry.IsValidName(request.WorkerDisplayName))
                errors["workerDisplayName"] = [$"Le nom du worker ne peut pas dépasser {Encoding.Api.Workers.LocalWorkerRegistry.MaximumNameLength} caractères."];
            if (!string.IsNullOrWhiteSpace(request.CentralName) && !Encoding.Api.Infrastructure.CentralIdentityRepository.IsValidName(request.CentralName))
                errors["centralName"] = [$"Le nom du central ne peut pas dépasser {Encoding.Api.Infrastructure.CentralIdentityRepository.MaximumNameLength} caractères."];
            if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);

            var (outcome, _) = await service.SetupAsync(request, ct);
            if (outcome == SetupOutcome.InvalidToken) return RefuseSetupToken(http);
            if (outcome == SetupOutcome.AlreadyConfigured)
                return Results.Conflict(new { title = "Le central possède déjà un administrateur." });
            await localWorker.RequestNameAsync(request.WorkerDisplayName, ct);
            if (!string.IsNullOrWhiteSpace(request.CentralName))
            {
                var centralRepo = http.RequestServices.GetRequiredService<Encoding.Api.Infrastructure.CentralIdentityRepository>();
                if (Encoding.Api.Infrastructure.CentralIdentityRepository.IsValidName(request.CentralName)) await centralRepo.RenameAsync(request.CentralName, ct);
            }
            var effectiveUsername = string.IsNullOrWhiteSpace(request.Username) ? "admin" : request.Username.Trim();
            var effectivePassword = service.LastCreatedPassword ?? request.Password ?? "admin";
            var login = await service.LoginAsync(effectiveUsername, effectivePassword, ct);
            if (login is null) return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Le compte a été créé mais la session n’a pas pu être ouverte.");
            SetSessionCookie(http, login.SessionToken, options);
            return Results.Created("/api/v1/auth/me", login.User);
        }).AllowAnonymous().RequireRateLimiting(AuthExtensions.AnonymousPolicy).WithName("SetupFirstAdministrator");

        // Fiche du worker local pour l'assistant (décision du propriétaire du 2026-10-23) : ouverte sans compte seulement
        // tant qu'aucun administrateur n'existe, puis 404. Liste blanche de champs, sans secret ni identifiant de central.
        auth.MapGet("/setup/worker", async (AuthService service, IHttpClientFactory clients, IConfiguration configuration,
            Encoding.Api.Infrastructure.NetworkSettingsService network, CancellationToken ct) =>
        {
            if (!await service.SetupRequiredAsync(ct)) return Results.NotFound();
            return Results.Ok(await Encoding.Api.Workers.SetupWorkerProbe.ReadAsync(clients, configuration, network, ct));
        }).AllowAnonymous().RequireRateLimiting(AuthExtensions.SetupProbePolicy).WithName("GetSetupNode");

        auth.MapPost("/login", async (LoginRequest request, HttpContext http, AuthService service, AuthOptions options, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password) || request.Password.Length > AuthService.MaximumPasswordLength)
                return Results.Json(new { title = "Identifiants invalides." }, statusCode: StatusCodes.Status401Unauthorized);
            var login = await service.LoginAsync(request.Username, request.Password, ct);
            if (login is null)
                return Results.Json(new { title = "Identifiants invalides ou compte temporairement verrouillé." }, statusCode: StatusCodes.Status401Unauthorized);
            SetSessionCookie(http, login.SessionToken, options);
            return Results.Ok(login.User);
        }).AllowAnonymous().RequireRateLimiting(AuthExtensions.AnonymousPolicy).WithName("Login");

        auth.MapPost("/logout", async (HttpContext http, AuthService service, CancellationToken ct) =>
        {
            if (http.Request.Cookies.TryGetValue(SessionAuthentication.CookieName, out var token)) await service.LogoutAsync(token, ct);
            http.Response.Cookies.Delete(SessionAuthentication.CookieName, new CookieOptions { Path = "/" });
            return Results.NoContent();
        }).AllowViewer().WithName("Logout");

        auth.MapGet("/me", (HttpContext http) => Results.Ok(Current(http.User))).WithName("GetCurrentUser");

        auth.MapPost("/reauthenticate", async (ReauthenticateRequest request, HttpContext http, AuthService service, CancellationToken ct) =>
        {
            var current = Current(http.User);
            if (current is null || http.User.FindFirst("auth_type")?.Value == "api_token")
                return Results.Json(new { title = "Une session utilisateur est requise." }, statusCode: StatusCodes.Status401Unauthorized);
            if (string.IsNullOrEmpty(request.CurrentPassword) || request.CurrentPassword.Length > AuthService.MaximumPasswordLength
                || !await service.ReauthenticateAsync(current.Id, request.CurrentPassword, ct))
                return Results.Json(new { title = "Le mot de passe est incorrect ou le compte est temporairement verrouillé." }, statusCode: StatusCodes.Status401Unauthorized);
            return Results.NoContent();
        }).WithName("ReauthenticateCurrentUser");

        auth.MapPut("/password", async (ChangePasswordRequest request, HttpContext http, AuthService service, CancellationToken ct) =>
        {
            var errors = AuthService.ValidatePassword("newPassword", request.NewPassword);
            if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);
            // Un jeton API ou l'accès sans connexion (Auth:Enabled=false) n'a pas de compte ni de mot de passe à changer.
            if (!http.Request.Cookies.TryGetValue(SessionAuthentication.CookieName, out var token)
                || http.User.FindFirst("auth_type")?.Value == "api_token")
                return Results.Json(new { title = "Aucune session de compte : connectez-vous avec votre compte pour changer le mot de passe." }, statusCode: StatusCodes.Status401Unauthorized);
            var changed = await service.ChangePasswordAsync(Current(http.User)!.Id, request.CurrentPassword ?? string.Empty, request.NewPassword, token, ct);
            return changed ? Results.NoContent() : Results.ValidationProblem(new Dictionary<string, string[]> { ["currentPassword"] = ["Le mot de passe actuel est incorrect."] });
        }).AllowViewer().WithName("ChangeOwnPassword");
    }

    private static IResult RefuseSetupToken(HttpContext http)
    {
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Encoding.Api.Auth.Setup")
            .LogWarning("Initialisation refusée : jeton d'initialisation absent ou invalide.");
        return Results.Json(new { title = "Jeton d’initialisation absent ou invalide." }, statusCode: StatusCodes.Status403Forbidden);
    }

    private static void MapUserManagementRoutes(this WebApplication app)
    {
        var users = app.MapGroup("/api/v1/users").WithTags("Utilisateurs").RequireAuthorization(AuthExtensions.AdministratorPolicy);
        users.MapGet("", async (AuthService service, CancellationToken ct) => Results.Ok(await service.ListUsersAsync(ct)))
            .WithName("ListUsers");

        users.MapPost("", async (CreateUserRequest request, AuthService service, CancellationToken ct) =>
        {
            var errors = AuthService.ValidateNewUser(request.Username, request.Password);
            if (!Enum.IsDefined(request.Role)) errors["role"] = ["Le rôle est inconnu."];
            if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);
            var created = await service.CreateUserAsync(request.Username, request.Password, request.Role, ct);
            return created is null
                ? Results.Conflict(new { title = "Ce nom d’utilisateur existe déjà." })
                : Results.Created($"/api/v1/users/{created.Id}", created);
        }).WithName("CreateUser");

        users.MapPut("/{id:guid}", async (Guid id, UpdateUserRequest request, HttpContext http, AuthService service, CancellationToken ct) =>
        {
            if (!Enum.IsDefined(request.Role)) return (IResult)Results.ValidationProblem(new Dictionary<string, string[]> { ["role"] = ["Le rôle est inconnu."] });
            if (id == Current(http.User)!.Id && (!request.IsActive || request.Role != UserRole.Admin))
                return Results.Conflict(new { title = "Vous ne pouvez pas retirer vos propres droits d’administration ni désactiver votre compte." });
            return await service.UpdateUserAsync(id, request.Role, request.IsActive, ct) switch
            {
                UserChangeResult.Updated => Results.NoContent(),
                UserChangeResult.LastAdministrator => Results.Conflict(new { title = "Le central doit conserver au moins un administrateur actif." }),
                _ => Results.NotFound(),
            };
        }).WithName("UpdateUser");

        users.MapPut("/{id:guid}/password", async (Guid id, ResetPasswordRequest request, AuthService service, CancellationToken ct) =>
        {
            var errors = AuthService.ValidatePassword("newPassword", request.NewPassword);
            if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);
            return await service.ResetPasswordAsync(id, request.NewPassword, ct) ? Results.NoContent() : Results.NotFound();
        }).WithName("ResetUserPassword");

        users.MapDelete("/{id:guid}", async (Guid id, HttpContext http, AuthService service, CancellationToken ct) =>
        {
            if (id == Current(http.User)!.Id) return Results.Conflict(new { title = "Vous ne pouvez pas supprimer votre propre compte." });
            return await service.DeleteUserAsync(id, ct) switch
            {
                UserChangeResult.Updated => Results.NoContent(),
                UserChangeResult.LastAdministrator => Results.Conflict(new { title = "Le central doit conserver au moins un administrateur actif." }),
                _ => Results.NotFound(),
            };
        }).WithName("DeleteUser");
    }

    internal static CurrentUserResponse? Current(ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated == true
            ? new CurrentUserResponse(
                Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!),
                principal.FindFirstValue(ClaimTypes.Name)!,
                AuthService.ParseRole(principal.FindFirstValue(ClaimTypes.Role)!))
            : null;

    private static void SetSessionCookie(HttpContext http, string token, AuthOptions options) =>
        http.Response.Cookies.Append(SessionAuthentication.CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = options.SecureCookie ?? http.Request.IsHttps,
            Path = "/",
            Expires = DateTimeOffset.UtcNow + options.SessionAbsoluteLifetime,
            IsEssential = true,
        });
}
