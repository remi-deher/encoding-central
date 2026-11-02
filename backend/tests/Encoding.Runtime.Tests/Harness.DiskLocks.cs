using System.Text.Json;
using Encoding.Api.Executions;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Microsoft.Extensions.Logging.Abstractions;

namespace Encoding.Runtime.Tests;

internal static partial class Harness
{
    internal sealed partial class Fixture
    {
        private DiskLockRepository Locks => new(db);
        private Task<string?> LockHolderAsync(string disk) => ScalarAsync<string>("SELECT CAST(execution_id AS varchar(36)) FROM disk_locks WHERE disk_key=@disk", ("disk", disk));

        public async Task DiskLocksAsync()
        {
            var simple = WaitFlow(new() { ["mode"] = J("timer"), ["delayMinutes"] = J(30) });
            var (x, lx, _) = await EnqueueAsync(simple);
            var (y, ly, _) = await EnqueueAsync(simple);
            var token = CancellationToken.None;
            DiskLockRequest For(RuntimeLease lease, string disk) => new(lease.Epoch, disk);

            Check(DiskLockKey.Normalize("  NAS-1 ") == "nas-1", "A disk name is trimmed and lowercased.");
            foreach (var bad in new[] { "", "   ", "-x", "a/b", "../etc", new string('a', 65) })
                Check(Throws(() => DiskLockKey.Normalize(bad)), "An invalid disk name is refused: '" + bad + "'.");

            var first = await Locks.TryAcquireAsync(x, a, For(lx, "nas-1"), token);
            Check(first is { Acquired: true, Disk: "nas-1" }, "The first execution takes the disk.");
            Check((await Locks.TryAcquireAsync(x, a, For(lx, "NAS-1"), token)) is { Acquired: true }, "Taking its own lock again is idempotent.");
            var busy = await Locks.TryAcquireAsync(y, a, For(ly, "nas-1"), token);
            Check(busy is { Acquired: false } && busy.HolderExecutionId == x, "A second execution is told the disk is busy and who holds it.");
            Check((await Locks.TryAcquireAsync(y, a, For(ly, "nas-2"), token)) is { Acquired: true }, "Another disk is independent.");
            Check(await Locks.TryAcquireAsync(x, a, new(lx.Epoch + 1, "nas-3"), token) is null, "A stale epoch cannot take a lock.");
            Check(await Locks.TryAcquireAsync(x, b, For(lx, "nas-3"), token) is null, "Another worker cannot take a lock for this execution.");

            // Un worker qui ne tient pas le verrou ne peut pas le libérer.
            Check(await Locks.ReleaseAsync(y, a, For(ly, "nas-1"), token) == false && await LockHolderAsync("nas-1") == x.ToString(), "Releasing a disk held by someone else changes nothing.");
            Check(await Locks.ReleaseAsync(x, a, new(lx.Epoch + 1, "nas-1"), token) is null && await LockHolderAsync("nas-1") == x.ToString(), "A stale worker cannot release a lock.");

            // Exactement un gagnant en cas de course.
            var race = await Task.WhenAll(Locks.TryAcquireAsync(x, a, For(lx, "race"), token), Locks.TryAcquireAsync(y, a, For(ly, "race"), token));
            Check(race.Count(item => item!.Acquired) == 1, "Concurrent acquisitions give the disk to exactly one execution.");
            await Locks.ReleaseAsync(x, a, For(lx, "race"), token); await Locks.ReleaseAsync(y, a, For(ly, "race"), token);

            // Reprise d'un verrou expiré (exécution perdue), puis refus pour l'ancien détenteur.
            await ScalarAsync<object>("UPDATE disk_locks SET lease_until=clock_timestamp()-interval '1 second' WHERE disk_key='nas-1' RETURNING 1");
            Check((await Locks.TryAcquireAsync(y, a, For(ly, "nas-1"), token)) is { Acquired: true }, "An expired lock is taken over.");
            Check((await Locks.TryAcquireAsync(x, a, For(lx, "nas-1"), token)) is { Acquired: false }, "The previous holder is now told the disk is busy.");

            // Libération idempotente.
            Check(await Locks.ReleaseAsync(y, a, For(ly, "nas-1"), token) == true && await Locks.ReleaseAsync(y, a, For(ly, "nas-1"), token) == false, "Release is idempotent.");
            Check((await Locks.TryAcquireAsync(x, a, For(lx, "nas-1"), token)) is { Acquired: true }, "A released disk can be taken.");

            // Le renouvellement du bail d'exécution prolonge ses verrous.
            await ScalarAsync<object>("UPDATE disk_locks SET lease_until=clock_timestamp()+interval '1 second' WHERE disk_key='nas-1' RETURNING 1");
            Check(await runtime.RenewAsync(x, a, lx.Epoch, token) is not null, "The execution lease is renewed.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM disk_locks WHERE disk_key='nas-1' AND lease_until>clock_timestamp()+interval '30 seconds'") == 1, "Renewing the execution lease extends its disk locks.");

            // Route worker : succès, occupé, nom invalide.
            using var client = Client(a);
            var viaApi = await client.PostAsync<DiskLockResponse>($"executions/{y:D}/disk-locks/acquire", For(ly, "nas-1"), token);
            Check(viaApi is { Acquired: false } && viaApi.HolderExecutionId == x, "The worker route reports a busy disk.");
            var invalid = false;
            try { await client.PostAsync<DiskLockResponse>($"executions/{y:D}/disk-locks/acquire", For(ly, "a/b"), token); } catch (HttpRequestException exception) { invalid = exception.StatusCode == System.Net.HttpStatusCode.BadRequest; }
            Check(invalid, "The worker route rejects an invalid disk name.");

            // Fin d'exécution : annulation et récupération libèrent les verrous.
            Check(await executions.CancelAsync(x, token), "Cancel the holder.");
            await ExpireAsync(x); await RecoverCancellationAsync(x);
            Check(await LockHolderAsync("nas-1") is null, "Cancelling and recovering an execution frees its disks.");
            Check(await executions.CancelAsync(y, token), "Cancel the second execution.");
            await ExpireAsync(y); await RecoverCancellationAsync(y);
            Check(await ScalarAsync<long>("SELECT count(*) FROM disk_locks WHERE execution_id=@id", ("id", y)) == 0, "No lock survives its execution.");

            // Une attente durable libère les verrous tout en gardant le média réservé.
            var (w, _, _, _) = await WaitingAsync(simple, null, async (id, lease) =>
                Check((await Locks.TryAcquireAsync(id, a, For(lease, "nas-w"), token)) is { Acquired: true }, "Take a disk before waiting."));
            Check(await LockHolderAsync("nas-w") is null, "Entering a durable wait frees the disk locks.");
            Check(await ReservationUntilAsync(w) == "unbounded", "The media stays reserved while the disk is free.");
            Check(await executions.CancelAsync(w, token), "Cancel the waiting execution."); await ExpireAsync(w); await RecoverCancellationAsync(w);
        }

        /// <summary>La perte du worker libère le verrou ; l'exécution est reprise (ADR 0004) sans le garder pour un worker disparu.</summary>
        public async Task DiskLockReleasedAfterWorkerLossAsync()
        {
            var flow = new FlowDefinition(1, Guid.NewGuid(), 1, "Disk fixture", "video",
                [Node(registry, "entry", "base-input-file"), Node(registry, "disk", "base-disk-lock", new() { ["disk"] = J("nas-loss") }), Node(registry, "end", "base-complete-flow")],
                [new("a", "entry", "success", "disk"), new("b", "disk", "acquired", "end")]);
            var (x, first, context) = await EnqueueAsync(flow);
            var (y, second, _) = await EnqueueAsync(flow);
            var token = CancellationToken.None;
            Check((await Locks.TryAcquireAsync(x, a, new(first.Epoch, "nas-loss"), token)) is { Acquired: true }, "The first execution takes the disk.");

            await ExpireAsync(x);
            Check(await runtime.ClaimAsync(b, token) is null, "Another worker cannot silently resume the assigned attempt.");
            var recovery = new RuntimeRecoveryService(db, NullLogger<RuntimeRecoveryService>.Instance);
            await recovery.ProcessOnceAsync(token);
            Check((await executions.GetAsync(x, token))?.State == "running" && await ScalarAsync<long>("SELECT count(*) FROM execution_work WHERE execution_id=@id AND worker_node_id IS NULL AND handoff_pending", ("id", x)) == 1, "The disconnected worker's attempt is handed back for a resume.");
            Check(await LockHolderAsync("nas-loss") is null, "Resuming releases the lost worker's disk lock.");
            Check((await Locks.TryAcquireAsync(y, a, new(second.Epoch, "nas-loss"), token)) is { Acquired: true }, "A later explicit task can acquire the released disk.");
            var secondState = (await executions.GetAsync(y, token))?.State;
            Check(secondState is "running" or "cancelling", "The second execution remains active after the first worker fails.");
            if (secondState == "running")
            {
                Check(await executions.CancelAsync(y, token), "Cancel the second execution."); await ExpireAsync(y); await RecoverCancellationAsync(y);
            }
            Check(await executions.CancelAsync(x, token), "Cancel the resumable execution."); await RecoverCancellationAsync(x);
        }

        private static bool Throws(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } }
    }
}
