using System.Text.Json;
using Encoding.Api.Executions;
using Encoding.Api.Flows;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Plugins;
using Encoding.Api.Workers;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Workers;
using Encoding.WorkerAgent.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>Chaque refus de steps/start et steps/complete renvoie un code stable, journalisé en Warning (voir StepRefusalCodes).</summary>
public sealed class StepRefusalTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private sealed class CaptureLogger : ILogger<RuntimeRepository>
    {
        public readonly List<(LogLevel Level, string Message)> Entries = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((level, formatter(state, exception)));
    }

    private sealed class Scenario : IAsyncDisposable
    {
        public required TestDatabase Database { get; init; }
        public required RuntimeRepository Runtime { get; init; }
        public required CaptureLogger Logger { get; init; }
        public required Guid ExecutionId { get; init; }
        public required Guid WorkerId { get; init; }
        public required long Epoch { get; init; }
        public required StartedRuntimeStep Started { get; init; }
        public required int Revision { get; init; }

        public ValueTask DisposeAsync() => Database.DisposeAsync();

        public RuntimeCheckpoint Checkpoint(Func<RuntimeCheckpoint, RuntimeCheckpoint>? edit = null)
        {
            var context = new RuntimeContext(Sha, ".mkv", new Dictionary<string, JsonElement>(), null, null, []);
            var checkpoint = new RuntimeCheckpoint("input", Started.InvocationId, "success", "remux", context, null);
            return edit is null ? checkpoint : edit(checkpoint);
        }

        public CompleteRuntimeStep Request(RuntimeCheckpoint? checkpoint = null, long? epoch = null, int? revision = null) =>
            new(epoch ?? Epoch, Guid.NewGuid(), revision ?? Revision, checkpoint ?? Checkpoint());

        public async Task<string?> RefusalAsync(CompleteRuntimeStep request, Guid? worker = null) =>
            (await Runtime.TryCompleteStepAsync(ExecutionId, worker ?? WorkerId, request, CancellationToken.None))?.Code;

        public async Task ExecuteAsync(string sql)
        {
            await using var command = Database.Db.CreateCommand(sql);
            command.Parameters.AddWithValue("id", ExecutionId);
            await command.ExecuteNonQueryAsync();
        }

        public static async Task<Scenario> CreateAsync(string name)
        {
            var database = await TestDatabase.CreateSqliteAsync(name);
            await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);
            var (registry, _, eligibility) = Harness.Services();
            var logger = new CaptureLogger();
            var runtime = new RuntimeRepository(database.Db, eligibility, registry, new WorkerPresenceOptions(TimeSpan.FromHours(1)),
                new PluginConfigurationRepository(database.Db, new EphemeralDataProtectionProvider()), new WorkerGroupRepository(database.Db),
                placementPlanner: new FlowPlacementPlanner(registry), logger: logger);
            var library = Guid.NewGuid(); var worker = Guid.NewGuid(); var id = Guid.NewGuid();
            WorkerToolCapability[] capabilities =
            [
                new("plugin/base", "1.9.0", ["general"], []), new("ffprobe", "7.0.0", ["probe"], []),
                new("ffmpeg", "7.0.0", ["video", "audio"], ["cpu"], ["libx264", "aac"]), new("mkvmerge", "85.0.0", ["mkv"], []),
            ];
            await using (var c = await database.Db.OpenConnectionAsync())
            await using (var tx = await c.BeginTransactionAsync())
            {
                await using var insert = Sql.Command("""
                    INSERT INTO libraries(id, name, is_active) VALUES(@library, 'Refusals', true);
                    INSERT INTO workers(id, name, roles, max_concurrent_tasks, capabilities, last_seen_at)
                    VALUES(@worker, 'Direct', json_array('worker'), 4, @capabilities, (strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z'));
                    INSERT INTO library_worker_accesses(library_id, worker_id, mode, local_path, can_read, can_write, is_enabled, transfer_source_worker_id)
                    VALUES(@library, @worker, 'direct', '/media/library', true, true, true, NULL);
                    """, c, tx);
                insert.Parameters.AddWithValue("library", library); insert.Parameters.AddWithValue("worker", worker);
                insert.Parameters.AddWithValue("capabilities", SqlKind.Json, JsonSerializer.Serialize(capabilities, Json));
                await insert.ExecuteNonQueryAsync(); await tx.CommitAsync();
            }
            FlowNodeDefinition Node(string nodeId, string componentId) =>
                new(nodeId, nodeId, registry.FindReference(componentId) ?? throw new InvalidOperationException(componentId), new Dictionary<string, JsonElement>());
            FlowNodeDefinition[] nodes = [Node("input", "base-input-file"), Node("remux", "base-remux-media"), Node("complete", "base-complete-flow")];
            var flow = new FlowDefinition(1, Guid.NewGuid(), 1, "refusals", "video", nodes,
                [new("conn_0", "input", "success", "remux"), new("conn_1", "remux", "success", "complete")]);
            await using (var c = await database.Db.OpenConnectionAsync())
            await using (var tx = await c.BeginTransactionAsync())
            {
                Assert.True(await RuntimeRepository.EnqueueInTransactionAsync(new(id, library, "movie.mkv", flow.Id, flow.Version), flow, c, tx, CancellationToken.None));
                await tx.CommitAsync();
            }
            var lease = await runtime.ClaimAsync(worker, CancellationToken.None);
            Assert.NotNull(lease);
            var started = await runtime.StartStepAsync(id, worker, new(lease.Epoch, "input"), CancellationToken.None);
            Assert.NotNull(started);
            await using (var artifact = database.Db.CreateCommand("INSERT INTO execution_artifacts(execution_id,sha256,byte_length) VALUES(@id,@sha,1)"))
            { artifact.Parameters.AddWithValue("id", id); artifact.Parameters.AddWithValue("sha", Sha); await artifact.ExecuteNonQueryAsync(); }
            var status = await runtime.RenewAsync(id, worker, lease.Epoch, CancellationToken.None);
            Assert.NotNull(status);
            return new Scenario
            {
                Database = database, Runtime = runtime, Logger = logger, ExecutionId = id, WorkerId = worker,
                Epoch = lease.Epoch, Started = started, Revision = status.Revision,
            };
        }
    }

    private static RuntimeCommandLog Command(Guid? id = null, string standardOutput = "") =>
        new(id ?? Guid.NewGuid(), "ffmpeg", standardOutput, "", 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, false);

    private static RuntimeCheckpoint Shape(Func<RuntimeContext, RuntimeContext>? context = null, string nodeId = "input", Guid? invocation = null)
    {
        var value = new RuntimeContext(Sha, ".mkv", new Dictionary<string, JsonElement>(), null, null, []);
        return new RuntimeCheckpoint(nodeId, invocation ?? Guid.NewGuid(), "success", "remux", context is null ? value : context(value), null);
    }

    // ---- Forme du checkpoint (sans accès base) : un code par motif.

    [Fact]
    public void ShapeRefusalsEachHaveTheirOwnCode()
    {
        IReadOnlyList<RuntimeCommandLog> none = [];
        Assert.Null(RuntimeRepository.ValidateCheckpointShape(Shape(), none));
        Assert.Equal(StepRefusalCodes.CheckpointMissing, RuntimeRepository.ValidateCheckpointShape(null, none));
        Assert.Equal(StepRefusalCodes.CheckpointMissing, RuntimeRepository.ValidateCheckpointShape(Shape(nodeId: " "), none));
        Assert.Equal(StepRefusalCodes.CheckpointMissing, RuntimeRepository.ValidateCheckpointShape(Shape(invocation: Guid.Empty), none));
        Assert.Equal(StepRefusalCodes.ContextIncomplete, RuntimeRepository.ValidateCheckpointShape(Shape(c => c with { Plan = null! }), none));
        Assert.Equal(StepRefusalCodes.ContextIncomplete, RuntimeRepository.ValidateCheckpointShape(Shape(c => c with { Variables = null! }), none));
        Assert.Equal(StepRefusalCodes.VideoEncodingsLimit,
            RuntimeRepository.ValidateCheckpointShape(Shape(c => c with { VideoEncodings = new ResolvedVideoEncoding[65] }), none));
        Assert.Equal(StepRefusalCodes.SourceDigestInvalid, RuntimeRepository.ValidateCheckpointShape(Shape(c => c with { CurrentSha256 = "xyz" }), none));
        Assert.Equal(StepRefusalCodes.SourceDigestInvalid, RuntimeRepository.ValidateCheckpointShape(Shape(c => c with { CurrentSha256 = "" }), none));
        Assert.Equal(StepRefusalCodes.ExtensionInvalid, RuntimeRepository.ValidateCheckpointShape(Shape(c => c with { Extension = "mkv" }), none));
        Assert.Equal(StepRefusalCodes.ExtensionInvalid, RuntimeRepository.ValidateCheckpointShape(Shape(c => c with { Extension = null! }), none));
    }

    [Fact]
    public void CommandJournalRefusalsEachHaveTheirOwnCode()
    {
        var checkpoint = Shape();
        Assert.Null(RuntimeRepository.ValidateCheckpointShape(checkpoint, [Command()]));
        Assert.Equal(StepRefusalCodes.CommandsLimit, RuntimeRepository.ValidateCheckpointShape(checkpoint, Enumerable.Range(0, 65).Select(_ => Command()).ToArray()));
        Assert.Equal(StepRefusalCodes.CommandLogInvalid, RuntimeRepository.ValidateCheckpointShape(checkpoint, [Command(standardOutput: new string('x', 32769))]));
        var repeated = Guid.NewGuid();
        Assert.Equal(StepRefusalCodes.CommandIdDuplicate, RuntimeRepository.ValidateCheckpointShape(checkpoint, [Command(repeated), Command(repeated)]));
        // 64 journaux valides de 32 Ko chacun dépassent ensemble 512 Ko sérialisés.
        var heavy = Enumerable.Range(0, 64).Select(_ => Command(standardOutput: new string('x', 32768))).ToArray();
        Assert.Equal(StepRefusalCodes.CommandsTooLarge, RuntimeRepository.ValidateCheckpointShape(checkpoint, heavy));
    }

    [Fact]
    public async Task ShapeRefusalIsReturnedWithoutTouchingTheDatabase()
    {
        await using var s = await Scenario.CreateAsync("refusal-shape");
        var bad = s.Checkpoint(c => c with { Context = c.Context with { Extension = "mkv" } });
        Assert.Equal(StepRefusalCodes.ExtensionInvalid, await s.RefusalAsync(s.Request(bad)));
    }

    // ---- Bail et état de l'exécution.

    [Fact]
    public async Task LeaseRefusalsAreDistinguished()
    {
        await using var s = await Scenario.CreateAsync("refusal-lease");
        Assert.Equal(StepRefusalCodes.LeaseEpochMismatch, await s.RefusalAsync(s.Request(epoch: s.Epoch + 1)));
        Assert.Equal(StepRefusalCodes.LeaseWrongWorker, await s.RefusalAsync(s.Request(), worker: Guid.NewGuid()));
        var unknown = await s.Runtime.TryCompleteStepAsync(Guid.NewGuid(), s.WorkerId, s.Request(), CancellationToken.None);
        Assert.Equal(StepRefusalCodes.ExecutionUnknown, unknown?.Code);

        await s.ExecuteAsync("DELETE FROM media_reservations WHERE execution_id=@id");
        Assert.Equal(StepRefusalCodes.ReservationLost, await s.RefusalAsync(s.Request()));
        await s.ExecuteAsync("UPDATE execution_work SET lease_until='2000-01-01 00:00:00.000000Z' WHERE execution_id=@id");
        Assert.Equal(StepRefusalCodes.LeaseExpired, await s.RefusalAsync(s.Request()));
        await s.ExecuteAsync("UPDATE executions SET state='failed' WHERE id=@id");
        Assert.Equal(StepRefusalCodes.ExecutionNotActive, await s.RefusalAsync(s.Request()));
    }

    [Fact]
    public async Task ReplayedCheckpointIsAcknowledgedButADifferentOneIsRefused()
    {
        await using var s = await Scenario.CreateAsync("refusal-duplicate");
        var request = s.Request();
        Assert.Null(await s.RefusalAsync(request));
        Assert.Null(await s.RefusalAsync(request with { EventId = Guid.NewGuid() }));
        var changed = s.Checkpoint(c => c with { Context = c.Context with { Extension = ".mp4" } });
        Assert.Equal(StepRefusalCodes.DuplicateCheckpointDiffers, await s.RefusalAsync(s.Request(changed)));
    }

    [Fact]
    public async Task StaleRevisionIsReportedAsRevisionConflict()
    {
        await using var s = await Scenario.CreateAsync("refusal-revision");
        Assert.Equal(StepRefusalCodes.RevisionConflict, await s.RefusalAsync(s.Request(revision: s.Revision + 5)));
    }

    // ---- Enchaînement du flow.

    [Fact]
    public async Task FlowSequenceRefusalsAreDistinguished()
    {
        await using var s = await Scenario.CreateAsync("refusal-sequence");
        Assert.Equal(StepRefusalCodes.NodeUnknown, await s.RefusalAsync(s.Request(s.Checkpoint(c => c with { NodeId = "ghost" }))));
        Assert.Equal(StepRefusalCodes.OutputInvalid, await s.RefusalAsync(s.Request(s.Checkpoint(c => c with { OutputId = "bogus" }))));
        Assert.Equal(StepRefusalCodes.NextNodeMismatch, await s.RefusalAsync(s.Request(s.Checkpoint(c => c with { NextNodeId = "complete" }))));
        Assert.Equal(StepRefusalCodes.StepNotStarted, await s.RefusalAsync(s.Request(s.Checkpoint(c => c with { InvocationId = Guid.NewGuid() }))));
    }

    [Fact]
    public async Task StartRefusalsCarryACodeToo()
    {
        await using var s = await Scenario.CreateAsync("refusal-start");
        var outOfOrder = await s.Runtime.TryStartStepAsync(s.ExecutionId, s.WorkerId, new(s.Epoch, "complete"), CancellationToken.None);
        Assert.Null(outOfOrder.Step); Assert.Equal(StepRefusalCodes.StepOutOfOrder, outOfOrder.Refusal?.Code);
        var unknown = await s.Runtime.TryStartStepAsync(s.ExecutionId, s.WorkerId, new(s.Epoch, "ghost"), CancellationToken.None);
        Assert.Equal(StepRefusalCodes.NodeUnknown, unknown.Refusal?.Code);
        var stale = await s.Runtime.TryStartStepAsync(s.ExecutionId, s.WorkerId, new(s.Epoch + 1, "input"), CancellationToken.None);
        Assert.Equal(StepRefusalCodes.LeaseEpochMismatch, stale.Refusal?.Code);
        var again = await s.Runtime.TryStartStepAsync(s.ExecutionId, s.WorkerId, new(s.Epoch, "input"), CancellationToken.None);
        Assert.NotNull(again.Step); Assert.Null(again.Refusal);
    }

    // ---- Source et artefacts.

    [Fact]
    public async Task ArtifactRefusalsAreDistinguished()
    {
        await using var s = await Scenario.CreateAsync("refusal-artifacts");
        Assert.Equal(StepRefusalCodes.ArtifactMissing, await s.RefusalAsync(s.Request(s.Checkpoint(c => c with { Context = c.Context with { CurrentSha256 = OtherSha } }))));

        RuntimeCheckpoint WithExtra(object extra) => s.Checkpoint(c => c with
        {
            Context = c.Context with { Variables = new Dictionary<string, JsonElement> { ["artifact.fonts"] = JsonSerializer.SerializeToElement(extra, Json) } },
        });
        Assert.Equal(StepRefusalCodes.ExtraArtifactInvalid, await s.RefusalAsync(s.Request(WithExtra(new { sha256 = Sha, name = "a/b.ttf" }))));
        Assert.Equal(StepRefusalCodes.ExtraArtifactMissing, await s.RefusalAsync(s.Request(WithExtra(new { sha256 = OtherSha, name = "b.ttf" }))));
    }

    [Fact]
    public async Task DirectSourceCheckpointThatMutatesIsRefused()
    {
        await using var s = await Scenario.CreateAsync("refusal-direct");
        var direct = s.Checkpoint(c => c with
        {
            Context = c.Context with { CurrentSha256 = "", DirectSource = new RuntimeSourceObservation(1, 1) },
            Handoff = new RuntimeHandoffSelector("any", null),
        });
        Assert.Equal(StepRefusalCodes.DirectSourceNotAllowed, await s.RefusalAsync(s.Request(direct)));
    }

    // ---- Relais et attentes.

    [Fact]
    public async Task HandoffAndWaitRefusalsAreDistinguished()
    {
        await using var s = await Scenario.CreateAsync("refusal-handoff");
        Assert.Equal(StepRefusalCodes.PlacementHandoffUnexpected, await s.RefusalAsync(s.Request(s.Checkpoint(c => c with { PlacementHandoff = true }))));
        Assert.Equal(StepRefusalCodes.PlacementHandoffInvalid,
            await s.RefusalAsync(s.Request(s.Checkpoint(c => c with { Handoff = new RuntimeHandoffSelector("any", null) }))));
        Assert.Equal(StepRefusalCodes.WaitUnexpected, await s.RefusalAsync(s.Request(s.Checkpoint(c => c with { Wait = new RuntimeWait("delay", 1) }))));
    }

    // ---- Journalisation et corps du 409.

    [Fact]
    public async Task RefusalIsLoggedAsWarningWithItsContext()
    {
        await using var s = await Scenario.CreateAsync("refusal-log");
        Assert.Equal(StepRefusalCodes.LeaseEpochMismatch, await s.RefusalAsync(s.Request(epoch: s.Epoch + 1)));
        var (level, message) = Assert.Single(s.Logger.Entries.Where(entry => entry.Message.Contains(StepRefusalCodes.LeaseEpochMismatch)));
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("steps/complete", message);
        Assert.Contains(s.ExecutionId.ToString(), message);
        Assert.Contains(s.WorkerId.ToString(), message);
        Assert.Contains((s.Epoch + 1).ToString(), message);
        Assert.Contains("input", message);
    }

    [Fact]
    public void EveryRefusalCodeIsShortStableAndUnique()
    {
        var codes = typeof(StepRefusalCodes).GetFields().Where(field => field.IsLiteral).Select(field => (string)field.GetRawConstantValue()!).ToArray();
        Assert.Equal(codes.Length, codes.Distinct().Count());
        Assert.All(codes, code => Assert.Matches("^[a-z][a-z0-9_]{0,63}$", code));
        Assert.Equal(StepRefusalCodes.LeaseExpired, "lease_expired");
        Assert.Equal(StepRefusalCodes.ArtifactMissing, "artifact_missing");
    }

    [Fact]
    public void RefusalBodyIsCamelCaseReasonObject() =>
        Assert.Equal("{\"reason\":\"artifact_missing\"}", JsonSerializer.Serialize(new RuntimeRefusal(StepRefusalCodes.ArtifactMissing), Json));

    // ---- Côté worker : le code figure dans le message cluster_refused.

    [Fact]
    public void WorkerReadsTheReasonFromTheConflictBody()
    {
        var conflict = new HttpRequestException(
            "Response status code does not indicate success: 409 (Conflict). Content: {\"reason\":\"artifact_missing\"}", null, System.Net.HttpStatusCode.Conflict);
        Assert.Equal("artifact_missing", RuntimeRefusalReason.From(conflict));
        var message = RuntimeRefusalReason.Describe(conflict, "Le central a refusé une étape.");
        Assert.StartsWith("cluster_refused : ", message);
        Assert.Contains("(motif : artifact_missing)", message);
    }

    [Theory]
    [InlineData("Response status code does not indicate success: 409 (Conflict). Content: ")]
    [InlineData("Response status code does not indicate success: 409 (Conflict). Content: not json")]
    [InlineData("Response status code does not indicate success: 409 (Conflict). Content: {\"reason\":\"Pas un code !\"}")]
    [InlineData("Response status code does not indicate success: 409 (Conflict). Content: [\"artifact_missing\"]")]
    [InlineData("409")]
    public void WorkerFallsBackToTheGenericMessageWithoutAUsableReason(string raw)
    {
        var conflict = new HttpRequestException(raw, null, System.Net.HttpStatusCode.Conflict);
        Assert.Null(RuntimeRefusalReason.From(conflict));
        Assert.Equal("cluster_refused : Texte.", RuntimeRefusalReason.Describe(conflict, "Texte."));
    }
}
