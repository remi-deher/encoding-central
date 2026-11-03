using Encoding.Api.Executions;
using Encoding.Api.Infrastructure;
using Encoding.Contracts.Workers;
using Encoding.WorkerAgent;
using Encoding.WorkerAgent.Runtime;
using Encoding.WorkerAgent.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class WorkerLinkTests
{
    [Fact]
    public void ReconnectUsesExponentialCeilingsAndResetsOnlyAfterAStableConnection()
    {
        var clock = new Clock();
        var policy = new WakeReconnectPolicy(clock, () => 1);
        foreach (var seconds in new[] { 1, 2, 4, 8, 16, 32, 60, 60 })
            Assert.Equal(TimeSpan.FromSeconds(seconds), policy.NextDelay());
        policy.MarkConnected();
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(TimeSpan.FromSeconds(60), policy.NextDelay());
        policy.MarkConnected();
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(1), policy.NextDelay());
    }

    [Fact]
    public void ReconnectJitterAndServerRetryStayBounded()
    {
        var policy = new WakeReconnectPolicy(new Clock(), () => 0.5);
        Assert.Equal(TimeSpan.FromMilliseconds(500), policy.NextDelay());
        policy.SetServerRetry(TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(3), policy.NextDelay());
        policy.SetServerRetry(TimeSpan.FromDays(1));
        Assert.Equal(TimeSpan.FromSeconds(60), policy.NextDelay());
    }

    [Fact]
    public void AStillValidLeaseSurvivesFiveRenewalFailuresButStopsBeforeExpiry()
    {
        var clock = new Clock();
        var expired = false;
        using var deadline = new WorkerLeaseDeadline(clock.GetUtcNow().AddSeconds(180), clock, () => expired = true);
        clock.Advance(TimeSpan.FromSeconds(75));
        Assert.False(expired);
        Assert.Equal(TimeSpan.FromSeconds(103), deadline.Remaining);
        clock.Advance(TimeSpan.FromSeconds(103));
        Assert.True(expired);
        Assert.False(deadline.TryRenew(clock.GetUtcNow().AddSeconds(180)));
    }

    [Fact]
    public void SuccessfulRenewalMovesTheDeadlineAndALateResponseCannotReviveIt()
    {
        var clock = new Clock();
        var expired = false;
        using var deadline = new WorkerLeaseDeadline(clock.GetUtcNow().AddSeconds(180), clock, () => expired = true);
        clock.Advance(TimeSpan.FromSeconds(75));
        Assert.True(deadline.TryRenew(clock.GetUtcNow().AddSeconds(180)));
        clock.Advance(TimeSpan.FromSeconds(103));
        Assert.False(expired);
        clock.Advance(TimeSpan.FromSeconds(75), fireTimers: false);
        Assert.False(deadline.TryRenew(clock.GetUtcNow().AddSeconds(180)));
        clock.Advance(TimeSpan.Zero);
        Assert.True(expired);
    }

    [Fact]
    public void AnExpiredInitialLeaseStopsImmediately()
    {
        var clock = new Clock();
        var expired = false;
        using var deadline = new WorkerLeaseDeadline(clock.GetUtcNow().AddSeconds(-1), clock, () => expired = true);
        clock.Advance(TimeSpan.Zero);
        Assert.True(expired);
        Assert.False(deadline.TryRenew(clock.GetUtcNow().AddSeconds(180)));
    }

    [Fact]
    public void HeartbeatJitterHasTenPercentBounds()
    {
        Assert.Equal(TimeSpan.FromSeconds(9), WorkerPacing.Jitter(TimeSpan.FromSeconds(10), WorkerPacing.HeartbeatJitter, 0));
        Assert.Equal(TimeSpan.FromSeconds(11), WorkerPacing.Jitter(TimeSpan.FromSeconds(10), WorkerPacing.HeartbeatJitter, 1));
        Assert.Equal(TimeSpan.FromMinutes(5), WorkerPacing.EnrolledPollInterval);
    }

    [Fact]
    public void HeartbeatCannotRedirectTheEnrollmentAndKnownAddressesDoNotReceiveTheToken()
    {
        Assert.Null(typeof(WorkerResponse).GetProperty("MasterUrl"));
        var options = WorkerAgentOptions.FromConfiguration(new ConfigurationBuilder().Build());
        var identity = new WorkerIdentityState(Guid.NewGuid(), Guid.NewGuid().ToString(), "Chosen",
            "http://chosen-central:8080/", "test-token", true, KnownServerUrls: ["http://other-central:8080/"]);
        Assert.Equal(["http://chosen-central:8080/"], identity.GetCandidateServerUrls(options));
    }

    [Fact]
    public async Task OpeningANewWakeStreamClosesThePreviousOneForThatWorker()
    {
        var streams = new RuntimeWakeStreams(new ExecutionStreamBroadcaster(), new InterfaceLiveBroadcaster());
        var worker = Guid.NewGuid();
        using var stop = new CancellationTokenSource();
        var first = streams.RunAsync(worker, _ => Task.FromResult(true), (_, _) => Task.CompletedTask, stop.Token);
        var second = streams.RunAsync(worker, _ => Task.FromResult(true), (_, _) => Task.CompletedTask, stop.Token);
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(second.IsCompleted);
        stop.Cancel();
        await second.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task RevocationClosesTheWakeStreamEvenWhileWakeEventsKeepArriving()
    {
        var clock = new Clock();
        var live = new InterfaceLiveBroadcaster();
        var streams = new RuntimeWakeStreams(new ExecutionStreamBroadcaster(), live, clock);
        var emitted = new List<string>();
        var checks = 0;
        using var stop = new CancellationTokenSource();
        var running = streams.RunAsync(Guid.NewGuid(), _ => { checks++; return Task.FromResult(false); },
            (kind, _) => { emitted.Add(kind); return Task.CompletedTask; }, stop.Token);
        clock.Advance(TimeSpan.FromSeconds(15));
        live.Publish("workers");
        await running.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, checks);
        Assert.Equal(["ready"], emitted);
    }

    [Fact]
    public async Task WakeReaderAppliesServerRetryAndProcessesReadyAndWakeEvents()
    {
        var clock = new Clock();
        var policy = new WakeReconnectPolicy(clock, () => 0.5);
        var options = WorkerAgentOptions.FromConfiguration(new ConfigurationBuilder().Build());
        var signal = new WorkerWakeSignal();
        using var listener = new WorkerWakeListener(options, new WorkerIdentityStore(options), signal, clock,
            NullLogger<WorkerWakeListener>.Instance, policy);
        await listener.ReadEventsAsync(new StringReader("retry: invalid\nretry: -1\nretry: 3000\nevent: ready\n\nevent: wake\n\n"), CancellationToken.None);
        Assert.True(signal.Connected);
        Assert.Equal(TimeSpan.FromSeconds(3), policy.NextDelay());
    }

    [Fact]
    public async Task WakeReaderStopsAStreamThatHasBeenSilentForFortyFiveSeconds()
    {
        var clock = new Clock();
        var options = WorkerAgentOptions.FromConfiguration(new ConfigurationBuilder().Build());
        using var listener = new WorkerWakeListener(options, new WorkerIdentityStore(options), new WorkerWakeSignal(), clock,
            NullLogger<WorkerWakeListener>.Instance);
        var reading = listener.ReadEventsAsync(new SilentReader(), CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(45));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task RuntimeClientRefusesToForwardItsTokenAcrossAnHttpRedirect()
    {
        var socket = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        socket.Start();
        var port = ((System.Net.IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        using var server = new System.Net.HttpListener();
        var url = $"http://127.0.0.1:{port}/";
        server.Prefixes.Add(url);
        server.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serving = Task.Run(async () =>
        {
            var request = await server.GetContextAsync().WaitAsync(timeout.Token);
            Assert.Equal("fixture-token", request.Request.Headers["X-Node-Token"]);
            request.Response.StatusCode = 302;
            request.Response.RedirectLocation = "http://127.0.0.1:9/other-central";
            request.Response.Close();
        }, timeout.Token);
        var identity = new WorkerIdentityState(Guid.NewGuid(), Guid.NewGuid().ToString(), "Chosen", url, "fixture-token", true);
        using var client = new RuntimeClient(identity, url);
        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => client.OpenWakeStreamAsync(timeout.Token));
        Assert.Equal(System.Net.HttpStatusCode.Found, refused.StatusCode);
        await serving;
    }

    private sealed class SilentReader : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null;
        }
    }

    private sealed class Clock : TimeProvider
    {
        private long ticks;
        private readonly List<Timer> timers = [];
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
        public override long GetTimestamp() => ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan elapsed, bool fireTimers = true)
        {
            ticks += elapsed.Ticks;
            if (fireTimers)
                foreach (var timer in timers.ToArray()) timer.FireIfDue(ticks);
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        private sealed class Timer(Clock clock, TimerCallback callback, object? state) : ITimer
        {
            private long? due;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.ticks + dueTime.Ticks;
                return true;
            }
            public void FireIfDue(long now)
            {
                if (due is not { } at || at > now) return;
                due = null;
                callback(state);
            }
            public void Dispose() => due = null;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
