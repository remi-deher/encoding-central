using System.Text.Json;
using Encoding.Api.Flows;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.NodeAgent.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace Encoding.Runtime.Tests;

/// <summary>Invocateur de garde qui n'est jamais appelé (transferts sans garde).</summary>
internal sealed class NoGuardInvoker : IPublicationGuardInvoker
{
    public Task<string> InvokeAsync(RuntimeTransfer transfer, RuntimePublicationGuard guard, CancellationToken ct) => throw new InvalidOperationException("Aucune garde attendue.");
}

internal static class PublicationGuardScenarios
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Scripted(params Func<string>[] answers) : IPublicationGuardInvoker
    {
        public int Calls;
        public Task<string> InvokeAsync(RuntimeTransfer transfer, RuntimePublicationGuard guard, CancellationToken ct) => Task.FromResult(answers[Math.Min(Calls++, answers.Length - 1)]());
    }

    private static RuntimePublicationGuard Guard(string plugin = "plex", int maxWait = 30) =>
        new(plugin, "1.1.0", "plex-check-playback", "1.0.0", 1, new Dictionary<string, JsonElement>(), new Dictionary<string, JsonElement>(), maxWait, ["playing"], ["idle"]);

    private static RuntimeTransfer Transfer(params RuntimePublicationGuard[] guards) =>
        new(Guid.NewGuid(), 1, "/media", "Show/e1.mkv", "a", "b", ".mkv", true, null, "video", true, 168, Guid.NewGuid(), guards.Length == 0 ? null : guards);

    private static async Task<(PublicationGuardVerdict Verdict, TimeSpan Waited)> RunAsync(RuntimeTransfer transfer, IPublicationGuardInvoker invoker)
    {
        var now = new DateTimeOffset(2026, 10, 16, 8, 0, 0, TimeSpan.Zero);
        var verdict = await PublicationGuardRunner.EvaluateAsync(transfer, invoker, (delay, _) => { now += delay; return Task.CompletedTask; },
            () => now, NullLogger.Instance, CancellationToken.None);
        return (verdict, now - new DateTimeOffset(2026, 10, 16, 8, 0, 0, TimeSpan.Zero));
    }

    public static async Task RunAsync(FlowComponentRegistry registry)
    {
        // Sans garde configurée, rien n'est consulté.
        var none = await RunAsync(Transfer(), new NoGuardInvoker());
        Check(none.Verdict.Allowed && none.Waited == TimeSpan.Zero, "A transfer without guard replaces immediately.");

        // Libre : autorisé.
        var idle = new Scripted(() => "idle");
        Check((await RunAsync(Transfer(Guard()), idle)).Verdict.Allowed && idle.Calls == 1, "An idle guard allows the replacement.");

        // Lecture puis libre : attente, puis autorisation.
        var later = new Scripted(() => "playing", () => "playing", () => "idle");
        var waited = await RunAsync(Transfer(Guard()), later);
        Check(waited.Verdict.Allowed && later.Calls == 3 && waited.Waited == PublicationGuardRunner.PollInterval * 2, "A guard that waits is asked again until playback ends.");

        // Lecture sans fin : attente plafonnée, source conservée, jamais d'écrasement.
        var forever = new Scripted(() => "playing");
        var capped = await RunAsync(Transfer(Guard(maxWait: 2)), forever);
        Check(!capped.Verdict.Allowed && capped.Verdict.Detail!.Contains("au-delà de 2 min") && capped.Waited <= TimeSpan.FromMinutes(2), "The wait is capped and the source is kept.");

        // Garde indisponible ou sortie inattendue : refus (comportement sûr).
        var down = await RunAsync(Transfer(Guard()), new Scripted(() => throw new IOException("Plex injoignable.")));
        Check(!down.Verdict.Allowed && down.Verdict.Detail!.Contains("indisponible") && down.Waited == TimeSpan.Zero, "An unreachable guard never allows a replacement.");
        var error = await RunAsync(Transfer(Guard()), new Scripted(() => "error"));
        Check(!error.Verdict.Allowed && error.Verdict.Detail!.Contains("refusé"), "An error output refuses the replacement.");

        // Plusieurs gardes : toutes doivent autoriser ; un refus l'emporte sur une attente.
        var mixed = new Scripted(() => "idle", () => "playing", () => "idle", () => "idle");
        Check((await RunAsync(Transfer(Guard("a"), Guard("b")), mixed)).Verdict.Allowed, "Every guard must allow.");
        var refusal = new Scripted(() => "playing", () => "error");
        Check(!(await RunAsync(Transfer(Guard("a"), Guard("b")), refusal)).Verdict.Allowed, "A refusal wins over a wait.");

        // Annulation pendant l'attente : propagée, jamais traduite en autorisation.
        using var cancel = new CancellationTokenSource();
        var cancelled = false;
        try
        {
            await PublicationGuardRunner.EvaluateAsync(Transfer(Guard()), new Scripted(() => "playing"), (_, _) => { cancel.Cancel(); throw new OperationCanceledException(cancel.Token); },
                () => DateTimeOffset.UtcNow, NullLogger.Instance, cancel.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "Cancelling during the wait never allows the replacement.");

        // Blocs de flow (modification sur place, publication) : attente, dernier contrôle sans attente, et Plex injoignable n'empêche pas d'écrire.
        async Task<(PublicationGuardVerdict Verdict, TimeSpan Waited)> StepAsync(IPublicationGuardInvoker invoker, bool wait, int maxWait = 10, RuntimePublicationGuard[]? guards = null)
        {
            var clock = new DateTimeOffset(2026, 10, 16, 8, 0, 0, TimeSpan.Zero); var now = clock;
            var verdict = await PublicationGuardRunner.CheckForStepAsync(Guid.NewGuid(), 1, "Show/e1.mkv", null, guards ?? [Guard(maxWait: maxWait)], wait, invoker,
                (delay, _) => { now += delay; return Task.CompletedTask; }, () => now, NullLogger.Instance, CancellationToken.None);
            return (verdict, now - clock);
        }
        var stepIdle = await StepAsync(new Scripted(() => "playing", () => "playing", () => "idle"), true);
        Check(stepIdle.Verdict.Allowed && stepIdle.Waited == PublicationGuardRunner.PollInterval * 2, "A step waits for the playback to end, checking every 30 s.");
        var stepCapped = await StepAsync(new Scripted(() => "playing"), true, maxWait: 2);
        Check(!stepCapped.Verdict.Allowed && stepCapped.Verdict.Detail!.Contains("Écriture non effectuée") && stepCapped.Waited <= TimeSpan.FromMinutes(2), "A step refuses when the playback lasts past the maximum wait.");
        var lastCheck = await StepAsync(new Scripted(() => "playing"), false);
        Check(!lastCheck.Verdict.Allowed && lastCheck.Waited == TimeSpan.Zero, "The last check before writing never waits: a playback that started refuses at once.");
        Check((await StepAsync(new Scripted(() => "idle"), false)).Verdict.Allowed, "The last check allows an idle file.");
        Check((await StepAsync(new Scripted(() => throw new IOException("Plex injoignable.")), true)).Verdict.Allowed, "An unreachable Plex does not prevent a step from writing.");
        Check((await StepAsync(new Scripted(() => "error"), true)).Verdict.Allowed, "A guard without an opinion (no path mapping) does not prevent a step from writing.");
        Check((await StepAsync(new NoGuardInvoker(), true, guards: [])).Verdict.Allowed, "Without a configured guard a step writes immediately.");

        // Le contrat survit au transport JSON entre serveur et nœud d'accès.
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var roundTrip = JsonSerializer.Deserialize<RuntimeTransfer>(JsonSerializer.Serialize(Transfer(Guard()), json), json)!;
        Check(roundTrip.Guards is { Count: 1 } && roundTrip.Guards[0].WaitOutputs.SequenceEqual(["playing"]) && roundTrip.LibraryId is not null, "Guards travel with the transfer.");

        // Catalogue : Plex 1.1.0 garde les références 1.0.0 et ajoute l'analyse.
        var plex = registry.Plugins.Single(item => item.Id == "plex");
        Check(plex.Version == "1.1.0" && plex.PublicationGuard is { ComponentId: "plex-check-playback" }, "Plex 1.1.0 declares its publication guard.");
        Check(registry.Resolve(new("plex", "1.0.0", "plex-check-playback", "1.0.0", 1)) is not null && registry.Resolve(new("plex", "1.1.0", "plex-analyze-media", "1.1.0", 1)) is not null,
            "Flows that reference Plex 1.0.0 stay valid and the analysis block exists in 1.1.0.");
        var analyze = plex.Components.Single(item => item.Id == "plex-analyze-media");
        Check(analyze.Outputs.Select(item => item.Id).SequenceEqual(["success", "notFound", "error"]), "The analysis block has its three outputs.");
    }
}
