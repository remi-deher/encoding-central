using Encoding.Api.Infrastructure.Data;

namespace Encoding.Runtime.Tests;

internal static partial class Harness
{
    internal sealed partial class Fixture
    {
        public async Task ClaimPastIncompatiblePagesAsync(int blocked, bool missingAccess, bool concurrent)
        {
            var library = Guid.NewGuid();
            var blockedLibrary = missingAccess ? Guid.NewGuid() : library;
            await using (var seed = db.CreateCommand("""
                INSERT INTO libraries(id,name) VALUES(@library,@name);
                INSERT INTO library_node_accesses(library_id,node_id,mode,local_path,can_read,can_write)
                    VALUES(@library,@worker,'direct',@root,true,true);
                INSERT INTO library_processing_settings(library_id,max_concurrent_tasks) VALUES(@library,1);
                UPDATE cluster_nodes SET max_concurrent_tasks=1 WHERE id=@worker;
                """))
            {
                seed.Parameters.AddWithValue("library", library); seed.Parameters.AddWithValue("name", library.ToString());
                seed.Parameters.AddWithValue("worker", a); seed.Parameters.AddWithValue("root", directory);
                await seed.ExecuteNonQueryAsync();
            }
            await using (var access = db.CreateCommand("""
                INSERT INTO libraries(id,name) VALUES(@library,@name) ON CONFLICT DO NOTHING;
                INSERT INTO library_node_accesses(library_id,node_id,mode,local_path,can_read,can_write)
                    VALUES(@library,@worker,'direct',@root,true,true);
                """))
            {
                access.Parameters.AddWithValue("library", blockedLibrary); access.Parameters.AddWithValue("name", blockedLibrary.ToString());
                access.Parameters.AddWithValue("worker", b); access.Parameters.AddWithValue("root", directory);
                await access.ExecuteNonQueryAsync();
            }
            var compatible = Chain(Node(registry, "entry", "base-input-file"), Node(registry, "end", "base-complete-flow"));
            var incompatible = missingAccess ? compatible : Chain(Node(registry, "entry", "base-input-file"),
                Node(registry, "target", "nodes-run-on-node", new() { ["targetNodeId"] = J(b) }), Node(registry, "end", "base-complete-flow"));
            Guid Id(int i) => Guid.Parse("00000000-0000-0000-0000-" + i.ToString("D12", System.Globalization.CultureInfo.InvariantCulture));
            for (var i = 1; i <= blocked + 2; i++)
            {
                var flow = i <= blocked ? incompatible : compatible;
                Check(await runtime.EnqueueAsync(new(Id(i), i <= blocked ? blockedLibrary : library,
                    $"candidate-{i}.mkv", flow.Id, flow.Version), flow, CancellationToken.None), "Queue candidate.");
            }
            // Equal timestamps exercise the ID tie-breaker across every page boundary.
            await using (var order = db.CreateCommand("UPDATE executions SET queued_at='2000-01-01 00:00:00.000000Z'")) await order.ExecuteNonQueryAsync();
            var claims = concurrent
                ? await Task.WhenAll(Task.Run(() => runtime.ClaimAsync(a, CancellationToken.None)), Task.Run(() => runtime.ClaimAsync(a, CancellationToken.None)))
                : [await runtime.ClaimAsync(a, CancellationToken.None)];
            Check(claims.Count(lease => lease is not null) == 1, "Only one claim fits the worker and library limits.");
            var lease = claims.Single(item => item is not null)!;
            Check(lease.ExecutionId == Id(blocked + 1), "The oldest compatible candidate beyond the first pages is assigned.");
            Check(await runtime.ClaimAsync(a, CancellationToken.None) is null, "Paging never bypasses the capacity limit.");
            Check(await runtime.RenewAsync(lease.ExecutionId, a, lease.Epoch, CancellationToken.None) is not null, "The assigned lease remains valid.");
            await using var verify = db.CreateCommand("SELECT count(*) FROM executions WHERE state='queued'");
            Check((long)(await verify.ExecuteScalarAsync())! == blocked + 1, "Skipped candidates are neither failed nor modified.");
        }
    }
}
