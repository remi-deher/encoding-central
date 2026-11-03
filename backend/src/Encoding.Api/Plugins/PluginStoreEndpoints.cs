using System.Globalization;
using System.Text.Json;
using Encoding.Api.Flows;
using Encoding.Api.Infrastructure;
using Encoding.Api.Auth;
using Encoding.Api.Resources;
using Encoding.Contracts.Flows;
using Encoding.Core.Plugins;

namespace Encoding.Api.Plugins;

/// <summary>Store de plugins (0063) : dépôts, paquets, installation, import d'archive et distribution aux workers.</summary>
public static class PluginStoreEndpoints
{
    public static void MapPluginStoreEndpoints(this WebApplication app)
    {
        var store = app.MapGroup("/api/v1/store").WithTags("Store");
        MapRepositoryRoutes(store);
        MapCatalogRoutes(store);
        MapInstallRoutes(store);
        MapImportAndDistributionRoutes(store);
    }

    /// <summary>Dépôts : liste, ajout, synchronisation, activation et retrait.</summary>
    private static void MapRepositoryRoutes(RouteGroupBuilder store)
    {
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
    }

    /// <summary>Catalogue : paquets, outils, ressources et modèles proposés par les dépôts.</summary>
    private static void MapCatalogRoutes(RouteGroupBuilder store)
    {
        store.MapGet("/packages", async (PluginRepositoryService repositories, PluginStoreService plugins, ToolStoreService tools, ResourceRepository resources, FlowRepository flows, CancellationToken ct) =>
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
            var installedResources = (await resources.ListAsync(ct)).ToDictionary(item => item.Id);
            var installedFlows = (await flows.ListAsync(ct)).Where(item => item.Version > 0).ToDictionary(item => item.Id);
            var seenCatalog = new HashSet<string>(StringComparer.Ordinal);
            foreach (var repository in repositories.List().Where(item => item.Enabled))
            {
                foreach (var resource in repository.Resources ?? [])
                {
                    if (!seenCatalog.Add("resource:" + resource.Id.ToString("D"))) continue;
                    installedResources.TryGetValue(resource.Id, out var current);
                    result.Add(new
                    {
                        Id = resource.Id.ToString("D"), resource.Name, Kind = "resource", Category = resource.Kind,
                        CategoryLabel = resource.Kind switch { "font" => "Police", "subtitle-profile" => "Profil de sous-titres", "subtitle" => "Sous-titre", _ => resource.Kind },
                        Version = resource.LatestVersion.ToString(CultureInfo.InvariantCulture),
                        InstalledVersion = current is null ? null : current.LatestVersion.ToString(CultureInfo.InvariantCulture),
                        HasUpdate = current is not null && resource.LatestVersion > current.LatestVersion,
                        Author = "", Description = resource.Summary, RequiredTools = Array.Empty<string>(), RepositoryId = repository.Id, Icon = "📄"
                    });
                }
                foreach (var flow in repository.Flows ?? [])
                {
                    if (!seenCatalog.Add("flow:" + flow.Id.ToString("D"))) continue;
                    installedFlows.TryGetValue(flow.Id, out var current);
                    result.Add(new
                    {
                        Id = flow.Id.ToString("D"), flow.Name, Kind = "flow", Category = "flow", CategoryLabel = "Flow",
                        Version = flow.LatestVersion.ToString(CultureInfo.InvariantCulture),
                        InstalledVersion = current is null ? null : current.Version.ToString(CultureInfo.InvariantCulture),
                        HasUpdate = current is not null && flow.LatestVersion > current.Version,
                        Author = "", Description = flow.Summary, RequiredTools = Array.Empty<string>(), RepositoryId = repository.Id, Icon = "↝"
                    });
                }
            }
            return Results.Ok(result);
        }).AllowViewer().WithName("ListStorePackages");
    }

    /// <summary>Installation de paquets, d'outils, de ressources et de flows du catalogue.</summary>
    private static void MapInstallRoutes(RouteGroupBuilder store)
    {
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

        store.MapPost("/resources/install", async (InstallCatalogResourceRequest request, PluginRepositoryService repositories, ResourceBlobStore blobs, ResourceRepository resources, CancellationToken ct) =>
        {
            try
            {
                if (!Guid.TryParse(request.Id, out var id)) return Results.UnprocessableEntity(new { title = "Identifiant de ressource invalide." });
                var (resource, version, bytes) = await repositories.DownloadResourceAsync(request.RepositoryId, id, request.Version, ct);
                await using var stream = new MemoryStream(bytes);
                var file = await blobs.PutAsync(stream, version.FileName, resource.Kind, ct);
                if (!string.Equals(file.Sha256, version.Sha256, StringComparison.OrdinalIgnoreCase))
                    return Results.UnprocessableEntity(new { title = "L'empreinte calculée après contrôle du type ne correspond pas au dépôt." });
                var reference = new ResourceFileReference(file.Name, file.Sha256);
                var existing = await resources.GetAsync(id, ct);
                if (existing is null)
                    return Results.Ok(await resources.CreateAsync(id, resource.Kind, resource.Name, resource.Summary, [reference], ct));
                if (existing.Summary.Kind != resource.Kind)
                    return Results.Conflict(new { title = "Une ressource porte déjà cet identifiant avec un autre type." });
                var have = existing.Versions.FirstOrDefault(item => item.Version == version.Version);
                if (have is not null)
                    return have.Files.Count == 1 && have.Files[0].Sha256 == file.Sha256
                        ? Results.Ok(existing)
                        : Results.Conflict(new { title = "Cette version est déjà publiée avec un autre contenu." });
                if (version.Version != existing.Summary.LatestVersion + 1)
                    return Results.Conflict(new { title = "La version du dépôt ne suit pas la dernière version du catalogue." });
                await resources.AddVersionAsync(id, [reference], ct);
                return Results.Ok(await resources.GetAsync(id, ct));
            }
            catch (PluginRepositoryException exception) { return Results.UnprocessableEntity(new { title = exception.Message }); }
            catch (ResourceException exception) { return Results.UnprocessableEntity(new { title = exception.Message }); }
        }).WithName("InstallStoreResource");

        store.MapPost("/flows/install", async (InstallCatalogFlowRequest request, PluginRepositoryService repositories, FlowRepository flows, FlowDefinitionValidator validator, FlowComponentRegistry registry, CancellationToken ct) =>
        {
            try
            {
                if (!Guid.TryParse(request.Id, out var id)) return Results.UnprocessableEntity(new { title = "Identifiant de flow invalide." });
                var (flow, version, bytes) = await repositories.DownloadFlowAsync(request.RepositoryId, id, request.Version, ct);
                JsonDocument document;
                try { document = JsonDocument.Parse(bytes); }
                catch (JsonException) { return Results.UnprocessableEntity(new { title = "Le flow du dépôt n'est pas un JSON valide." }); }
                using (document)
                {
                    var root = document.RootElement;
                    var definitionElement = root.TryGetProperty("definition", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : root;
                    if (definitionElement.ValueKind != JsonValueKind.Object || !definitionElement.TryGetProperty("id", out var declared) || declared.ValueKind != JsonValueKind.String
                        || !Guid.TryParse(declared.GetString(), out var declaredId) || declaredId != flow.Id)
                        return Results.UnprocessableEntity(new { title = "L'identifiant du flow ne correspond pas à l'entrée du dépôt." });
                    var definition = FlowDefinitionParser.Parse(definitionElement, registry);
                    if (definition.Id != flow.Id || definition.Version != version.Version)
                        return Results.UnprocessableEntity(new { title = "La version du flow ne correspond pas à l'entrée du dépôt." });
                    var validation = validator.Validate(definition);
                    if (!validation.IsValid) return Results.UnprocessableEntity(validation);
                    var current = (await flows.ListAsync(ct)).FirstOrDefault(item => item.Id == flow.Id && item.Version > 0);
                    var expected = current?.Version ?? 0;
                    if (expected == 0 && definition.Version != 1)
                        return Results.Conflict(new { title = "La première version publiée d'un flow importé est 1." });
                    if (expected > 0 && definition.Version != expected && definition.Version != expected + 1)
                        return Results.Conflict(new { title = "La version du dépôt ne suit pas la dernière version publiée." });
                    var saved = await flows.SaveAsync(new SaveFlowRequest(expected, definition), ct);
                    return saved is not null
                        ? Results.Ok(saved)
                        : Results.Conflict(new { title = "Ce flow existe déjà avec un autre contenu." });
                }
            }
            catch (PluginRepositoryException exception) { return Results.UnprocessableEntity(new { title = exception.Message }); }
        }).WithName("InstallStoreFlow");
    }

    /// <summary>Import d'archive, désinstallation, liste des éléments installés et téléchargement par les workers.</summary>
    private static void MapImportAndDistributionRoutes(RouteGroupBuilder store)
    {
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

        // Distribution aux workers, authentifiés par le jeton de leur worker (lecture seule).
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
        PackagesCount = item.Packages.Count + (item.Tools?.Count ?? 0) + (item.Resources?.Count ?? 0) + (item.Flows?.Count ?? 0), UpdatedAt = item.LastSyncAt, item.LastError
    };
}

public sealed record AddRepositoryRequest(string? Url);
public sealed record SetRepositoryEnabledRequest(bool Enabled);
public sealed record InstallPackageRequest(string RepositoryId, string Id, string? Version);
public sealed record InstallToolPackageRequest(string RepositoryId, string Id, string? Version, string? Platform);
public sealed record InstallCatalogResourceRequest(string RepositoryId, string Id, int? Version);
public sealed record InstallCatalogFlowRequest(string RepositoryId, string Id, int? Version);
