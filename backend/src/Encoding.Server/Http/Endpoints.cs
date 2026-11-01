using System.Security.Claims;
using Encoding.Server.Auth;
using Encoding.Server.Database;

namespace Encoding.Server.Http;

internal static class Endpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
        app.MapGet("/health/ready", (ServerDatabase database) =>
        {
            try
            {
                return database.IsReady()
                    ? Results.Ok(new { status = "ready" })
                    : Results.Json(new ApiError("Base indisponible."), statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception)
            {
                return Results.Json(new ApiError("Base indisponible."), statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).AllowAnonymous();

        app.MapGet("/api/v1/setup", (ServerDatabase database) =>
            Results.Ok(new SetupStatusResponse(database.IsSetup() is false))).AllowAnonymous();

        app.MapPost("/api/v1/setup", (SetupRequest request, HttpContext http, ServerDatabase database) =>
        {
            if (!TryAccount(request.Username, request.Password, out var username, out var password, out var error))
                return Validation(error);
            if (!TryIdentity(request.DisplayName, request.PublicBaseUrl, out var displayName, out var publicBaseUrl, out error))
                return Validation(error);
            try
            {
                var (server, account, token) = database.Setup(username, password, displayName, publicBaseUrl);
                SessionCookies.Set(http, token);
                return Results.Json(ToSetup(server, account), statusCode: StatusCodes.Status201Created);
            }
            catch (SetupAlreadyCompletedException)
            {
                return Results.Json(new ApiError("Le serveur est déjà en service."), statusCode: StatusCodes.Status409Conflict);
            }
        }).AllowAnonymous();

        app.MapPost("/api/v1/session", (LoginRequest request, HttpContext http, ServerDatabase database) =>
        {
            var username = request.Username?.Trim() ?? "";
            var password = request.Password ?? "";
            var loggedIn = database.Login(username, password);
            if (loggedIn is null)
                return Results.Json(new ApiError("Identifiants refusés."), statusCode: StatusCodes.Status401Unauthorized);
            SessionCookies.Set(http, loggedIn.Value.SessionToken);
            return Results.Ok(new SessionResponse(loggedIn.Value.Account.Username, loggedIn.Value.Account.Role));
        }).AllowAnonymous();

        app.MapDelete("/api/v1/session", (HttpContext http, ServerDatabase database) =>
        {
            if (http.Request.Cookies.TryGetValue(SessionCookies.Name, out var token) && token.Length > 0)
                database.DeleteSession(token);
            SessionCookies.Clear(http);
            return Results.NoContent();
        }).AllowAnonymous();

        app.MapGet("/api/v1/session", (ClaimsPrincipal user) =>
            Results.Ok(new SessionResponse(user.Identity?.Name ?? "", "admin")));

        app.MapGet("/api/v1/server", (ServerDatabase database) => Results.Ok(ToServer(database.GetServer())));

        app.MapPut("/api/v1/server", (ServerUpdateRequest request, ServerDatabase database) =>
        {
            if (!TryIdentity(request.DisplayName, request.PublicBaseUrl, out var displayName, out var publicBaseUrl, out var error))
                return Validation(error);
            return Results.Ok(ToServer(database.UpdateServer(displayName, publicBaseUrl)));
        });

        app.MapGet("/api/v1/workers", (ServerDatabase database) =>
            Results.Ok(database.ListWorkers().Select(worker => new WorkerResponse(
                worker.Id,
                worker.DisplayName,
                DateTimeOffset.FromUnixTimeMilliseconds(worker.CreatedAt))).ToArray()));

        app.Map("/api/{**catchall}", () => Results.Json(new ApiError("Route inconnue."), statusCode: StatusCodes.Status404NotFound));
        app.MapFallbackToFile("index.html").AllowAnonymous();
    }

    private static IResult Validation(string message) =>
        Results.Json(new ApiError(message), statusCode: StatusCodes.Status400BadRequest);

    private static bool TryAccount(string? username, string? password, out string normalizedUsername, out string normalizedPassword, out string error)
    {
        normalizedUsername = username?.Trim() ?? "";
        normalizedPassword = password ?? "";
        error = "";
        if (normalizedUsername.Length is < 3 or > 64
            || !normalizedUsername.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
        {
            error = "L'identifiant contient 3 à 64 lettres, chiffres, points, tirets ou underscores.";
            return false;
        }

        if (normalizedPassword.Length is < 12 or > 200)
        {
            error = "Le mot de passe contient 12 à 200 caractères.";
            return false;
        }

        return true;
    }

    private static bool TryIdentity(string? displayName, string? publicBaseUrl, out string normalizedName, out string normalizedUrl, out string error)
    {
        normalizedName = displayName?.Trim() ?? "";
        normalizedUrl = "";
        error = "";
        if (normalizedName.Length is < 1 or > 80 || normalizedName.Any(char.IsControl))
        {
            error = "Le nom du serveur contient 1 à 80 caractères.";
            return false;
        }

        if (!TryNormalizeUrl(publicBaseUrl, out normalizedUrl))
        {
            error = "L'adresse publique est une URL http ou https, sans chemin, sans requête et sans identifiant.";
            return false;
        }

        return true;
    }

    internal static bool TryNormalizeUrl(string? value, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(value) || value.Length > 300)
            return false;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return false;
        if (uri.AbsolutePath is not "/" and not "")
            return false;
        normalized = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }

    private static ServerResponse ToServer(ServerRecord server) => new(
        server.Id,
        server.DisplayName,
        server.PublicBaseUrl,
        DateTimeOffset.FromUnixTimeMilliseconds(server.CreatedAt));

    private static object ToSetup(ServerRecord server, Account account) => new
    {
        server = ToServer(server),
        session = new SessionResponse(account.Username, account.Role),
    };
}

internal sealed record ApiError(string Error);
internal sealed record SetupStatusResponse(bool SetupRequired);
internal sealed record SetupRequest(string? Username, string? Password, string? DisplayName, string? PublicBaseUrl);
internal sealed record LoginRequest(string? Username, string? Password);
internal sealed record ServerUpdateRequest(string? DisplayName, string? PublicBaseUrl);
internal sealed record SessionResponse(string Username, string Role);
internal sealed record ServerResponse(string Id, string DisplayName, string PublicBaseUrl, DateTimeOffset CreatedAt);
internal sealed record WorkerResponse(string Id, string DisplayName, DateTimeOffset CreatedAt);
