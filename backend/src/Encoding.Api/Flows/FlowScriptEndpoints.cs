using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Contracts.Flows;
using Microsoft.AspNetCore.Http.Features;

namespace Encoding.Api.Flows;

public static class FlowScriptEndpoints
{
    public static void MapFlowScriptEndpoints(this WebApplication app)
    {
        var scripts = app.MapGroup("/api/v1/scripts").WithTags("Scripts");
        scripts.MapGet("", async (FlowScriptRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.ListAsync(ct))).WithName("ListFlowScripts");
        scripts.MapPost("", async (CreateFlowScriptRequest request, FlowScriptRepository repository, CancellationToken ct) =>
        {
            var errors = Validate(request.Draft, allowEmptyCode: true);
            if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);
            return await UniqueConflict.GuardAsync(async () => {
                var id = Guid.NewGuid();
                var script = await repository.CreateAsync(id, request.Draft, ct);
                return Results.Created($"/api/v1/scripts/{id:D}", script);
            }, () => Results.Conflict(new { title = "Un script porte déjà ce nom." }));
        }).WithName("CreateFlowScript");
        scripts.MapGet("/{id:guid}", async (Guid id, FlowScriptRepository repository, CancellationToken ct) =>
            await repository.GetAsync(id, ct) is { } script ? Results.Ok(script) : Results.NotFound()).WithName("GetFlowScript");
        scripts.MapPut("/{id:guid}/draft", async (Guid id, SaveFlowScriptDraftRequest request, FlowScriptRepository repository, CancellationToken ct) =>
        {
            var errors = Validate(request.Draft, allowEmptyCode: true);
            if (request.ExpectedRevision < 1) errors["expectedRevision"] = ["Révision de brouillon invalide."];
            if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);
            return await UniqueConflict.GuardAsync(async () => {
                var saved = await repository.SaveDraftAsync(id, request, ct);
                if (saved is not null) return Results.Ok(saved);
                return await repository.GetAsync(id, ct) is null
                    ? Results.NotFound()
                    : Results.Conflict(new { title = "Le brouillon a été modifié ailleurs. Rechargez-le avant de continuer." });
            }, () => Results.Conflict(new { title = "Un script porte déjà ce nom." }));
        }).WithName("SaveFlowScriptDraft");
        scripts.MapPost("/{id:guid}/publish", async (Guid id, PublishFlowScriptRequest request, FlowScriptRepository repository, CancellationToken ct) =>
        {
            if (request.ExpectedRevision < 1) return Results.BadRequest(new { title = "Révision de brouillon invalide." });
            var current = await repository.GetAsync(id, ct);
            if (current is null) return Results.NotFound();
            if (current.Script.DraftRevision != request.ExpectedRevision)
                return Results.Conflict(new { title = "Le brouillon a été modifié ailleurs. Rechargez-le avant de publier." });
            if (string.IsNullOrWhiteSpace(current.Draft.Code))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["code"] = ["Le script doit contenir du code avant sa publication."] });
            var published = await repository.PublishAsync(id, request.ExpectedRevision, ct);
            return published is null
                ? Results.Conflict(new { title = "Le brouillon a changé pendant la publication." })
                : Results.Created($"/api/v1/scripts/{id:D}/versions/{published.Version}", published);
        }).WithName("PublishFlowScript");
        scripts.MapGet("/{id:guid}/versions/{version:int}", async (Guid id, int version, FlowScriptRepository repository, CancellationToken ct) =>
            version < 1 ? Results.BadRequest() : await repository.GetVersionAsync(id, version, ct) is { } published ? Results.Ok(published) : Results.NotFound())
            .WithName("GetFlowScriptVersion");
        // Import / export : mêmes règles d'écriture que le reste des scripts (admin seulement, donc POST, y compris pour l'export).
        // Rien n'est exécuté ; un nom déjà pris crée une nouvelle révision de brouillon et ne touche jamais une version publiée.
        scripts.MapPost("/import", async (HttpRequest request, string name, FlowScriptRepository repository, CancellationToken ct) =>
        {
            if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = FlowScriptFiles.MaxBytes + 1024;
            if (request.ContentLength > FlowScriptFiles.MaxBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            var language = FlowScriptFiles.LanguageOf(name);
            if (language is null) return Results.UnprocessableEntity(new { title = "Seuls les fichiers .py (Python) et .js (JavaScript) sont acceptés." });
            using var buffer = new MemoryStream();
            var chunk = new byte[16384];
            int read;
            while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > FlowScriptFiles.MaxBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                buffer.Write(chunk, 0, read);
            }
            string code;
            try { code = new System.Text.UTF8Encoding(false, true).GetString(buffer.ToArray()).TrimStart('\uFEFF'); }
            catch (System.Text.DecoderFallbackException) { return Results.UnprocessableEntity(new { title = "Le fichier n'est pas un texte UTF-8 valide." }); }
            if (string.IsNullOrWhiteSpace(code)) return Results.UnprocessableEntity(new { title = "Le fichier est vide." });
            var scriptName = FlowScriptFiles.NameOf(name);
            return await UniqueConflict.GuardAsync(async () => {
                var existing = (await repository.ListAsync(ct)).FirstOrDefault(item => string.Equals(item.DraftName, scriptName, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    var draft = new FlowScriptDraft(existing.DraftName, language, existing.DraftDescription, code);
                    var saved = await repository.SaveDraftAsync(existing.Id, new(existing.DraftRevision, draft), ct);
                    return saved is null
                        ? Results.Conflict(new { title = "Le brouillon a été modifié ailleurs. Relancez l'import." })
                        : Results.Ok(new { created = false, script = saved });
                }
                var created = await repository.CreateAsync(Guid.NewGuid(), new(scriptName, language, "Importé depuis " + Path.GetFileName(name), code), ct);
                return Results.Created($"/api/v1/scripts/{created!.Script.Id:D}", new { created = true, script = created });
            }, () => Results.Conflict(new { title = "Un script porte déjà ce nom." }));
        }).WithName("ImportFlowScript");
        scripts.MapPost("/{id:guid}/versions/{version:int}/export", async (Guid id, int version, FlowScriptRepository repository, CancellationToken ct) =>
            version < 1 ? Results.BadRequest() : await repository.GetVersionAsync(id, version, ct) is { } item
                ? Results.File(System.Text.Encoding.UTF8.GetBytes(item.Code), "text/plain; charset=utf-8", FlowScriptFiles.FileName(item.Name, item.Language))
                : Results.NotFound()).WithName("ExportFlowScriptVersion");
        scripts.MapPost("/export", async (ExportFlowScriptsRequest request, FlowScriptRepository repository, CancellationToken ct) =>
        {
            if (request.Items is not { Count: > 0 and <= 200 }) return Results.BadRequest(new { title = "Choisissez entre 1 et 200 scripts." });
            using var output = new MemoryStream();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var archive = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create, true))
                foreach (var item in request.Items)
                {
                    var script = await repository.GetAsync(item.Id, ct);
                    if (script is null) return Results.NotFound(new { title = "Script introuvable." });
                    var version = item.Version ?? script.Script.PublishedVersion;
                    var published = version < 1 ? null : await repository.GetVersionAsync(item.Id, version, ct);
                    if (published is null) return Results.UnprocessableEntity(new { title = $"« {script.Script.DraftName} » n'a pas de version {(item.Version?.ToString() ?? "publiée")}." });
                    var entryName = FlowScriptFiles.FileName(published.Name, published.Language);
                    for (var n = 2; !used.Add(entryName); n++) entryName = FlowScriptFiles.FileName($"{published.Name} ({n})", published.Language);
                    var entry = archive.CreateEntry(entryName);
                    await using var stream = entry.Open();
                    await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(published.Code), ct);
                }
            return Results.File(output.ToArray(), "application/zip", "scripts.zip");
        }).WithName("ExportFlowScripts");
        scripts.MapDelete("/{id:guid}", async (Guid id, FlowScriptRepository repository, CancellationToken ct) =>
            await repository.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound())
            .WithName("DeleteFlowScript");
    }

    private static Dictionary<string, string[]> Validate(FlowScriptDraft? draft, bool allowEmptyCode)
    {
        var errors = new Dictionary<string, string[]>();
        if (draft is null) return new() { ["draft"] = ["Le brouillon est obligatoire."] };
        if (string.IsNullOrWhiteSpace(draft.Name) || draft.Name.Trim().Length > 128)
            errors["name"] = ["Le nom est obligatoire et limité à 128 caractères."];
        if (draft.Language is not ("javascript" or "python")) errors["language"] = ["Choisissez JavaScript ou Python."];
        if (draft.Description is null || draft.Description.Length > 1000) errors["description"] = ["La description est limitée à 1 000 caractères."];
        if (draft.Code is null || draft.Code.Length > 200000 || (!allowEmptyCode && string.IsNullOrWhiteSpace(draft.Code)))
            errors["code"] = ["Le code est obligatoire à la publication et limité à 200 000 caractères."];
        return errors;
    }
}

public sealed record ExportFlowScriptItem(Guid Id, int? Version);
public sealed record ExportFlowScriptsRequest(IReadOnlyList<ExportFlowScriptItem>? Items);

/// <summary>Règles de fichier des scripts importés et exportés : extension .py ou .js, UTF-8, 1 Mo au plus.</summary>
public static class FlowScriptFiles
{
    public const int MaxBytes = 1024 * 1024;

    public static string? LanguageOf(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch { ".py" => "python", ".js" => "javascript", _ => null };

    public static string NameOf(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(Path.GetFileName(fileName)).Trim();
        return stem.Length > 128 ? stem[..128] : stem.Length == 0 ? "script" : stem;
    }

    public static string FileName(string name, string language)
    {
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' ? '_' : c)).Trim();
        return (safe.Length == 0 ? "script" : safe) + (language == "python" ? ".py" : ".js");
    }
}
