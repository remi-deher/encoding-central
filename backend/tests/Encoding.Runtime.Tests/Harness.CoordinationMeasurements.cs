using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Encoding.Api.Infrastructure;
using Encoding.Contracts.Executions;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Infrastructure.Data.Sqlite;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Encoding.Runtime.Tests;

internal static partial class Harness
{
    internal sealed partial class Fixture
    {
        public async Task<object> MeasureCoordinationAsync(bool concurrentCapture, int queued = 128, int workerNodes = 1, int claimsPerClient = 4, bool readOverview = false)
        {
            if (queued < 32 || workerNodes is < 1 or > 2 || claimsPerClient < 1 || claimsPerClient * 8 > queued)
                throw new ArgumentOutOfRangeException(nameof(queued));
            var setup = Stopwatch.StartNew();
            var library = Guid.NewGuid();
            await using (var seed = db.CreateCommand("""
                INSERT INTO libraries(id,name) VALUES(@library,@name);
                INSERT INTO library_node_accesses(library_id,node_id,mode,local_path,can_read,can_write)
                    VALUES(@library,@worker,'direct',@root,true,true);
                """))
            {
                seed.Parameters.AddWithValue("library", library); seed.Parameters.AddWithValue("name", library.ToString());
                seed.Parameters.AddWithValue("worker", a); seed.Parameters.AddWithValue("root", directory);
                await seed.ExecuteNonQueryAsync();
            }
            if (workerNodes == 2)
            {
                await using var access = db.CreateCommand("INSERT INTO library_node_accesses(library_id,node_id,mode,local_path,can_read,can_write) VALUES(@library,@worker,'direct',@root,true,true)");
                access.Parameters.AddWithValue("library", library); access.Parameters.AddWithValue("worker", b); access.Parameters.AddWithValue("root", directory);
                await access.ExecuteNonQueryAsync();
            }
            var flow = Chain(Node(registry, "entry", "base-input-file"), Node(registry, "end", "base-complete-flow"));
            for (var i = 0; i < queued; i++)
                Check(await runtime.EnqueueAsync(new(Guid.NewGuid(), library, $"sample-{i}.mkv", flow.Id, flow.Version), flow, CancellationToken.None), "Queue measurement workload.");
            await new ClusterReconciliationRepository(db).EnsureIdentityAsync(CancellationToken.None);
            var cluster = await new ClusterIdentityRepository(db).GetAsync(CancellationToken.None);
            using var keys = new ClusterDataKeyStore(cluster.Id, RandomNumberGenerator.GetBytes(32));
            var capture = new ClusterEntityReconciliation(db, keys, new EphemeralDataProtectionProvider(), NullLogger<ClusterEntityReconciliation>.Instance);
            await capture.CaptureLocalChangesAsync(CancellationToken.None);
            setup.Stop();
            var writer = ((SqliteDataSource)db).Writer;
            var beforeIdle = writer.Snapshot();
            var idle = Stopwatch.StartNew();
            for (var i = 0; i < 8; i++) Check(await capture.CaptureLocalChangesAsync(CancellationToken.None) == 0, "Idle capture has no events.");
            idle.Stop();
            Check(writer.Snapshot().Admitted == beforeIdle.Admitted, "Idle capture does not acquire writer admission.");

            var claims = new ConcurrentBag<double>();
            var renewals = new ConcurrentBag<double>();
            var executionIds = new ConcurrentBag<Guid>();
            var overviews = new ConcurrentBag<double>();
            var details = new ConcurrentBag<double>();
            using var start = new ManualResetEventSlim();
            using var ready = new CountdownEvent(8 + (concurrentCapture ? 1 : 0) + (readOverview ? 1 : 0));
            Task Launch(Func<Task> work) => Task.Factory.StartNew(async () =>
            {
                ready.Signal();
                start.Wait();
                await work();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            var jobs = Enumerable.Range(0, 8).Select(client => Launch(async () =>
            {
                var worker = workerNodes == 2 && client % 2 == 1 ? b : a;
                for (var i = 0; i < claimsPerClient; i++)
                {
                    var claimStarted = Stopwatch.GetTimestamp();
                    var lease = await runtime.ClaimAsync(worker, CancellationToken.None);
                    claims.Add(Stopwatch.GetElapsedTime(claimStarted).TotalMilliseconds);
                    Check(lease is not null, "Every measurement claim succeeds.");
                    executionIds.Add(lease!.ExecutionId);
                    for (var renewal = 0; renewal < 4; renewal++)
                    {
                        var renewStarted = Stopwatch.GetTimestamp();
                        Check(await runtime.RenewAsync(lease.ExecutionId, worker, lease.Epoch, CancellationToken.None) is not null, "Renewal succeeds under capture load.");
                        renewals.Add(Stopwatch.GetElapsedTime(renewStarted).TotalMilliseconds);
                    }
                }
            })).ToList();
            if (concurrentCapture) jobs.Add(Launch(async () =>
            {
                for (var i = 0; i < 16; i++)
                {
                    await using var change = db.CreateCommand("UPDATE libraries SET name=@name WHERE id=@library");
                    change.Parameters.AddWithValue("library", library);
                    change.Parameters.AddWithValue("name", library + "-" + i);
                    await change.ExecuteNonQueryAsync();
                    await capture.CaptureLocalChangesAsync(CancellationToken.None);
                }
            }));
            if (readOverview) jobs.Add(Launch(async () =>
            {
                for (var page = 0; page < 64; page++)
                {
                    var observed = Stopwatch.GetTimestamp();
                    var overview = await executions.OverviewAsync(new ExecutionFilterQuery(Limit: 50, Offset: page * 50 % queued), CancellationToken.None);
                    overviews.Add(Stopwatch.GetElapsedTime(observed).TotalMilliseconds);
                    Check(overview.Executions.Count <= 50 && overview.TotalCount == queued, "The overview remains paginated and counts the entire queue.");
                    if (overview.Executions.FirstOrDefault() is { } execution)
                    {
                        observed = Stopwatch.GetTimestamp();
                        Check(await executions.GetAsync(execution.Id, CancellationToken.None) is not null, "A requested execution detail remains readable.");
                        await executions.EventsAsync(execution.Id, null, 50, CancellationToken.None);
                        details.Add(Stopwatch.GetElapsedTime(observed).TotalMilliseconds);
                    }
                }
            }));
            Check(ready.Wait(TimeSpan.FromSeconds(5)), "All synthetic clients are ready before measurement starts.");
            var before = writer.Snapshot();
            var elapsed = Stopwatch.StartNew();
            start.Set();
            await Task.WhenAll(jobs);
            elapsed.Stop();
            var after = writer.Snapshot();
            Check(executionIds.Distinct().Count() == 8 * claimsPerClient && renewals.Count == 32 * claimsPerClient, "No claim is duplicated and all renewals complete.");
            Check(after.Rejected == before.Rejected && after.Expired == before.Expired, "The workload neither saturates nor expires writer admission.");
            await capture.CaptureLocalChangesAsync(CancellationToken.None);

            static object? Distribution(IEnumerable<double> values)
            {
                var ordered = values.Order().ToArray();
                if (ordered.Length == 0) return null;
                double At(double percentile) => Math.Round(ordered[(int)Math.Ceiling(percentile * ordered.Length) - 1], 3);
                return new { count = ordered.Length, p50_ms = At(.5), p95_ms = At(.95), p99_ms = At(.99), max_ms = Math.Round(ordered[^1], 3) };
            }
            return new
            {
                concurrent_capture = concurrentCapture, clients = 8, active_worker_nodes = workerNodes, queued, claims_per_client = claimsPerClient,
                setup_ms = Math.Round(setup.Elapsed.TotalMilliseconds, 3),
                elapsed_ms = Math.Round(elapsed.Elapsed.TotalMilliseconds, 3), idle_capture_8_ms = Math.Round(idle.Elapsed.TotalMilliseconds, 3),
                idle_writer_admissions = 0, claim = Distribution(claims), renew = Distribution(renewals),
                overview = Distribution(overviews), detail_and_events = Distribution(details),
                writer_admissions = after.Admitted - before.Admitted,
                writer_wait_ms = Math.Round((after.WaitSeconds - before.WaitSeconds) * 1000, 3),
                writer_hold_ms = Math.Round((after.HoldSeconds - before.HoldSeconds) * 1000, 3),
                writer_long_holds = after.LongHolds - before.LongHolds,
                writer_rejections = after.Rejected - before.Rejected, writer_expirations = after.Expired - before.Expired
            };
        }
    }
}
