using Encoding.Api.Infrastructure.Data;

namespace Encoding.Runtime.Tests;

internal static partial class Harness
{
    internal sealed partial class Fixture
    {
        public async Task LegacyPendingResumptionIsClosedAsync(string state, bool finiteReservation, bool uncertain, bool retryPolicy)
        {
            var flow = retryPolicy
                ? Chain(Node(registry, "entry", "base-input-file"), Node(registry, "retry", "nodes-retry-on-worker-loss"), Node(registry, "end", "base-complete-flow"))
                : Chain(Node(registry, "entry", "base-input-file"), Node(registry, "variable", "base-variable", new() { ["name"] = J("kept"), ["value"] = J("7") }), Node(registry, "end", "base-complete-flow"));
            var (id, lease, context) = await EnqueueAsync(flow);
            await CompleteAsync(id, a, lease, "entry", context);
            if (uncertain)
            {
                Check(await runtime.StartStepAsync(id, a, new(lease.Epoch, "variable"), CancellationToken.None) is not null, "Start the interrupted step.");
                await using var mark = db.CreateCommand("UPDATE execution_steps SET idempotent=false WHERE execution_id=@id AND flow_node_id='variable'");
                mark.Parameters.AddWithValue("id", id);
                await mark.ExecuteNonQueryAsync();
            }
            Check(await Locks.TryAcquireAsync(id, a, new(lease.Epoch, "legacy-resume-" + id.ToString("N")), CancellationToken.None) is { Acquired: true }, "Acquire a disk lock before simulating an upgrade.");
            var sourcePath = artifacts.PathFor(id, context.CurrentSha256);
            var source = await File.ReadAllBytesAsync(sourcePath);
            // Ancien TryResumeAsync : même identifiant, compteur positif, worker libéré et réservation conservée.
            await using (var legacy = db.CreateCommand("""
                UPDATE executions SET state=@state WHERE id=@id;
                UPDATE execution_work SET worker_id=NULL,handoff_pending=true,resume_count=1,
                  handoff_selector='{"mode":"eligible","resumed":true}',previous_worker_id=@worker,
                  lease_epoch=lease_epoch+1,
                  lease_until=NULL
                WHERE execution_id=@id;
                UPDATE media_reservations SET lease_epoch=lease_epoch+1,
                  lease_until=CASE WHEN @finite THEN (strftime('%Y-%m-%d %H:%M:%f', 'now', '+1 hour') || '000Z') ELSE '9999-12-31 23:59:59.999999Z' END
                WHERE execution_id=@id;
                """))
            {
                legacy.Parameters.AddWithValue("id", id);
                legacy.Parameters.AddWithValue("worker", a);
                legacy.Parameters.AddWithValue("state", state);
                legacy.Parameters.AddWithValue("finite", finiteReservation);
                await legacy.ExecuteNonQueryAsync();
            }
            Check(await runtime.RenewAsync(id, a, lease.Epoch, CancellationToken.None) is null, "Fence the previous worker before cleanup.");
            Check(await runtime.ClaimAsync(b, CancellationToken.None) is null, "A persisted automatic resumption must not be claimable before recovery runs.");
            var recovery = FullRecovery();
            Check(await recovery.ProcessOnceAsync(CancellationToken.None) == 1, "Recovery must detect the persisted resumption without an expired lease.");
            var closed = (await executions.GetAsync(id, CancellationToken.None))!;
            Check(closed.State == (state == "cancelling" ? "cancelled" : "failed"), "The old resumption must become terminal.");
            if (state != "cancelling")
                Check(closed.Detail?.Contains("worker_perdu", StringComparison.Ordinal) == true, "The failure must explain the lost worker.");
            Check(await runtime.ClaimAsync(b, CancellationToken.None) is null, "No worker may continue the same execution after recovery.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM media_reservations WHERE execution_id=@id", ("id", id)) == 0, "Release the retained media reservation.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM disk_locks WHERE execution_id=@id", ("id", id)) == 0, "Release the disk lock.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM executions WHERE retry_of=@id", ("id", id)) == 0, "Do not silently create a fresh attempt for a legacy resumption.");
            Check(await runtime.RenewAsync(id, a, lease.Epoch, CancellationToken.None) is null, "The old worker remains fenced.");
            var preserved = await File.ReadAllBytesAsync(sourcePath);
            Check(source.SequenceEqual(preserved), "Preserve the source artifact.");
            Check(await recovery.ProcessOnceAsync(CancellationToken.None) == 0, "Closing an old resumption is idempotent.");
        }
    }
}
