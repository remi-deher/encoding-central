using System.Security.Claims;
using Encoding.Server.Database;

namespace Encoding.Server.Auth;

internal static class SessionCookies
{
    public const string Name = "encoding_session";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMilliseconds(ServerDatabase.SessionLifetimeMs);

    public static void Set(HttpContext http, string token)
    {
        http.Response.Cookies.Append(Name, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = Lifetime,
            IsEssential = true,
        });
    }

    public static void Clear(HttpContext http)
    {
        http.Response.Cookies.Delete(Name, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
        });
    }
}

internal sealed class SessionMiddleware(RequestDelegate next)
{
    public async Task Invoke(HttpContext context, ServerDatabase database)
    {
        if (context.Request.Cookies.TryGetValue(SessionCookies.Name, out var token) && token.Length > 0)
        {
            var account = database.ResolveSession(token);
            if (account is not null)
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, account.Id),
                    new Claim(ClaimTypes.Name, account.Username),
                    new Claim(ClaimTypes.Role, account.Role),
                ], authenticationType: "Session"));
            }
        }

        await next(context);
    }
}
