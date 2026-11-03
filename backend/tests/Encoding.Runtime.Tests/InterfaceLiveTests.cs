using Encoding.Api.Infrastructure;
using Encoding.WorkerAgent.Runtime;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>Le temps réel remplace le sondage : la table route → sujet et le réveil des workers doivent rester sans boucle ni perte.</summary>
public sealed class InterfaceLiveTests
{
    [Theory]
    [InlineData("POST", "/api/v1/flows", "flows")]
    [InlineData("PUT", "/api/v1/flows/11111111-1111-1111-1111-111111111111", "flows")]
    [InlineData("POST", "/api/v1/scripts", "flows")]
    [InlineData("POST", "/api/v1/plugins/x/enable", "plugins")]
    [InlineData("PUT", "/api/v1/network/settings", "settings")]
    [InlineData("POST", "/api/v1/runtime/workers/11111111-1111-1111-1111-111111111111/executions/22222222-2222-2222-2222-222222222222/operation-logs", "execution-logs")]
    public void AMutationNamesTheTopicItChanges(string method, string path, string topic) =>
        Assert.Equal(topic, InterfaceLiveNotifier.TopicFor(method, path));

    /// <summary>Workers, bibliothèques et exécutions ont leurs propres sources ; un claim (204 sans travail) ne doit jamais réveiller les workers en boucle.</summary>
    [Theory]
    [InlineData("GET", "/api/v1/flows")]
    [InlineData("HEAD", "/api/v1/flows")]
    [InlineData("POST", "/api/v1/auth/login")]
    [InlineData("POST", "/health/ready")]
    [InlineData("POST", "/api/v1/libraries/11111111-1111-1111-1111-111111111111/scan")]
    [InlineData("POST", "/api/v1/workers/11111111-1111-1111-1111-111111111111/leave")]
    [InlineData("POST", "/api/v1/runtime/workers/11111111-1111-1111-1111-111111111111/claim")]
    [InlineData("POST", "/api/v1/runtime/workers/11111111-1111-1111-1111-111111111111/library-scans/claim")]
    [InlineData("POST", "/api/v1/runtime/workers/11111111-1111-1111-1111-111111111111/executions/22222222-2222-2222-2222-222222222222/steps/start")]
    public void RoutesWithAnotherSourceOrNoChangeAnnounceNothing(string method, string path) =>
        Assert.Null(InterfaceLiveNotifier.TopicFor(method, path));

    [Fact]
    public async Task AWorkerBlockedOnItsFallbackWakesWhenTheCentralSignals()
    {
        var signal = new WorkerWakeSignal();
        var waiter = signal.CreateWaiter();
        signal.SetConnected(true);
        await waiter.WaitAsync(60_000, CancellationToken.None); // la connexion elle-même est un signal

        var waiting = waiter.WaitAsync(60_000, CancellationToken.None);
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);
        signal.Pulse();
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ASignalSentWhileTheWorkerIsBusyIsNotLost()
    {
        var signal = new WorkerWakeSignal();
        var waiter = signal.CreateWaiter();
        signal.Pulse();
        await waiter.WaitAsync(60_000, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EveryLoopSeesEachSignalOnce()
    {
        var signal = new WorkerWakeSignal();
        var first = signal.CreateWaiter();
        var second = signal.CreateWaiter();
        signal.Pulse();
        await first.WaitAsync(60_000, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await second.WaitAsync(60_000, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        var again = first.WaitAsync(300, CancellationToken.None);
        await Task.Delay(100);
        Assert.False(again.IsCompleted, "Un signal déjà consommé ne doit pas réveiller la boucle une seconde fois.");
        await again.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WithoutTheStreamEachLoopKeepsItsOwnShortDelay()
    {
        var signal = new WorkerWakeSignal();
        var waiter = signal.CreateWaiter();
        Assert.False(signal.Connected);
        await waiter.WaitAsync(200, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ABrokenStreamReleasesTheLoopsSoTheyFallBackToTheirOwnDelay()
    {
        var signal = new WorkerWakeSignal();
        var waiter = signal.CreateWaiter();
        signal.SetConnected(true);
        await waiter.WaitAsync(60_000, CancellationToken.None);
        var waiting = waiter.WaitAsync(60_000, CancellationToken.None);
        signal.SetConnected(false);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(signal.Connected);
    }

    [Fact]
    public async Task ShuttingDownCancelsTheWait()
    {
        var signal = new WorkerWakeSignal();
        using var stop = new CancellationTokenSource();
        var waiting = signal.CreateWaiter().WaitAsync(60_000, stop.Token);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task TheBroadcasterReportsEachTopicOnceToASubscriberThatWasWaiting()
    {
        var live = new InterfaceLiveBroadcaster();
        var known = live.CurrentVersion;
        var waiting = live.WaitAsync(known, TimeSpan.FromSeconds(30), CancellationToken.None);
        live.Publish("flows");
        live.Publish("flows");
        live.Publish("libraries");
        var signal = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("flows", signal.Topics);
        Assert.Equal(signal.Topics.Distinct().Count(), signal.Topics.Count);
    }

    [Fact]
    public void ThePathHelperIgnoresPathsOutsideTheApi() =>
        Assert.Null(InterfaceLiveNotifier.TopicFor("POST", new PathString("/")));

    [Fact]
    public async Task AnAssistantActivitySavedWhileTheStreamIsWaitingWakesIt()
    {
        var stream = new Encoding.Api.Tools.AiActivityBroadcaster();
        var known = stream.CurrentVersion;
        var waiting = stream.WaitAsync(known, TimeSpan.FromSeconds(30), CancellationToken.None);
        await Task.Delay(50);
        Assert.False(waiting.IsCompleted);
        stream.Publish();
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(5)) > known);
    }

    [Fact]
    public async Task AnAssistantActivitySavedBetweenTwoReadsIsNotLost()
    {
        var stream = new Encoding.Api.Tools.AiActivityBroadcaster();
        var known = stream.CurrentVersion;
        stream.Publish();
        Assert.True(await stream.WaitAsync(known, TimeSpan.FromSeconds(30), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)) > known);
    }

    [Fact]
    public async Task AnIdleAssistantStreamReturnsTheSameVersionAtTheHeartbeat()
    {
        var stream = new Encoding.Api.Tools.AiActivityBroadcaster();
        var known = stream.CurrentVersion;
        Assert.Equal(known, await stream.WaitAsync(known, TimeSpan.FromMilliseconds(100), CancellationToken.None));
    }
}
