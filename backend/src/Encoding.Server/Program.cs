using Encoding.Server.Auth;
using Encoding.Server.Database;
using Encoding.Server.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, ApiAuthorizationResultHandler>();
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});
builder.Services.AddSingleton(services =>
{
    var environment = services.GetRequiredService<IWebHostEnvironment>();
    var configuration = services.GetRequiredService<IConfiguration>();
    var path = ServerDatabase.ResolvePath(configuration["Database:Path"] ?? "", environment.ContentRootPath);
    return new ServerDatabase(path);
});

var app = builder.Build();
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await context.Response.WriteAsJsonAsync(new ApiError("Erreur interne."));
}));
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (context.Request.Path.StartsWithSegments("/api"))
        context.Response.Headers["Cache-Control"] = "no-store";
    await next(context);
});
app.UseStaticFiles();
app.UseMiddleware<SessionMiddleware>();
app.UseAuthorization();
Endpoints.Map(app);
app.Run();

public partial class Program;
