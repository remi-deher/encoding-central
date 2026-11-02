using System.Security.Claims;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Encoding.Api.Executions;
using Encoding.Api.Flows;
using Encoding.Api.Nodes;
using Encoding.Api.Mcp;
using Encoding.Api.Libraries;
using Encoding.Api.Infrastructure;
using Encoding.Api.Cluster;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Libraries;
using Encoding.Contracts.Nodes;
using Encoding.Contracts.Tools;

namespace Encoding.Api.Tools;

public sealed class AiAssistantActionsService(AiAssistantSupportService support, AiAssistantConversationsService conversations)
{

    public async Task<AiAssistantPreparedAction?> PrepareDeleteFlowAsync(AiAssistantPrepareActionRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        EnsureAdmin(user);
        if (request.ResourceId == Guid.Empty || request.ExpectedVersion < 1)
            throw new ArgumentException("Flow ou version invalide.");
        var current = (await support.Flows.ListAsync(ct)).FirstOrDefault(flow => flow.Id == request.ResourceId);
        if (current is null) return null;
        if (current.Version != request.ExpectedVersion)
            throw new InvalidOperationException("Le flow a changé. Actualisez son état avant de préparer la suppression.");
        return PrepareAction(user, "delete-flow", request.ResourceId, request.ExpectedVersion, null,
            $"Supprimer le flow « {current.Name} », version {current.Version} ? Les flows assignés ou utilisés par une exécution active seront refusés.");
    }

    public async Task<AiAssistantPreparedAction?> PreparePublishFlowAsync(AiAssistantPrepareActionRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        EnsureAdmin(user);
        if (request.ResourceId == Guid.Empty || request.ExpectedVersion < 0)
            throw new ArgumentException("Flow ou version publiée invalide.");
        var draft = await support.Flows.DraftAsync(request.ResourceId, ct);
        if (draft is null) return null;
        if (draft.PublishedVersion != request.ExpectedVersion)
            throw new InvalidOperationException("La version publiée du flow a changé. Actualisez le contexte.");
        var hash = HashJson(new { draft.DraftRevision, draft.PublishedVersion, draft.Definition });
        return PrepareAction(user, "publish-flow", request.ResourceId, request.ExpectedVersion, hash,
            $"Publier le brouillon « {draft.Definition.Name} » (révision {draft.DraftRevision}) comme version {draft.PublishedVersion + 1} ?");
    }

    public async Task<AiAssistantPreparedAction?> PrepareStartLibraryAsync(AiAssistantPrepareActionRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        EnsureAdmin(user);
        if (request.ResourceId == Guid.Empty || request.ExpectedVersion < 0)
            throw new ArgumentException("Bibliothèque ou révision d’affectation invalide.");
        var workflow = await support.LibraryWorkflows.GetAsync(request.ResourceId, null, 10000, 0, ct);
        var library = (await support.Libraries.ListAsync(ct)).FirstOrDefault(item => item.Id == request.ResourceId);
        if (library is null || workflow.Assignment is null || !library.IsActive) return null;
        if (workflow.Revision != request.ExpectedVersion)
            throw new InvalidOperationException("L’affectation de la bibliothèque a changé. Actualisez le contexte.");
        if (workflow.TotalFiles > workflow.Files.Count)
            throw new InvalidOperationException("La bibliothèque contient plus de 10 000 fichiers détectés. Réduisez la sélection avant de lancer le traitement.");
        var ids = workflow.Files.Select(file => file.Id).Order().ToArray();
        if (ids.Length == 0) throw new InvalidOperationException("Aucun fichier détecté ne peut être mis en file.");
        var hash = HashJson(new { workflow.Revision, Assignment = workflow.Assignment, FileIds = ids });
        return PrepareAction(user, "start-library", request.ResourceId, workflow.Revision, hash,
            $"Mettre en file les {ids.Length} fichiers actuellement détectés dans « {library.Name} » avec le flow « {workflow.Assignment.Name} », version {workflow.Assignment.Version} ?");
    }

    public async Task<AiAssistantPreparedAction?> PrepareCancelExecutionAsync(AiAssistantPrepareActionRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        EnsureAdmin(user);
        if (request.ResourceId == Guid.Empty || request.ExpectedVersion < 0)
            throw new ArgumentException("Exécution ou révision invalide.");
        var execution = await support.Executions.GetAsync(request.ResourceId, ct);
        if (execution is null) return null;
        if (execution.Revision != request.ExpectedVersion)
            throw new InvalidOperationException("L’exécution a changé. Actualisez son état avant de préparer l’annulation.");
        if (execution.State is not (ExecutionStates.Queued or ExecutionStates.Running))
            throw new InvalidOperationException("Cette exécution n’est plus annulable.");
        return PrepareAction(user, "cancel-execution", request.ResourceId, request.ExpectedVersion, null,
            $"Demander l’annulation de « {execution.Media} » ? État : {execution.State}, révision {execution.Revision}.");
    }

    public async Task<AiAssistantPreparedAction?> PrepareAssignLibraryFlowAsync(
        AiAssistantActionCall request, ClaimsPrincipal user, CancellationToken ct)
    {
        EnsureAdmin(user);
        if (request.Name != "assign-library-flow" || request.ResourceId == Guid.Empty
            || request.ExpectedVersion < 0 || request.SecondaryResourceId is not { } flowId
            || request.SecondaryVersion is not > 0)
            throw new ArgumentException("Bibliothèque, révision, flow ou version publiée invalide.");
        var workflow = await support.LibraryWorkflows.GetAsync(request.ResourceId, null, 1, 0, ct);
        var library = (await support.Libraries.ListAsync(ct)).FirstOrDefault(item => item.Id == request.ResourceId);
        var flow = await support.Flows.GetAsync(flowId, request.SecondaryVersion, ct);
        if (library is null || !library.IsActive || flow is null) return null;
        if (workflow.Revision != request.ExpectedVersion)
            throw new InvalidOperationException("L’affectation de la bibliothèque a changé. Actualisez le contexte.");
        return PrepareAction(user, request.Name, request.ResourceId, request.ExpectedVersion,
            HashJson(new { FlowId = flowId, FlowVersion = request.SecondaryVersion }),
            $"Affecter le flow « {flow.Definition.Name} », version publiée {flow.Definition.Version}, à la bibliothèque « {library.Name} » ?",
            flowId, request.SecondaryVersion);
    }

    public async Task<AiAssistantPreparedAction?> PrepareCreateFlowTemplateAsync(
        AiAssistantActionCall request, ClaimsPrincipal user, CancellationToken ct)
    {
        EnsureAdmin(user);
        if (request.Name != "create-flow-template" || request.ResourceId == Guid.Empty
            || request.ExpectedVersion < 1 || string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 128)
            throw new ArgumentException("Flow publié, version ou nom de modèle invalide.");
        var flow = await support.Flows.GetAsync(request.ResourceId, request.ExpectedVersion, ct);
        if (flow is null) return null;
        var existing = await support.FlowTemplates.ListAsync(ct);
        if (existing.Any(template => string.Equals(template.Name, request.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Un modèle de flow porte déjà ce nom.");
        return PrepareAction(user, request.Name, request.ResourceId, request.ExpectedVersion,
            HashJson(new { Name = request.Text.Trim(), Flow = flow.Definition }),
            $"Créer le modèle « {request.Text.Trim()} » à partir du flow publié « {flow.Definition.Name} », version {flow.Definition.Version} ?",
            text: request.Text.Trim());
    }

    public async Task<bool> ConfirmDeleteFlowAsync(AiAssistantConfirmActionRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        EnsureAdmin(user);
        var action = ReadAction(request, user, "delete-flow");
        var (result, reason) = await support.Flows.DeleteFlowAsync(action.ResourceId, ct, action.ExpectedVersion);
        if (result == FlowDeleteResult.Conflict) throw new InvalidOperationException(reason ?? "La suppression du flow est en conflit.");
        if (result == FlowDeleteResult.Deleted) await RecordConfirmedActionAsync(action.ActionId, "flow-delete", user, ct);
        return result == FlowDeleteResult.Deleted;
    }

    public async Task<bool> ConfirmPublishFlowAsync(AiAssistantConfirmActionRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        EnsureAdmin(user);
        var action = ReadAction(request, user, "publish-flow");
        var draft = await support.Flows.DraftAsync(action.ResourceId, ct);
        if (draft is null || draft.PublishedVersion != action.ExpectedVersion
            || HashJson(new { draft.DraftRevision, draft.PublishedVersion, draft.Definition }) != action.ParameterHash)
            throw new InvalidOperationException("Le brouillon ou la version publiée a changé après la confirmation.");
        var definition = draft.Definition with { Version = draft.PublishedVersion + 1 };
        var saved = await support.Flows.SaveAsync(new SaveFlowRequest(draft.PublishedVersion, definition, draft.DraftRevision), ct);
        if (saved is null) throw new InvalidOperationException("Le flow a changé pendant la publication. Actualisez son état.");
        await RecordConfirmedActionAsync(action.ActionId, "flow-publish", user, ct);
        return true;
    }

    public async Task<bool> ConfirmCancelExecutionAsync(AiAssistantConfirmActionRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        EnsureAdmin(user);
        var action = ReadAction(request, user, "cancel-execution");
        var execution = await support.Executions.GetAsync(action.ResourceId, ct);
        if (execution is null) return false;
        if (execution.Revision != action.ExpectedVersion)
            throw new InvalidOperationException("L’exécution a changé après la confirmation. Actualisez son état.");
        var cancelled = await support.Executions.CancelAsync(action.ResourceId, ct, action.ExpectedVersion);
        if (cancelled) await RecordConfirmedActionAsync(action.ActionId, "execution-cancel", user, ct);
        return cancelled;
    }

    public async Task<bool> ConfirmAssignLibraryFlowAsync(AiAssistantConfirmActionRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        EnsureAdmin(user);
        var action = ReadAction(request, user, "assign-library-flow");
        if (action.RelatedResourceId is not { } flowId || flowId == Guid.Empty
            || action.RelatedVersion is not > 0
            || HashJson(new { FlowId = flowId, FlowVersion = action.RelatedVersion }) != action.ParameterHash)
            throw new ArgumentException("Détails de l’affectation invalides.");
        var workflow = await support.LibraryWorkflows.GetAsync(action.ResourceId, null, 1, 0, ct);
        var library = (await support.Libraries.ListAsync(ct)).FirstOrDefault(item => item.Id == action.ResourceId);
        if (library is null || !library.IsActive || workflow.Revision != action.ExpectedVersion)
            throw new InvalidOperationException("La bibliothèque ou son affectation a changé après la confirmation.");
        var flow = await support.Flows.GetAsync(flowId, action.RelatedVersion, ct);
        if (flow is null)
            throw new InvalidOperationException("Le flow publié n’est plus disponible.");
        var assigned = await support.LibraryWorkflows.AssignAsync(action.ResourceId,
            new AssignLibraryFlowRequest(flowId, action.RelatedVersion, action.ExpectedVersion), ct);
        if (!assigned) throw new InvalidOperationException("L’affectation a changé pendant la confirmation.");
        await RecordConfirmedActionAsync(action.ActionId, "library-flow-assign", user, ct);
        return true;
    }

    public async Task<bool> ConfirmCreateFlowTemplateAsync(AiAssistantConfirmActionRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        EnsureAdmin(user);
        var action = ReadAction(request, user, "create-flow-template");
        if (string.IsNullOrWhiteSpace(action.Text) || action.Text.Length > 128)
            throw new ArgumentException("Nom de modèle invalide.");
        var flow = await support.Flows.GetAsync(action.ResourceId, action.ExpectedVersion, ct);
        if (flow is null || HashJson(new { Name = action.Text, Flow = flow.Definition }) != action.ParameterHash)
            throw new InvalidOperationException("Le flow publié a changé après la confirmation.");
        if (await support.FlowTemplates.GetAsync(action.ActionId, ct) is not null) return true;
        var created = await support.FlowTemplates.CreateFromFlowAsync(action.ActionId,
            new CreateFlowTemplateFromFlowRequest(action.ResourceId, action.ExpectedVersion, action.Text,
                $"Modèle créé depuis le flow publié « {flow.Definition.Name} » (version {flow.Definition.Version})."), ct);
        if (created is null) throw new InvalidOperationException("La version publiée du flow n’est plus disponible.");
        await RecordConfirmedActionAsync(action.ActionId, "flow-template-create", user, ct);
        return true;
    }

    public async Task<bool> ConfirmBusinessOperationAsync(AiAssistantConfirmActionRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        EnsureAdmin(user);
        var action = ReadAction(request, user, "business-operation");
        if (action.CoordinatorId is null || action.CoordinatorId == Guid.Empty)
            throw new ArgumentException("Ce jeton de confirmation ne contient pas de coordinateur valide. Préparez à nouveau l’action.");
        if (action.CoordinatorId != support.Transport.LocalId)
        {
            var ownerId = Guid.Parse(AiAssistantSupportService.OwnerId(user));
            var response = await support.Transport.CallNodeAsync<AiAssistantForwardConfirmResponse>(action.CoordinatorId.Value,
                "assistant-business-operation", new AiAssistantForwardConfirm(ownerId, true, request), TimeSpan.FromMinutes(2), ct);
            if (response.InvalidRequest) throw new ArgumentException(response.Error ?? "Jeton de confirmation invalide.");
            if (!response.Applied) throw new InvalidOperationException(response.Error ?? "L’opération n’a pas pu être appliquée.");
            return true;
        }
        var gate = support.actionGates.GetOrAdd(action.ActionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try { return await ConfirmBusinessOperationCoreAsync(request, user, action, ct); }
        finally { gate.Release(); }
    }

    public async Task<bool> ConfirmBusinessOperationCoreAsync(AiAssistantConfirmActionRequest request,
        ClaimsPrincipal user, ProtectedAssistantAction action, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(action.OperationName) || action.OperationArguments is null)
            throw new ArgumentException("L’opération proposée est incomplète.");
        var operation = BusinessOperationCatalog.FindTool(action.OperationName);
        using var document = JsonDocument.Parse(action.OperationArguments);
        if (operation is not { RequiresAdministrator: true }
            || !BusinessOperationCatalog.ValidateArguments(action.OperationName, document.RootElement)
            || HashJson(new { Name = action.OperationName, Arguments = action.OperationArguments }) != action.ParameterHash)
            throw new InvalidOperationException("L’opération ou ses paramètres ne sont plus valides.");
        var receiptKey = "assistant/business-operation/" + action.ActionId.ToString("N");
        var receipt = await support.Store.ReadAsync<string>(receiptKey, ct);
        if (receipt == "completed") return true;
        if (receipt == "started")
            throw new InvalidOperationException("Le résultat de cette opération est incertain après une interruption. Elle ne sera pas relancée automatiquement.");
        await support.Store.SaveAsync(receiptKey, "started", ct);
        var result = await support.Mcp.ExecuteOperationAsync(action.OperationName, document.RootElement, isAdmin: true, ct);
        if (result.IsError) throw new InvalidOperationException(result.Content.FirstOrDefault()?.Text ?? "L’opération a échoué.");
        await support.Store.SaveAsync(receiptKey, "completed", ct);
        await RecordConfirmedActionAsync(action.ActionId, action.OperationName, user, ct);
        return true;
    }

    public async Task<(int Queued, int Skipped)?> ConfirmStartLibraryAsync(AiAssistantConfirmActionRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        EnsureAdmin(user);
        var action = ReadAction(request, user, "start-library");
        var workflow = await support.LibraryWorkflows.GetAsync(action.ResourceId, null, 10000, 0, ct);
        var library = (await support.Libraries.ListAsync(ct)).FirstOrDefault(item => item.Id == action.ResourceId);
        if (library is null || !library.IsActive || workflow.Assignment is null || workflow.Revision != action.ExpectedVersion)
            throw new InvalidOperationException("L’affectation ou l’état de la bibliothèque a changé après la confirmation.");
        if (workflow.TotalFiles > workflow.Files.Count) throw new InvalidOperationException("La liste des fichiers dépasse la limite prise en charge.");
        var ids = workflow.Files.Select(file => file.Id).Order().ToArray();
        if (HashJson(new { workflow.Revision, Assignment = workflow.Assignment, FileIds = ids }) != action.ParameterHash)
            throw new InvalidOperationException("La sélection des fichiers a changé après la confirmation.");
        var result = await support.LibraryWorkflows.EnqueueConfirmedAsync(action.ResourceId, ids, action.ExpectedVersion, action.ActionId, ct)
            ?? throw new InvalidOperationException("Le flow, la révision ou la sélection n’est plus disponible.");
        await RecordConfirmedActionAsync(action.ActionId, "library-start", user, ct);
        return result;
    }

    public Task RecordConfirmedActionAsync(Guid actionId, string operation, ClaimsPrincipal user, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        return conversations.RecordOperationActivityAsync(user, actionId, operation, "assistant", "succeeded", now, null, ct);
    }

    public sealed record ProtectedAssistantAction(string OwnerId, string Action, Guid ResourceId, int ExpectedVersion,
        Guid ActionId, string? ParameterHash, DateTimeOffset ExpiresAt,
        Guid? RelatedResourceId = null, int? RelatedVersion = null, string? Text = null,
        string? OperationName = null, string? OperationArguments = null, Guid? CoordinatorId = null);

    private AiAssistantPreparedAction PrepareAction(ClaimsPrincipal user, string action, Guid resourceId, int expectedVersion,
        string? parameterHash, string summary, Guid? relatedResourceId = null, int? relatedVersion = null, string? text = null,
        string? operationName = null, string? operationArguments = null, Guid? coordinatorId = null)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var payload = new ProtectedAssistantAction(AiAssistantSupportService.OwnerId(user), action, resourceId, expectedVersion, Guid.NewGuid(), parameterHash, expiresAt,
            relatedResourceId, relatedVersion, text, operationName, operationArguments, coordinatorId);
        return new(support.actionProtector.Protect(JsonSerializer.Serialize(payload)), summary, expiresAt);
    }

    public static string HashJson<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    private ProtectedAssistantAction ReadAction(AiAssistantConfirmActionRequest request, ClaimsPrincipal user, string expectedAction)
    {
        if (string.IsNullOrWhiteSpace(request.ConfirmationToken)) throw new ArgumentException("Le jeton de confirmation est requis.");
        ProtectedAssistantAction? action;
        try { action = JsonSerializer.Deserialize<ProtectedAssistantAction>(support.actionProtector.Unprotect(request.ConfirmationToken)); }
        catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException)
        { throw new ArgumentException("Le jeton de confirmation est invalide ou expiré."); }
        if (action is null || action.OwnerId != AiAssistantSupportService.OwnerId(user) || action.Action != expectedAction || action.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new ArgumentException("Le jeton de confirmation est invalide, expiré ou lié à une autre action.");
        return action;
    }

    public static void EnsureAdmin(ClaimsPrincipal user)
    {
        if (!user.IsInRole("admin")) throw new UnauthorizedAccessException("Cette action nécessite le rôle administrateur.");
    }

    public Task<AiAssistantPreparedAction?> PrepareAssistantActionAsync(
        AiAssistantActionCall action,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        var request = new AiAssistantPrepareActionRequest(action.ResourceId, action.ExpectedVersion);
        return action.Name switch
        {
            "publish-flow" => PreparePublishFlowAsync(request, user, ct),
            "delete-flow" => PrepareDeleteFlowAsync(request, user, ct),
            "start-library" => PrepareStartLibraryAsync(request, user, ct),
            "cancel-execution" => PrepareCancelExecutionAsync(request, user, ct),
            "assign-library-flow" => PrepareAssignLibraryFlowAsync(action, user, ct),
            "create-flow-template" => PrepareCreateFlowTemplateAsync(action, user, ct),
            _ => PrepareBusinessOperationAsync(action, user),
        };
    }

    public Task<AiAssistantPreparedAction?> PrepareBusinessOperationAsync(AiAssistantActionCall action, ClaimsPrincipal user)
    {
        EnsureAdmin(user);
        if (BusinessOperationCatalog.FindTool(action.Name) is not { RequiresAdministrator: true }
            || action.Arguments is not { ValueKind: JsonValueKind.Object } arguments
            || !BusinessOperationCatalog.ValidateArguments(action.Name, arguments))
            throw new ArgumentException("Opération d’administration ou paramètres invalides.");
        var serialized = arguments.GetRawText();
        if (serialized.Length > 200_000) throw new ArgumentException("Les paramètres de cette opération dépassent la limite de taille.");
        var hash = HashJson(new { Name = action.Name, Arguments = serialized });
        var descriptor = BusinessOperationCatalog.FindTool(action.Name)!;
        var preview = PreviewOperationArguments(arguments);
        return Task.FromResult<AiAssistantPreparedAction?>(PrepareAction(user, "business-operation", Guid.Empty, 0,
            hash, $"{descriptor.Description}\n\nParamètres proposés : {preview}",
            operationName: action.Name, operationArguments: serialized, coordinatorId: support.Transport.LocalId));
    }

    public static string PreviewOperationArguments(JsonElement arguments)
    {
        object? Sanitize(JsonElement value, string? propertyName = null)
        {
            if (propertyName is not null && System.Text.RegularExpressions.Regex.IsMatch(propertyName,
                    "key|token|secret|password|credential", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return "[masqué]";
            if (propertyName?.Equals("code", StringComparison.OrdinalIgnoreCase) == true && value.ValueKind == JsonValueKind.String)
                return $"[code de {value.GetString()?.Length ?? 0} caractères]";
            return value.ValueKind switch
            {
                JsonValueKind.Object => value.EnumerateObject().ToDictionary(property => property.Name,
                    property => Sanitize(property.Value, property.Name)),
                JsonValueKind.Array => value.EnumerateArray().Select(item => Sanitize(item)).ToArray(),
                JsonValueKind.String when value.GetString() is { Length: > 512 } text => text[..512] + "…",
                _ => value
            };
        }
        var preview = JsonSerializer.Serialize(Sanitize(arguments), AiAssistantSupportService.ContextJsonOptions);
        return preview.Length <= 2_000 ? preview : preview[..2_000] + "…";
    }

}
