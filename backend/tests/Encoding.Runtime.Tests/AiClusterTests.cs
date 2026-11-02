using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Encoding.Api.Flows;
using Encoding.Api.Infrastructure;
using Encoding.Api.Tools;
using Encoding.Api.Nodes;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Tools;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class AiClusterTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task AvailabilityUsesDisplayNamesForLocalAndRemoteNodesWithTechnicalNameFallback()
    {
        await using var mesh = await Mesh.CreateAsync();
        Assert.Equal("first", (await mesh.First.Service.LocalAsync(Ct)).Name);
        await mesh.First.LocalNode.RequestNameAsync("Salon", Ct);
        await mesh.Second.LocalNode.RequestNameAsync("Bureau", Ct);

        var view = await mesh.First.Service.ViewAsync(Ct);
        Assert.Contains(view.Nodes, node => node.NodeId == mesh.First.Identity.PeerId && node.Name == "Salon");
        Assert.Contains(view.Nodes, node => node.NodeId == mesh.Second.Identity.PeerId && node.Name == "Bureau");
    }

    [Fact]
    public async Task AnyPeerCanUseRemoteProviderAndResumeDurableApplicationContextWithoutCopyingSecrets()
    {
        await using var mesh = await Mesh.CreateAsync();
        var requestId = Guid.NewGuid();
        var first = await mesh.First.Service.GenerateAsync("openai", new("instructions", "first question", RequestId: requestId), Ct);
        Assert.True(first.Success, first.ErrorMessage);
        Assert.NotNull(first.SessionId);
        Assert.Equal(1, mesh.Factory.Inferences);
        Assert.Null(mesh.First.Settings.GetSettings().OpenAiApiKey);
        var replay = await mesh.First.NewService().GenerateAsync("openai", new("instructions", "first question", RequestId: requestId), Ct);
        Assert.Equal(first.Content, replay.Content);
        Assert.Equal(1, mesh.Factory.Inferences);
        await Assert.ThrowsAsync<ArgumentException>(() => mesh.First.Service.GenerateAsync("openai", new("instructions", "changed question", SessionId: first.SessionId, RequestId: requestId), Ct));
        var continuation = await mesh.Second.Service.GenerateAsync("openai", new("instructions", "second question", SessionId: first.SessionId), Ct);
        Assert.True(continuation.Success, continuation.ErrorMessage);
        Assert.Equal(2, mesh.Factory.Inferences);
        Assert.Contains("first question", mesh.Factory.LastPrompt);
        Assert.Contains("answer from remote model", mesh.Factory.LastPrompt);
        Assert.Contains("second question", mesh.Factory.LastPrompt);
        var events = await mesh.First.Journal.GetEventsAfterAsync(mesh.ClusterId, new Dictionary<Guid, long>(), 32, Ct);
        Assert.DoesNotContain("first question", JsonSerializer.Serialize(events));
        await mesh.Second.Journal.ApplyIncomingAsync(events, Ct);
        var checkpoint = await mesh.Second.Service.GetSessionAsync(first.SessionId!, Ct);
        Assert.NotNull(checkpoint);
        Assert.Equal(2, checkpoint.Turns.Count);
        Assert.Null(checkpoint.PendingRequest);
        Assert.DoesNotContain("private-api-key", JsonSerializer.Serialize(checkpoint));
        var fork = await mesh.Second.Service.ForkSessionAsync(first.SessionId!, Ct);
        Assert.StartsWith(mesh.Second.Identity.PeerId.ToString("N"), fork.Id);
        Assert.Equal(2, fork.Turns.Count);
        Assert.Null(fork.PendingRequest);
    }

    [Fact]
    public async Task SessionHistoryIsBoundedAndReportsPendingAndCompletedWorkWithoutPrompts()
    {
        await using var mesh = await Mesh.CreateAsync();
        Assert.Empty((await mesh.First.Service.ListSessionsAsync(0, Ct)).Items);
        var first = await mesh.First.Service.GenerateAsync("openai", new("private instructions", "private question"), Ct);
        var page = await mesh.First.Service.ListSessionsAsync(0, Ct);
        var summary = Assert.Single(page.Items);
        Assert.Equal(first.SessionId, summary.Id);
        Assert.Equal("completed", summary.Status);
        Assert.Equal(1, summary.TurnCount);
        Assert.NotNull(summary.UpdatedAt);
        Assert.Null(summary.Usage);
        Assert.DoesNotContain("private question", JsonSerializer.Serialize(page));
        Assert.DoesNotContain("private instructions", JsonSerializer.Serialize(page));
        var id = $"{mesh.First.Identity.PeerId:N}.{Guid.NewGuid():N}";
        await mesh.First.Store.SaveAsync("session/" + id, new AiSessionState(id, "openai", "system", [],
            Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1)), Ct);
        Assert.Contains((await mesh.First.Service.ListSessionsAsync(0, Ct)).Items, item => item.Id == id && item.Status == "running");
        Assert.Empty((await mesh.First.Service.ListSessionsAsync(50, Ct)).Items);
        await Assert.ThrowsAsync<ArgumentException>(() => mesh.First.Service.ListSessionsAsync(-1, Ct));
    }

    [Fact]
    public async Task LargeAssistantPromptIsAcceptedUpToTheAssistantContextLimit()
    {
        await using var mesh = await Mesh.CreateAsync();
        var large = new string('x', 150_000);
        var generated = await mesh.First.Service.GenerateAsync("openai", new("instructions", large), Ct);
        Assert.True(generated.Success, generated.ErrorMessage);

        var tooLarge = new string('x', AiClusterService.MaximumPromptCharacters + 1);
        await Assert.ThrowsAsync<ArgumentException>(() => mesh.First.Service.GenerateAsync("openai", new("instructions", tooLarge), Ct));
    }

    [Fact]
    public async Task SessionHistorySkipsTombstonesAfterExpiration()
    {
        await using var mesh = await Mesh.CreateAsync();
        var generated = await mesh.First.Service.GenerateAsync("openai", new("instructions", "question"), Ct);
        Assert.True(generated.Success, generated.ErrorMessage);

        await mesh.First.Store.DeleteAsync("session/" + generated.SessionId, Ct);

        Assert.DoesNotContain((await mesh.First.Service.ListSessionsAsync(0, Ct)).Items,
            item => item.Id == generated.SessionId);
    }

    [Fact]
    public async Task RegisterPrefixPagesUseAnExclusiveStableCursor()
    {
        await using var mesh = await Mesh.CreateAsync();
        foreach (var key in new[] { "a", "b", "c" })
            await mesh.First.Store.SaveAsync("page-test/" + key, key, Ct);

        var first = await mesh.First.Store.ReadPrefixPageAsync<string>("page-test/", null, 2, Ct);
        var second = await mesh.First.Store.ReadPrefixPageAsync<string>("page-test/", first[^1].Key, 2, Ct);

        Assert.Equal(new[] { "a", "b" }, first.Select(item => item.Value));
        Assert.Equal("c", Assert.Single(second).Value);
    }

    [Fact]
    public async Task UsageIsGroupedByExecutorNodeWithAGlobalTotalAndNeverInventsMissingTokens()
    {
        await using var mesh = await Mesh.CreateAsync();
        var nodeA = Guid.NewGuid();
        var nodeB = Guid.NewGuid();
        AiSessionTurn Turn(Guid? node, AiUsage? usage, DateTimeOffset at) => new(Guid.NewGuid(), "q", "a", usage, at, null, node);
        var now = DateTimeOffset.UtcNow;
        await mesh.First.Store.SaveAsync("session/usage-1", new AiSessionState("usage-1", "openai", "", [
            Turn(nodeA, new AiUsage(100, 20, 10, 0.5m), now),
            Turn(nodeA, new AiUsage(50, 5), now),
            Turn(nodeB, null, now),
            Turn(nodeB, new AiUsage(7, 3), now.AddDays(-90))], UpdatedAt: now), Ct);
        await mesh.First.Store.SaveAsync("session/usage-2", new AiSessionState("usage-2", "claude-cli", "", [Turn(null, new AiUsage(1, 1), now)], UpdatedAt: now), Ct);

        var usage = await mesh.First.Service.UsageAsync(30, Ct);

        var a = Assert.Single(usage.Nodes, node => node.NodeId == nodeA);
        Assert.Equal(new AiUsageTotals(2, 150, 25, 10, 0.5m, 0), a.Total);
        var b = Assert.Single(usage.Nodes, node => node.NodeId == nodeB);
        Assert.Equal(new AiUsageTotals(1, 0, 0, 0, null, 1), b.Total);
        Assert.Contains(usage.Nodes, node => node.NodeId is null && node.Total.InputTokens == 1);
        Assert.Equal(new AiUsageTotals(4, 151, 26, 10, 0.5m, 1), usage.Total);
        Assert.False(usage.Truncated);
        await Assert.ThrowsAsync<ArgumentException>(() => mesh.First.Service.UsageAsync(0, Ct));
    }

    [Fact]
    public async Task ApiTokenReplicationShowsWhichNodeKnowsEachTokenWithoutExposingValues()
    {
        await using var mesh = await Mesh.CreateAsync();
        var (created, plaintext) = await new Encoding.Api.Auth.ApiTokenRepository(mesh.First.Database.Db).CreateTokenAsync("test", "viewer", null, Ct);

        var view = await mesh.First.Service.ApiTokenReplicationAsync(Ct);

        Assert.Equal(2, view.Nodes.Count);
        var first = Assert.Single(view.Nodes, node => node.NodeId == mesh.First.Identity.PeerId);
        Assert.Equal(new Encoding.Contracts.Auth.ApiTokenReplicationEntry(created.Id, false), Assert.Single(first.Tokens!));
        // Le second nœud n'a pas encore reçu le jeton : l'écart est visible au lieu d'être supposé résorbé.
        var second = Assert.Single(view.Nodes, node => node.NodeId == mesh.Second.Identity.PeerId);
        Assert.True(second.Reachable);
        Assert.Empty(second.Tokens!);
        Assert.DoesNotContain(plaintext, System.Text.Json.JsonSerializer.Serialize(view), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionsCannotMixMediaOrOperationsAndPreserveUsageOnReplay()
    {
        await using var mesh = await Mesh.CreateAsync();
        var scope = new AiSessionScope("subtitle-translation", LibraryId: Guid.NewGuid(), MediaId: Guid.NewGuid());
        var prompt = new AiPrompt("instructions", "question", Scope: scope);
        var first = await mesh.First.Service.GenerateAsync("openai", prompt, Ct);
        Assert.True(first.Success);
        var session = await mesh.First.Service.GetSessionAsync(first.SessionId!, Ct);
        var usage = new AiUsage(100, 20);
        await mesh.First.Store.SaveAsync("session/" + first.SessionId, session! with {
            Turns = [session!.Turns[0] with { Usage = usage }]
        }, Ct);
        var replay = await mesh.First.NewService().GenerateAsync("openai", prompt with { SessionId = first.SessionId, RequestId = first.RequestId }, Ct);
        Assert.Equal(usage, replay.Usage);
        Assert.Equal(usage, Assert.Single((await mesh.First.Service.ListSessionsAsync(0, Ct)).Items).Usage);
        Assert.Equal(1, mesh.Factory.Inferences);
        await Assert.ThrowsAsync<ArgumentException>(() => mesh.First.Service.GenerateAsync("openai",
            prompt with { SessionId = first.SessionId, Scope = scope with { MediaId = Guid.NewGuid() } }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => mesh.First.Service.GenerateAsync("openai",
            prompt with { SessionId = first.SessionId, Model = "another-model" }, Ct));
    }

    [Fact]
    public async Task QuotasUseSignedPeerTransportAndStaySeparatedByNode()
    {
        await using var mesh = await Mesh.CreateAsync();
        var quotas = await mesh.First.Service.QuotasAsync(Ct);
        Assert.Equal(2, quotas.Nodes.Count);
        Assert.All(quotas.Nodes, node => {
            Assert.Equal(2, node.Providers.Count);
            Assert.All(node.Providers, provider => Assert.Equal("unavailable", provider.State));
        });
        Assert.DoesNotContain("private-api-key", JsonSerializer.Serialize(quotas));
    }

    [Fact]
    public async Task FailedExecutorFallsBackToAnotherConnectedPeerAndCommitsOnlyOneTurn()
    {
        await using var mesh = await Mesh.CreateAsync();
        mesh.First.Settings.UpdateSettings(new(OpenAiApiKey: "broken-test-key"));
        var result = await mesh.First.Service.GenerateAsync("openai", new("instructions", "question"), Ct);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, mesh.Factory.Inferences);
        var session = await mesh.First.Service.GetSessionAsync(result.SessionId!, Ct);
        Assert.Single(session!.Turns);
        Assert.Null(session.PendingRequest);
    }

    [Fact]
    public async Task RestartedCoordinatorDoesNotStartAnotherAttemptBeforeLeaseExpires()
    {
        await using var mesh = await Mesh.CreateAsync();
        var sessionId = $"{mesh.First.Identity.PeerId:N}.{Guid.NewGuid():N}";
        var requestId = Guid.NewGuid();
        var prompt = new AiPrompt("instructions", "question", SessionId: sessionId, RequestId: requestId);
        var pending = new AiSessionState(sessionId, "openai", "instructions", [], requestId, DateTimeOffset.UtcNow.AddMinutes(1), prompt);
        await mesh.First.Store.SaveAsync("session/" + sessionId, pending, Ct);
        var result = await mesh.First.NewService().GenerateAsync("openai", prompt, Ct);
        Assert.False(result.Success);
        Assert.Contains("bail", result.ErrorMessage);
        Assert.Equal(0, mesh.Factory.Inferences);
        await mesh.First.Store.SaveAsync("session/" + sessionId, pending with { AttemptDeadline = DateTimeOffset.UtcNow.AddMinutes(-1) }, Ct);
        var recovered = await mesh.First.NewService().GenerateAsync("openai", prompt, Ct);
        Assert.True(recovered.Success, recovered.ErrorMessage);
        Assert.Equal(1, mesh.Factory.Inferences);
    }

    [Fact]
    public async Task LostCoordinatorSessionContinuesOnThisNodeWithItsValidatedHistory()
    {
        await using var mesh = await Mesh.CreateAsync();
        var lost = Guid.NewGuid();
        var sessionId = $"{lost:N}.{Guid.NewGuid():N}";
        var done = new AiSessionTurn(Guid.NewGuid(), "first question", "first answer", null, DateTimeOffset.UtcNow);
        await mesh.First.Store.SaveAsync("session/" + sessionId, new AiSessionState(sessionId, "openai", "instructions", [done]), Ct);
        var prompt = new AiPrompt("instructions", "suite", SessionId: sessionId, RequestId: Guid.NewGuid());

        var result = await mesh.First.NewService().GenerateAsync("openai", prompt, Ct);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains("first answer", mesh.Factory.LastPrompt);
        Assert.Equal(mesh.First.Identity.PeerId.ToString("N"), await mesh.First.Store.ReadAsync<string>(AiClusterService.CoordinatorKey(sessionId), Ct));
        Assert.Equal(2, (await mesh.First.Service.GetSessionAsync(sessionId, Ct))!.Turns.Count);
    }

    [Fact]
    public async Task TakeOverRequiresALocalCheckpointAndHelperSessionsFollowTheNewCoordinator()
    {
        await using var mesh = await Mesh.CreateAsync();
        var lost = Guid.NewGuid();
        var conversation = $"{lost:N}.{Guid.NewGuid():N}";
        var helper = $"{lost:N}.{Guid.NewGuid():N}";

        Assert.Equal(lost, await mesh.First.Service.CoordinatorAsync(conversation, Ct));
        Assert.False(await mesh.First.Service.TryTakeOverAsync(conversation, Ct));
        Assert.Equal(lost, await mesh.First.Service.CoordinatorAsync(conversation, Ct));
        Assert.Null(await mesh.First.Service.CoordinatorAsync("pas-une-session", Ct));

        await mesh.First.Store.SaveAsync("session/" + conversation, new AiSessionState(conversation, "openai", "instructions", []), Ct);
        Assert.True(await mesh.First.Service.TryTakeOverAsync(conversation, Ct));
        Assert.Equal(mesh.First.Identity.PeerId, await mesh.First.Service.CoordinatorAsync(conversation, Ct));

        // Une session auxiliaire (titre, planification) n'a pas besoin de checkpoint : elle suit le coordinateur courant.
        await mesh.First.Service.AdoptSessionAsync(helper, Ct);
        Assert.Equal(mesh.First.Identity.PeerId, await mesh.First.Service.CoordinatorAsync(helper, Ct));
    }

    [Fact]
    public async Task SessionWithoutLocalCheckpointIsNeverTakenOverFromAnEmptyContext()
    {
        await using var mesh = await Mesh.CreateAsync();
        var sessionId = $"{Guid.NewGuid():N}.{Guid.NewGuid():N}";
        var prompt = new AiPrompt("instructions", "suite", SessionId: sessionId, RequestId: Guid.NewGuid());

        var result = await mesh.First.NewService().GenerateAsync("openai", prompt, Ct);

        Assert.False(result.Success);
        Assert.Contains("Coordinateur de session indisponible", result.ErrorMessage);
        Assert.Null(await mesh.First.Store.ReadAsync<string>(AiClusterService.CoordinatorKey(sessionId), Ct));
        Assert.Equal(0, mesh.Factory.Inferences);
    }

    [Fact]
    public async Task UnsignedMeshRequestsAndExpiredAttemptsCannotInvokeModel()
    {
        await using var mesh = await Mesh.CreateAsync();
        var http = new DefaultHttpContext();
        http.Request.Body = new MemoryStream("{}"u8.ToArray());
        Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.HttpResults.UnauthorizedHttpResult>(await mesh.Second.Transport.ReceiveAsync(http, mesh.Second.Service, Ct));
        var request = new AiExecuteRequest(Guid.NewGuid(), "openai", new("system", "user"), DateTimeOffset.UtcNow.AddSeconds(-1));
        var result = (AiCompletionResult)await mesh.Second.Service.DispatchAsync("execute", JsonSerializer.SerializeToElement(request), Ct);
        Assert.False(result.Success);
        Assert.Equal(0, mesh.Factory.Inferences);
        await Assert.ThrowsAsync<ArgumentException>(() => mesh.Second.Service.DispatchAsync("save-token", JsonSerializer.SerializeToElement(new { token = "x" }), Ct));
    }

    [Fact]
    public async Task ModelCatalogAndPolicyUsePeerTransportAndNoCredentialsAreExposed()
    {
        await using var mesh = await Mesh.CreateAsync();
        await mesh.PrepareOpenAiCatalogsAsync();
        var catalog = await mesh.First.Service.ModelsAsync(false, Ct);
        var remote = Assert.Single(catalog.Nodes, n => n.NodeId == mesh.Second.Identity.PeerId);
        Assert.Equal(2, Assert.Single(remote.Providers).Models.Count);
        Assert.DoesNotContain("private-api-key", JsonSerializer.Serialize(catalog));
        await mesh.First.Service.UpdateModelPolicyAsync(new("openai", "other-model", false), Ct);
        await mesh.First.Service.UpdateModelPolicyAsync(new("openai", "gpt-4o-mini", false), Ct);
        Assert.Equal(2, (await mesh.First.Service.ModelPolicyAsync(Ct)).DisabledModels.Count);
        var events = await mesh.First.Journal.GetEventsAfterAsync(mesh.ClusterId, new Dictionary<Guid, long>(), 32, Ct);
        await mesh.Second.Journal.ApplyIncomingAsync(events, Ct);
        Assert.Equal(2, (await mesh.Second.Service.ModelPolicyAsync(Ct)).DisabledModels.Count);
        await Assert.ThrowsAsync<ArgumentException>(() => mesh.First.Service.GenerateAsync("openai", new("s", "u", Model: "other-model"), Ct));
        await mesh.First.Service.UpdateModelPolicyAsync(new("openai", "other-model", true), Ct);
        Assert.Single((await mesh.First.Service.ModelPolicyAsync(Ct)).DisabledModels);
    }

    [Fact]
    public async Task ConversationTurnsCanChangeModelsButReplayAndPendingRequestsKeepTheirSelection()
    {
        await using var mesh = await Mesh.CreateAsync();
        await mesh.PrepareOpenAiCatalogsAsync();
        var node = mesh.Second.Identity.PeerId;
        var firstPrompt = new AiPrompt("instructions", "first", Model: "gpt-4o-mini", NodeId: node);
        var first = await mesh.First.Service.GenerateAsync("openai", firstPrompt, Ct, allowModelChanges: true);
        Assert.True(first.Success, first.ErrorMessage);
        var next = new AiPrompt("instructions", "second", Model: "other-model", SessionId: first.SessionId, RequestId: Guid.NewGuid(), NodeId: node);
        var second = await mesh.Second.Service.GenerateAsync("openai", next, Ct, allowModelChanges: true);
        Assert.True(second.Success, second.ErrorMessage);
        var session = await mesh.First.Service.GetSessionAsync(first.SessionId!, Ct);
        Assert.Equal("gpt-4o-mini", session!.Turns[0].Selection!.Model);
        Assert.Equal("other-model", session.Turns[1].Selection!.Model);
        Assert.Equal(node, session.Turns[1].ExecutorNodeId);
        Assert.Contains("first", mesh.Factory.LastPrompt);
        var replay = await mesh.First.Service.GenerateAsync("openai", firstPrompt with { SessionId = first.SessionId, RequestId = first.RequestId }, Ct, allowModelChanges: true);
        Assert.Equal(first.Content, replay.Content);
        Assert.Equal(2, mesh.Factory.Inferences);
        await Assert.ThrowsAsync<ArgumentException>(() => mesh.First.Service.GenerateAsync("openai", firstPrompt with { Model = "other-model", SessionId = first.SessionId, RequestId = first.RequestId }, Ct, allowModelChanges: true));
        var pendingId = Guid.NewGuid();
        var pendingPrompt = next with { UserPrompt = "third", RequestId = pendingId };
        await mesh.First.Store.SaveAsync("session/" + first.SessionId, session with {
            PendingRequest = pendingId, PendingPrompt = pendingPrompt, AttemptDeadline = DateTimeOffset.UtcNow.AddMinutes(1)
        }, Ct);
        await Assert.ThrowsAsync<ArgumentException>(() => mesh.First.Service.GenerateAsync("openai", pendingPrompt with { Model = "gpt-4o-mini" }, Ct, allowModelChanges: true));
    }

    [Fact]
    public async Task AssistantOptionsUseTheDiscoveredCatalogAndFilterDisabledModels()
    {
        await using var mesh = await Mesh.CreateAsync();
        await mesh.PrepareOpenAiCatalogsAsync();
        var assistant = AiAssistantService.Create(mesh.First.Store, mesh.First.Service, mesh.First.Transport,
            null!, null!, null!, null!, null!, null!, null!, new EphemeralDataProtectionProvider());
        var options = await assistant.OptionsAsync(Ct);
        Assert.Equal(2, options.Models.Count);
        Assert.All(options.Models, option => {
            Assert.Equal(mesh.Second.Identity.PeerId, option.NodeId);
            Assert.Empty(option.ReasoningEfforts);
            Assert.False(option.ReasoningKnown);
        });
        await mesh.First.Service.UpdateModelPolicyAsync(new("openai", "other-model", false), Ct);
        Assert.Single((await assistant.OptionsAsync(Ct)).Models);
    }

    private static async Task<(Mesh Mesh, AiAssistantService Assistant, System.Security.Claims.ClaimsPrincipal User)> TitleHarnessAsync()
    {
        var mesh = await Mesh.CreateAsync();
        await mesh.PrepareOpenAiCatalogsAsync();
        var assistant = AiAssistantService.Create(mesh.First.Store, mesh.First.Service, mesh.First.Transport,
            null!, null!, null!, null!, null!, null!, null!, new EphemeralDataProtectionProvider());
        mesh.First.Assistant = assistant;
        mesh.Second.Assistant = AiAssistantService.Create(mesh.Second.Store, mesh.Second.Service, mesh.Second.Transport,
            null!, null!, null!, null!, null!, null!, null!, new EphemeralDataProtectionProvider());
        var user = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([
            new(System.Security.Claims.ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(System.Security.Claims.ClaimTypes.Role, "admin")], "test"));
        return (mesh, assistant, user);
    }

    private static AiAssistantMessageRequest TitleRequest(Guid mesh, string prompt, string? conversationId = null) =>
        new(Guid.NewGuid(), prompt, conversationId, Provider: "openai", Model: "gpt-4o-mini", NodeId: mesh);

    [Fact]
    public async Task AssistantNamesANewConversationFromItsFirstExchange()
    {
        var (mesh, assistant, user) = await TitleHarnessAsync();
        await using var _ = mesh;
        // planner, answer, then the title (quotes, markdown and a final period are cleaned).
        mesh.Factory.EnqueueReplies("{\"kind\":\"answer\",\"content\":\"ok\"}", "Voici la réponse", "**\"Diagnostic du flow HEVC\".**\nexplication ignorée");
        var first = await assistant.SendAsync(TitleRequest(mesh.Second.Identity.PeerId, "Pourquoi mon flow HEVC échoue ?"), user, Ct);
        Assert.True(first!.Success, first.ErrorMessage);
        Assert.Equal("Diagnostic du flow HEVC", first.Conversation.Title);
        Assert.Equal("ai", first.Conversation.TitleSource);
        Assert.Equal("Diagnostic du flow HEVC", (await assistant.ListAsync(user, Ct)).Single().Title);
        Assert.Contains("Pourquoi mon flow HEVC", mesh.Factory.LastPrompt);
        // The generated title is final: later turns keep it without another title request.
        var before = mesh.Factory.Inferences;
        var second = await assistant.SendAsync(TitleRequest(mesh.Second.Identity.PeerId, "Et pour l’audio ?", first.Conversation.Id), user, Ct);
        Assert.Equal("Diagnostic du flow HEVC", second!.Conversation.Title);
        Assert.Equal(2, mesh.Factory.Inferences - before);
    }

    [Fact]
    public async Task AssistantKeepsThePromptTitleWhenTheProviderCannotNameTheConversation()
    {
        var (mesh, assistant, user) = await TitleHarnessAsync();
        await using var _ = mesh;
        mesh.Factory.EnqueueReplies("{\"kind\":\"answer\",\"content\":\"ok\"}", "Voici la réponse");
        mesh.Factory.EnqueueFailures(8);
        var first = await assistant.SendAsync(TitleRequest(mesh.Second.Identity.PeerId, "Pourquoi mon flow HEVC échoue ?"), user, Ct);
        Assert.True(first!.Success, first.ErrorMessage);
        Assert.Equal("Voici la réponse", first.Content);
        Assert.Equal("Pourquoi mon flow HEVC échoue ?", first.Conversation.Title);
        Assert.Equal("fallback", first.Conversation.TitleSource);
    }

    [Fact]
    public async Task AssistantIgnoresAnEmptyGeneratedTitleAndRetriesOnTheNextTurn()
    {
        var (mesh, assistant, user) = await TitleHarnessAsync();
        await using var _ = mesh;
        mesh.Factory.EnqueueReplies("{\"kind\":\"answer\",\"content\":\"ok\"}", "Réponse 1", " \n ");
        var first = await assistant.SendAsync(TitleRequest(mesh.Second.Identity.PeerId, "Bonjour"), user, Ct);
        Assert.Equal("Bonjour", first!.Conversation.Title);
        Assert.Equal("fallback", first.Conversation.TitleSource);
        mesh.Factory.EnqueueReplies("{\"kind\":\"answer\",\"content\":\"ok\"}", "Réponse 2", "Salutations");
        var second = await assistant.SendAsync(TitleRequest(mesh.Second.Identity.PeerId, "Salut encore", first.Conversation.Id), user, Ct);
        Assert.Equal("Salutations", second!.Conversation.Title);
    }

    [Fact]
    public async Task AssistantRenameIsValidatedOwnedAndNeverOverwrittenByAutomaticTitles()
    {
        var (mesh, assistant, user) = await TitleHarnessAsync();
        await using var _ = mesh;
        mesh.Factory.EnqueueReplies("{\"kind\":\"answer\",\"content\":\"ok\"}", "Réponse", " ");
        var first = await assistant.SendAsync(TitleRequest(mesh.Second.Identity.PeerId, "Bonjour"), user, Ct);
        var renamed = await assistant.RenameAsync(first!.Conversation.Id, "  Mon titre  ", user, Ct);
        Assert.Equal("Mon titre", renamed!.Title);
        Assert.Equal("user", renamed.TitleSource);
        await Assert.ThrowsAsync<ArgumentException>(() => assistant.RenameAsync(first.Conversation.Id, "   ", user, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => assistant.RenameAsync(first.Conversation.Id, new string('x', 200), user, Ct));
        var stranger = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([
            new(System.Security.Claims.ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test"));
        Assert.Null(await assistant.RenameAsync(first.Conversation.Id, "Pirate", stranger, Ct));
        Assert.Null(await assistant.RenameAsync("not-a-session", "Titre", user, Ct));
        mesh.Factory.EnqueueReplies("{\"kind\":\"answer\",\"content\":\"ok\"}", "Réponse 2", "Titre automatique");
        var next = await assistant.SendAsync(TitleRequest(mesh.Second.Identity.PeerId, "Suite", first.Conversation.Id), user, Ct);
        Assert.Equal("Mon titre", next!.Conversation.Title);
        Assert.Equal("Mon titre", (await assistant.GetAsync(first.Conversation.Id, user, Ct))!.Conversation.Title);
    }

    [Fact]
    public async Task AssistantActivityCarriesTheConversationTitle()
    {
        var (mesh, assistant, user) = await TitleHarnessAsync();
        await using var _ = mesh;
        mesh.Factory.EnqueueReplies("{\"kind\":\"answer\",\"content\":\"ok\"}", "Réponse", "Migration de bibliothèque");
        var request = TitleRequest(mesh.Second.Identity.PeerId, "Aide-moi");
        var first = await assistant.SendAsync(request, user, Ct);
        var activity = (await assistant.ActivityAsync(user, Ct)).Single(item => item.RequestId == request.RequestId);
        Assert.Equal("Migration de bibliothèque", activity.ConversationTitle);
        await assistant.RenameAsync(first!.Conversation.Id, "Renommée", user, Ct);
        Assert.Equal("Renommée", (await assistant.GetActivityAsync(request.RequestId, user, Ct))!.ConversationTitle);
    }

    [Fact]
    public async Task AssistantPersistsPerTurnSelectionsAndObservedModelWithoutRewritingOldTurns()
    {
        await using var mesh = await Mesh.CreateAsync();
        await mesh.PrepareOpenAiCatalogsAsync();
        mesh.First.Settings.UpdateSettings(new(OpenAiApiKey: "first-test-key"));
        mesh.Factory.ReportModel = true;
        var resourceId = Guid.NewGuid();
        var nodes = new NodeRepository(mesh.First.Database.Db);
        await nodes.EnrollAsync(new(resourceId, "worker", [Encoding.Contracts.Nodes.NodeRole.Worker], 1, [], "node-secret"), true, Ct);
        var assistant = AiAssistantService.Create(mesh.First.Store, mesh.First.Service, mesh.First.Transport,
            null!, null!, null!, null!, nodes, new NodePresenceOptions(TimeSpan.FromMinutes(2)), null!, new EphemeralDataProtectionProvider());
        mesh.First.Assistant = assistant;
        var remoteAssistant = AiAssistantService.Create(mesh.Second.Store, mesh.Second.Service, mesh.Second.Transport,
            null!, null!, null!, null!, null!, null!, null!, new EphemeralDataProtectionProvider());
        mesh.Second.Assistant = remoteAssistant;
        var user = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([
            new(System.Security.Claims.ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(System.Security.Claims.ClaimTypes.Role, "admin")], "test"));
        var request = new AiAssistantMessageRequest(Guid.NewGuid(), "first question", ResourceType: "node", ResourceId: resourceId,
            Provider: "openai", Model: "gpt-4o-mini", NodeId: mesh.Second.Identity.PeerId);
        var first = await assistant.SendAsync(request, user, Ct);
        Assert.True(first!.Success, first.ErrorMessage);
        Assert.Equal("resolved-test-model", first.ActualModel);
        Assert.Equal(mesh.Second.Identity.PeerId, first.Selection!.NodeId);
        Assert.Equal(mesh.Second.Identity.PeerId, first.ExecutorNodeId);
        var plannerRequestId = DerivePlannerRequestId(first.Conversation.Id, request.RequestId);
        var plannerSessionId = $"{first.Conversation.Id.Split('.')[0]}.{plannerRequestId:N}";
        var plannerSession = await mesh.First.Service.GetSessionAsync(plannerSessionId, Ct);
        Assert.Equal(mesh.Second.Identity.PeerId, Assert.Single(plannerSession!.Turns).Selection!.NodeId);
        Assert.Equal(mesh.Second.Identity.PeerId, plannerSession.Turns[0].ExecutorNodeId);
        Assert.True(await assistant.IsAssistantConversationAsync(first.Conversation.Id, Ct));
        Assert.True(await assistant.IsAssistantConversationAsync(plannerSessionId, Ct));
        Assert.DoesNotContain((await mesh.First.Service.ListSessionsAsync(0, Ct)).Items,
            item => item.Id == first.Conversation.Id || item.Id == plannerSessionId);
        var otherAdmin = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([
            new(System.Security.Claims.ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(System.Security.Claims.ClaimTypes.Role, "admin")], "test"));
        Assert.Null(await assistant.GetAsync(first.Conversation.Id, otherAdmin, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => assistant.SendAsync(request with { Model = "other-model" }, user, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => assistant.SendAsync(new(Guid.NewGuid(), "unknown node",
            Provider: "openai", Model: "gpt-4o-mini", NodeId: Guid.NewGuid()), user, Ct));
        var second = await assistant.SendAsync(new(Guid.NewGuid(), "second question", ConversationId: first.Conversation.Id,
            Provider: "openai", Model: "other-model", NodeId: mesh.Second.Identity.PeerId), user, Ct);
        Assert.True(second!.Success, second.ErrorMessage);
        var detail = await assistant.GetAsync(first.Conversation.Id, user, Ct);
        Assert.Equal("gpt-4o-mini", detail!.Turns[0].Selection!.Model);
        Assert.Equal("other-model", detail.Turns[1].Selection!.Model);
        Assert.Equal("other-model", detail.Conversation.Model);
        Assert.All(detail.Turns, turn => Assert.Equal("resolved-test-model", turn.ActualModel));
        var replay = await assistant.SendAsync(request with { ConversationId = first.Conversation.Id }, user, Ct);
        Assert.True(replay!.Success);
        Assert.Equal("other-model", replay.Conversation.Model);
        Assert.Equal("other-model", (await assistant.GetAsync(first.Conversation.Id, user, Ct))!.Conversation.Model);

        await Assert.ThrowsAsync<ArgumentException>(() => assistant.SendAsync(request with { ConversationId = first.Conversation.Id, Model = "other-model" }, user, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => assistant.SendAsync(new(Guid.NewGuid(), "invalid effort", ConversationId: first.Conversation.Id,
            Provider: "openai", Model: "other-model", ReasoningEffort: "high", NodeId: mesh.Second.Identity.PeerId), user, Ct));
        mesh.Factory.BlockInferences = true;
        var cancelledRequest = Guid.NewGuid();
        var sending = assistant.SendAsync(new(cancelledRequest, "third question", ConversationId: first.Conversation.Id,
            Provider: "openai", Model: "other-model", NodeId: mesh.Second.Identity.PeerId), user, Ct);
        await mesh.Factory.InferenceStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await mesh.Second.Transport.CallNodeAsync<bool>(mesh.First.Identity.PeerId, "assistant-cancel",
            new AiAssistantForwardCancel(Guid.Parse(user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value), cancelledRequest),
            TimeSpan.FromSeconds(15), Ct));
        Assert.False((await sending)!.Success);
        Assert.Equal(2, (await assistant.GetAsync(first.Conversation.Id, user, Ct))!.Turns.Count);

        mesh.Factory.BlockInferences = false;
        var forwarded = await mesh.Second.Transport.CallNodeAsync<AiAssistantForwardResponse>(mesh.First.Identity.PeerId,
            "assistant-message", new AiAssistantForwardRequest(
                new(Guid.NewGuid(), "continued from another API node", ConversationId: first.Conversation.Id),
                Guid.Parse(user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value), true),
            TimeSpan.FromMinutes(2), Ct);
        Assert.Null(forwarded.Error);
        Assert.True(forwarded.Result!.Success, forwarded.Result.ErrorMessage);
        Assert.Equal(3, (await assistant.GetAsync(first.Conversation.Id, user, Ct))!.Turns.Count);

    }

    [Fact]
    public async Task FlowRepairPreservesSessionScopeAndUsesDeterministicRepairRequestId()
    {
        await using var mesh = await Mesh.CreateAsync();
        var (registry, validator, _) = Harness.Services();
        mesh.Factory.EnqueueReplies("not JSON", ValidFlowJson(registry));
        using var services = new ServiceCollection().AddSingleton(mesh.First.Service).BuildServiceProvider();
        var factory = new AiClientFactory(mesh.First.Configuration, mesh.Factory, mesh.First.Settings,
            mesh.First.Profile.Runtime, services);
        var generator = new FlowAiGeneratorService(factory, new FlowCatalogPromptBuilder(registry), validator);
        var requestId = Guid.NewGuid();
        var sessionId = $"{mesh.First.Identity.PeerId:N}.{Guid.NewGuid():N}";

        var result = await generator.GenerateFlowAsync(new FlowAiGenerateRequest("video", "test flow", "openai", sessionId, requestId));

        Assert.True(result.Success, result.Explanation);
        Assert.Equal(sessionId, result.SessionId);
        Assert.Equal(requestId, result.RequestId);
        var session = await mesh.First.Service.GetSessionAsync(sessionId, Ct);
        Assert.Equal(new AiSessionScope("flow-create"), session!.Scope);
        Assert.Equal(2, session.Turns.Count);
        Assert.Equal(requestId, session.Turns[0].RequestId);
        Assert.Equal(DeriveRepairRequestId(requestId), session.Turns[1].RequestId);
        Assert.All(session.Turns, turn => Assert.Equal(mesh.Second.Identity.PeerId, turn.ExecutorNodeId));
    }

    [Fact]
    public async Task FailedFlowRepairDoesNotReturnValidationIssuesFromTheFirstOutput()
    {
        await using var mesh = await Mesh.CreateAsync();
        var (registry, validator, _) = Harness.Services();
        var unavailableComponentFlow = new FlowDefinition(1, Guid.NewGuid(), 1, "Invalid", "video",
        [
            new("entry", null, registry.FindReference("base-input-file")!, new Dictionary<string, JsonElement>()),
            new("ai", null, new FlowComponentReference("ai", "1.0.0", "ai-translate-subtitles", "1.0.0", 1), new Dictionary<string, JsonElement>()),
            new("complete", null, registry.FindReference("base-complete-flow")!, new Dictionary<string, JsonElement>
            {
                ["result"] = JsonSerializer.SerializeToElement("unchanged"),
            }),
        ], [new("edge1", "entry", "success", "ai"), new("edge2", "ai", "output", "complete")]);
        mesh.Factory.EnqueueReplies(EmptyFlowJson(), JsonSerializer.Serialize(unavailableComponentFlow, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using var services = new ServiceCollection().AddSingleton(mesh.First.Service).BuildServiceProvider();
        var factory = new AiClientFactory(mesh.First.Configuration, mesh.Factory, mesh.First.Settings,
            mesh.First.Profile.Runtime, services);
        var generator = new FlowAiGeneratorService(factory, new FlowCatalogPromptBuilder(registry), validator);

        var result = await generator.GenerateFlowAsync(new FlowAiGenerateRequest("video", "test flow", "openai", RequestId: Guid.NewGuid()));

        Assert.False(result.Success);
        Assert.Contains(result.ValidationIssues!, issue => issue.Code == "field.invalid");
        Assert.DoesNotContain(result.ValidationIssues!, issue => issue.Code == "graph.entry.count");
        Assert.Contains("réparation", result.Explanation!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, mesh.Factory.Inferences);
    }

    [Fact]
    public async Task InterruptedFlowRepairCanResumeWithItsSameDeterministicRequestId()
    {
        await using var mesh = await Mesh.CreateAsync();
        var (registry, validator, _) = Harness.Services();
        mesh.Factory.EnqueueReplies("not JSON");
        mesh.Factory.EnqueueFailures(1);
        mesh.Factory.EnqueueReplies(ValidFlowJson(registry));
        using var services = new ServiceCollection().AddSingleton(mesh.First.Service).BuildServiceProvider();
        var factory = new AiClientFactory(mesh.First.Configuration, mesh.Factory, mesh.First.Settings,
            mesh.First.Profile.Runtime, services);
        var generator = new FlowAiGeneratorService(factory, new FlowCatalogPromptBuilder(registry), validator);
        var requestId = Guid.NewGuid();
        var sessionId = $"{mesh.First.Identity.PeerId:N}.{Guid.NewGuid():N}";
        var request = new FlowAiGenerateRequest("video", "test flow", "openai", sessionId, requestId);

        var interrupted = await generator.GenerateFlowAsync(request);
        Assert.False(interrupted.Success);
        var resumed = await generator.GenerateFlowAsync(request);

        Assert.True(resumed.Success, resumed.Explanation);
        var session = await mesh.First.Service.GetSessionAsync(sessionId, Ct);
        Assert.Equal(2, session!.Turns.Count);
        Assert.Equal(DeriveRepairRequestId(requestId), session.Turns[1].RequestId);
        Assert.Equal(3, mesh.Factory.Inferences);
    }

    private static Guid DerivePlannerRequestId(string conversationId, Guid requestId)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(conversationId + "/" + requestId.ToString("N")
            + "/assistant-read-tool-planner"));
        var derived = new Guid(bytes.AsSpan(0, 16));
        return derived == Guid.Empty ? new Guid(bytes.AsSpan(16, 16)) : derived;
    }

    private static Guid DeriveRepairRequestId(Guid requestId)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"flow-repair-v1:{requestId:N}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static string ValidFlowJson(FlowComponentRegistry registry)
    {
        var flow = new FlowDefinition(1, Guid.NewGuid(), 1, "Test flow", "video",
        [
            new("entry", "Entrée", registry.FindReference("base-input-file")!, new Dictionary<string, JsonElement>()),
            new("complete", "Terminer", registry.FindReference("base-complete-flow")!, new Dictionary<string, JsonElement>
            {
                ["result"] = JsonSerializer.SerializeToElement("unchanged"),
            }),
        ], [new("edge", "entry", "success", "complete")]);
        return JsonSerializer.Serialize(flow, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static string EmptyFlowJson() => JsonSerializer.Serialize(new FlowDefinition(1, Guid.NewGuid(), 1,
        "Vide", "video", [], []), new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private sealed class Node : IAsyncDisposable
    {
        public TestDatabase Database = null!;
        public AiCliTestProfile Profile = null!;
        public ClusterPeerIdentityStore Identity = null!;
        public ClusterDataKeyStore Key = null!;
        public ClusterPeerAdmissionRepository Admissions = null!;
        public ClusterReconciliationRepository Journal = null!;
        public AiClusterStore Store = null!;
        public AiClusterTransport Transport = null!;
        public AiClusterService Service = null!;
        public DeviceCodeAuthService Auth = null!;
        public AiSettingsService Settings = null!;
        public AiClientFactory Clients = null!;
        public AiModelCatalogService Catalog = null!;
        public AiAssistantService? Assistant;
        public IConfiguration Configuration = null!;
        public LocalNodeRegistry LocalNode = null!;
        public static async Task<Node> CreateAsync(Guid cluster, string name, string other, MeshFactory factory, byte[] key, bool connected)
        {
            var node = new Node { Database = await TestDatabase.CreateSqliteAsync("ai-cluster"), Profile = new() };
            await DatabaseMigrator.ApplyAsync(node.Database.Db, Harness.RepositoryRoot(), Ct);
            node.Configuration = new ConfigurationBuilder().AddConfiguration(node.Profile.Configuration).AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Node:Name"] = name, ["Cluster:Reconciliation:Peers:0"] = "http://" + other + "/",
                ["Cluster:Reconciliation:AdvertisedApiBaseUrl"] = "http://" + name + "/",
                ["AiTools:OpenAi:ApiKey"] = connected ? "private-api-key" : null
            }).Build();
            var clusterIdentity = new ClusterIdentityRepository(node.Database.Db);
            await clusterIdentity.AdoptAsync(cluster, "AI tests", Ct);
            node.Identity = new(node.Configuration, new EphemeralDataProtectionProvider());
            node.Key = new(cluster, key);
            node.Admissions = new(node.Database.Db, clusterIdentity, node.Identity, node.Key);
            node.Journal = new(node.Database.Db);
            node.Store = new(node.Database.Db, node.Journal, clusterIdentity, node.Key);
            node.Transport = new(node.Identity, clusterIdentity, node.Admissions, node.Journal, new(), node.Store, factory, node.Configuration);
            node.Auth = new(node.Profile.Runtime);
            node.Settings = new(node.Configuration);
            node.Clients = new(node.Configuration, factory, node.Settings, node.Profile.Runtime);
            node.Catalog = new(node.Profile.Runtime, node.Settings, factory);
            node.LocalNode = new(new NodeRepository(node.Database.Db));
            node.Service = node.NewService();
            return node;
        }
        public AiClusterService NewService() => new(Transport, Store, Auth, Settings, Clients, Configuration, LocalNode, new AiQuotaService(Profile.Runtime), Catalog,
            new ServiceCollection().AddSingleton(new Encoding.Api.Auth.ApiTokenRepository(Database.Db)).AddSingleton<AiAssistantService>(_ => Assistant ?? throw new InvalidOperationException("Assistant not configured")).BuildServiceProvider());
        public async ValueTask DisposeAsync() { Auth.Dispose(); Identity.Dispose(); Key.Dispose(); await Database.DisposeAsync(); Profile.Dispose(); }
    }
    private sealed class Mesh : IAsyncDisposable
    {
        public Guid ClusterId = Guid.NewGuid();
        public Node First = null!;
        public Node Second = null!;
        public MeshFactory Factory = new();
        public static async Task<Mesh> CreateAsync()
        {
            var mesh = new Mesh();
            var key = RandomNumberGenerator.GetBytes(32);
            mesh.First = await Node.CreateAsync(mesh.ClusterId, "first", "second", mesh.Factory, key, false);
            mesh.Second = await Node.CreateAsync(mesh.ClusterId, "second", "first", mesh.Factory, key, true);
            mesh.Factory.Nodes["first"] = mesh.First;
            mesh.Factory.Nodes["second"] = mesh.Second;
            var nodeId = Guid.NewGuid();
            await mesh.First.Admissions.EnsureRootAdmissionAsync(mesh.ClusterId, Ct);
            await mesh.First.Admissions.CreateDirectApprovalGrantAsync(nodeId, mesh.Second.Identity.PeerId, mesh.Second.Identity.PublicKey, Ct);
            var bootstrap = await mesh.First.Admissions.GetBootstrapForNodeAsync(nodeId, Ct);
            await mesh.Second.Admissions.ImportBootstrapAsync(bootstrap!, nodeId, Ct);
            return mesh;
        }
        public async Task PrepareOpenAiCatalogsAsync()
        {
            await Second.Catalog.ReadAsync("openai", "http", true, Ct);
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var view = await First.Service.CachedViewAsync(Ct);
                var remote = view.Nodes.FirstOrDefault(node => node.NodeId == Second.Identity.PeerId);
                if (remote?.Catalogs?.Any(provider => provider.Provider == "openai" && provider.State == "available") == true)
                    return;
                await Task.Delay(25, Ct);
            }
            throw new InvalidOperationException("Le catalogue OpenAI du pair n’est pas apparu dans la vue cluster mise en cache.");
        }
        public async ValueTask DisposeAsync() { await First.DisposeAsync(); await Second.DisposeAsync(); }
    }
    private sealed class MeshFactory : IHttpClientFactory
    {
        public readonly Dictionary<string, Node> Nodes = [];
        private readonly ConcurrentQueue<(string? Content, HttpStatusCode Status)> replies = new();
        public int Inferences;
        public bool ReportModel;
        public bool BlockInferences;
        public TaskCompletionSource InferenceStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string LastPrompt = "";
        public void EnqueueReplies(params string[] values)
        {
            foreach (var value in values) replies.Enqueue((value, HttpStatusCode.OK));
        }
        public void EnqueueFailures(int count)
        {
            for (var index = 0; index < count; index++) replies.Enqueue((null, HttpStatusCode.ServiceUnavailable));
        }
        public HttpClient CreateClient(string name) => new(new Handler(this, name)) { Timeout = Timeout.InfiniteTimeSpan };
        private sealed class Handler(MeshFactory factory, string name) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (name == "AiClient")
                {
                    if (request.Method == HttpMethod.Get)
                        return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { data = new[] { new { id = "gpt-4o-mini" }, new { id = "other-model" } } }) };
                    factory.Inferences++;
                    if (factory.BlockInferences)
                    {
                        factory.InferenceStarted.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    }
                    factory.LastPrompt = await request.Content!.ReadAsStringAsync(ct);
                    if (request.Headers.Authorization?.Parameter == "broken-test-key")
                        return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("Provider unavailable") };
                    var queued = factory.replies.TryDequeue(out var next)
                        ? next
                        : (Content: "answer from remote model", Status: HttpStatusCode.OK);
                    if (queued.Status != HttpStatusCode.OK)
                        return new(queued.Status) { Content = new StringContent("Provider temporarily unavailable") };
                    var reply = queued.Content!;
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { model = factory.ReportModel ? "resolved-test-model" : null, choices = new[] { new { message = new { content = reply } } } }) };
                }
                var node = factory.Nodes[request.RequestUri!.Host];
                var http = new DefaultHttpContext();
                http.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
                http.Request.Body = new MemoryStream(await request.Content!.ReadAsByteArrayAsync(ct));
                http.Response.Body = new MemoryStream();
                foreach (var header in request.Headers) http.Request.Headers[header.Key] = header.Value.ToArray();
                var result = await node.Transport.ReceiveAsync(http, node.Service, ct);
                await result.ExecuteAsync(http);
                var response = new HttpResponseMessage((HttpStatusCode)http.Response.StatusCode) { Content = new ByteArrayContent(((MemoryStream)http.Response.Body).ToArray()) };
                foreach (var header in http.Response.Headers) response.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                return response;
            }
        }
    }
}
