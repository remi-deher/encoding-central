using Encoding.Api.Infrastructure;
using Encoding.Api.Auth;
using Encoding.Core.Plugins;

namespace Encoding.Api.Plugins;

/// <summary>Store de plugins (0063) : dépôts, paquets, installation, import d'archive et distribution aux workers.</summary>
public static class PluginStoreEndpoints
{
    public static void MapPluginStoreEndpoints(this WebApplication app)
    {
        var store = app.MapGroup("/api/v1/store").WithTags("Store");

        store.MapGet("/repositories", (PluginRepositoryService repositories) =>
            Results.Ok(repositories.List().Select(item => new
            {
                item.Id,
                item.Name,
                item.Url,
                item.Description,
                item.IsOfficial,
                IsLocked = item.IsOfficial,
                item.Enabled,
                PackagesCount = item.Packages.Count + (item.Tools?.Count ?? 0),
                UpdatedAt = item.LastSyncAt,
                item.LastError
            }))).AllowViewer().WithName("ListStoreRepositories");

        store.MapPost("/repositories", async (AddRepositoryRequest request, PluginRepositoryService repositories, CancellationToken ct) =>
        {
            try { return Results.Ok(Summary(await repositories.AddAsync(request.Url, ct))); }
            catch (PluginRepositoryException exception) { return Results.UnprocessableEntity(new { title = exception.Message }); }
        }).WithName("AddStoreRepository");

        store.MapPost("/repositories/{repositoryId}/sync", async (string repositoryId, PluginRepositoryService repositories, CancellationToken ct) =>
            await repositories.SyncAsync(repositoryId, ct) is { } synced ? Results.Ok(Summary(synced)) : Results.NotFound()).WithName("SyncStoreRepository");

        store.MapPut("/repositories/{repositoryId}", async (string repositoryId, SetRepositoryEnabledRequest request, PluginRepositoryService repositories, CancellationToken ct) =>
            await repositories.SetEnabledAsync(repositoryId, request.Enabled, ct) is { } updated ? Results.Ok(Summary(updated)) : Results.NotFound()).WithName("SetStoreRepositoryEnabled");

        store.MapDelete("/repositories/{repositoryId}", async (string repositoryId, PluginRepositoryService repositories, CancellationToken ct) =>
        {
            try { return await repositories.RemoveAsync(repositoryId, ct) ? Results.NoContent() : Results.NotFound(); }
            catch (PluginRepositoryException exception) { return Results.UnprocessableEntity(new { title = exception.Message }); }
        }).WithName("RemoveStoreRepository");

        store.MapGet("/packages", (PluginRepositoryService repositories, PluginStoreService plugins, ToolStoreService tools) =>
        {
            var installed = plugins.Installed().ToDictionary(item => item.Id, StringComparer.Ordinal);
            var listed = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<object>();
            foreach (var repository in repositories.List().Where(item => item.Enabled))
                foreach (var package in repository.Packages)
                {
                    if (!listed.Add(package.Id)) continue;
                    installed.TryGetValue(package.Id, out var current);
                    result.Add(new
                    {
                        package.Id,
                        package.Name,
                        Kind = "plugin",
                        package.Category,
                        package.CategoryLabel,
                        Version = package.LatestVersion,
                        InstalledVersion = current?.Version,
                        HasUpdate = current is not null && Version.TryParse(package.LatestVersion, out var latest) && Version.TryParse(current.Version, out var have) && latest > have,
                        Author = package.Author ?? "",
                        Description = package.Summary,
                        package.RequiredTools,
                        RepositoryId = repository.Id,
                        Icon = "🧩"
                    });
                }
            foreach (var current in installed.Values.Where(item => !listed.Contains(item.Id)))
                result.Add(new
                {
                    current.Id,
                    current.Name,
                    Kind = "plugin",
                    Category = "local",
                    CategoryLabel = "Installé localement",
                    current.Version,
                    InstalledVersion = (string?)current.Version,
                    HasUpdate = false,
                    Author = "",
                    Description = $"Installé depuis {current.Source} le {current.InstalledAt:yyyy-MM-dd}.",
                    RequiredTools = Array.Empty<string>(),
                    RepositoryId = current.Source,
                    Icon = "📦"
                });
            var installedTools = tools.Available().ToDictionary(item => (item.Id, item.Platform));
            var seenTools = new HashSet<string>(StringComparer.Ordinal);
            foreach (var repository in repositories.List().Where(item => item.Enabled))
                foreach (var package in repository.Tools ?? [])
                {
                    if (seenTools.Contains(package.Id)) continue;
                    var platform = Encoding.Contracts.Tools.ToolPackagePlatform.Current;
                    var currentTool = installedTools.GetValueOrDefault((package.Id, platform));
                    var available = package.Versions.Where(item => item.Platform == platform)
                        .OrderByDescending(item => Version.TryParse(item.Version, out var parsed) ? parsed : new Version(0, 0)).FirstOrDefault();
                    if (available is null) continue;
                    seenTools.Add(package.Id);
                    result.Add(new
                    {
                        Id = package.Id, package.Name, Kind = "tool", Category = "tools", CategoryLabel = "Outil média",
                        Platform = platform,
                        Sha256 = available.Sha256,
                        InstalledSha256 = currentTool?.ArchiveSha256,
                        Version = available.Version, InstalledVersion = currentTool?.Version,
                        HasUpdate = currentTool is not null && Version.TryParse(available.Version, out var latest) && Version.TryParse(currentTool.Version, out var have) && latest > have,
                        Author = "", Description = package.Summary, RequiredTools = Array.Empty<string>(), RepositoryId = repository.Id, Icon = "🛠️"
                    });
                }
            foreach (var currentTool in installedTools.Values.Where(item => !seenTools.Contains(item.Id)))
                result.Add(new
                {
                    Id = currentTool.Id, Name = currentTool.Id, Kind = "tool", Category = "tools", CategoryLabel = "Installé localement",
                    Platform = currentTool.Platform,
                    Sha256 = currentTool.ArchiveSha256,
                    InstalledSha256 = currentTool.ArchiveSha256,
                    Version = currentTool.Version, InstalledVersion = (string?)currentTool.Version, HasUpdate = false,
                    Author = "", Description = $"Paquet {currentTool.Platform}, SHA-256 {currentTool.ArchiveSha256}.",
                    RequiredTools = Array.Empty<string>(), RepositoryId = currentTool.Source, Icon = "🛠️"
                });
            return Results.Ok(result);
        }).AllowViewer().WithName("ListStorePackages");

        store.MapPost("/packages/install", async (InstallPackageRequest request, PluginRepositoryService repositories, CancellationToken ct) =>
        {
            try { return Results.Ok(await repositories.InstallAsync(request.RepositoryId, request.Id, request.Version, ct)); }
            catch (PluginRepositoryException exception) { return Results.UnprocessableEntity(new { title = exception.Message }); }
        }).WithName("InstallStorePackage");

        store.MapPost("/tools/install", async (InstallToolPackageRequest request, PluginRepositoryService repositories, CancellationToken ct) =>
        {
            try { return Results.Ok(await repositories.InstallToolAsync(request.RepositoryId, request.Id, request.Version, request.Platform, ct)); }
            catch (PluginRepositoryException exception) { return Results.UnprocessableEntity(new { title = exception.Message }); }
        }).WithName("InstallStoreToolPackage");

        // Import d'une archive .zip (développement local, environnement isolé). Corps brut : 32 Mo au plus.
        store.MapPost("/packages/import", async (HttpRequest request, PluginStoreService plugins, CancellationToken ct) =>
        {
            if (request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = PluginPackage.MaxArchiveBytes + 1024;
            if (request.ContentLength > PluginPackage.MaxArchiveBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            using var memory = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
            {
                if (memory.Length + read > PluginPackage.MaxArchiveBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                memory.Write(buffer, 0, read);
            }
            try { return Results.Ok(await plugins.ImportAsync(memory.ToArray(), "sideload")); }
            catch (PluginPackageException exception) { return Results.UnprocessableEntity(new { title = exception.Message }); }
        }).WithName("ImportStorePackage");

        store.MapPost("/tools/import", async (HttpRequest request, ToolStoreService tools, CancellationToken ct) =>
        {
            var max = Encoding.Core.Tools.ToolPackageArchive.MaxArchiveBytes;
            if (request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = max + 1024;
            if (request.ContentLength > max) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            using var memory = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
            {
                if (memory.Length + read > max) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                memory.Write(buffer, 0, read);
            }
            try { return Results.Ok(await tools.ImportAsync(memory.ToArray(), "sideload", ct: ct)); }
            catch (Encoding.Core.Tools.ToolPackageException exception) { return Results.UnprocessableEntity(new { title = exception.Message }); }
        }).WithName("ImportStoreToolPackage");

        store.MapDelete("/tools/{toolId}/{platform}", async (string toolId, string platform, ToolStoreService tools, PluginStoreService plugins, CancellationToken ct) =>
        {
            if (plugins.RequiresToolPackage(toolId)) return Results.Conflict(new { title = $"L'outil {toolId} est encore requis par un plugin installé." });
            return await tools.RemoveAsync(toolId, platform, ct) ? Results.NoContent() : Results.NotFound();
        }).WithName("RemoveStoreToolPackage");

        store.MapDelete("/packages/{pluginId}", async (string pluginId, PluginStoreService plugins) =>
            await plugins.RemoveAsync(pluginId) ? Results.NoContent() : Results.NotFound()).WithName("RemoveStorePackage");

        // Distribution aux workers, authentifiés par le jeton de leur nœud (lecture seule).
        store.MapGet("/installed", (PluginStoreService plugins) =>
            Results.Ok(plugins.Installed().Select(item => new { item.Id, item.Version, item.Sha256 }))).AllowNodeCredentials().WithName("ListInstalledPluginPackages");

        store.MapGet("/tools/installed", (ToolStoreService tools) =>
            Results.Ok(tools.Available().Select(item => new { item.Id, item.Version, item.Platform, item.ArchiveSha256 })))
            .AllowNodeCredentials().WithName("ListInstalledToolPackages");

        store.MapGet("/packages/{pluginId}/{version}/download", (string pluginId, string version, PluginStoreService plugins) =>
        {
            var installed = plugins.Installed().SingleOrDefault(item => item.Id == pluginId && item.Version == version);
            if (installed is null) return Results.NotFound();
            var path = plugins.PackagePath(installed.Id, installed.Version);
            return FileResults.ServeOrNotFound(path, "application/zip", $"{pluginId}-{version}.zip");
        }).AllowNodeCredentials().WithName("DownloadPluginPackage");

        store.MapGet("/tools/packages/{toolId}/{version}/{platform}/download", (string toolId, string version, string platform, ToolStoreService tools) =>
        {
            var installed = tools.Available().SingleOrDefault(item => item.Id == toolId && item.Version == version && item.Platform == platform);
            if (installed is null) return Results.NotFound();
            var path = tools.PackagePath(installed.Id, installed.Version, installed.Platform);
            return FileResults.ServeOrNotFound(path, "application/zip", $"{toolId}-{version}-{platform}.zip");
        }).AllowNodeCredentials().WithName("DownloadToolPackage");
    }

    private static object Summary(PluginRepository item) => new
    {
        item.Id, item.Name, item.Url, item.Description, item.IsOfficial, IsLocked = item.IsOfficial, item.Enabled,
        PackagesCount = item.Packages.Count + (item.Tools?.Count ?? 0), UpdatedAt = item.LastSyncAt, item.LastError
    };
}

public sealed record AddRepositoryRequest(string? Url);
public sealed record SetRepositoryEnabledRequest(bool Enabled);
public sealed record InstallPackageRequest(string RepositoryId, string Id, string? Version);
public sealed record InstallToolPackageRequest(string RepositoryId, string Id, string? Version, string? Platform);
