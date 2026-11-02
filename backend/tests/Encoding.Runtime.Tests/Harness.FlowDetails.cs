using Encoding.Api.Flows;
using Encoding.Api.Infrastructure.Data;
using Encoding.Contracts.Flows;
using System.Net.Http.Json;

namespace Encoding.Runtime.Tests;

internal static partial class Harness
{
    internal sealed partial class Fixture
    {
        public async Task FlowDetailsHistoryAsync()
        {
            var repository = new FlowRepository(db);
            var flow = Chain(Node(registry, "input", "base-input-file"), Node(registry, "end", "base-complete-flow"));
            Check(await repository.SaveAsync(new(0, flow), CancellationToken.None) is not null, "Publish the history fixture.");
            var first = await repository.SaveDraftAsync(new(0, flow with { Version = 2, Name = "Draft one" }), CancellationToken.None);
            var second = await repository.SaveDraftAsync(new(first!.DraftRevision, flow with { Version = 2, Name = "Draft two" }), CancellationToken.None);
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var backups = await client.GetFromJsonAsync<FlowDraftBackupSummary[]>($"api/v1/flows/{flow.Id}/draft-backups");
            Check(backups?.Select(item => item.Revision).SequenceEqual([second!.DraftRevision, first.DraftRevision]) == true,
                "List immutable draft snapshots newest first.");
            var source = await client.GetFromJsonAsync<FlowDefinition>($"api/v1/flows/{flow.Id}/draft-backups/{first.DraftRevision}");
            Check(source?.Name == "Draft one", "Preview the exact historical snapshot.");
            using var missing = await client.GetAsync($"api/v1/flows/{flow.Id}/draft-backups/99999");
            Check(missing.StatusCode == System.Net.HttpStatusCode.NotFound, "An absent snapshot returns 404.");
            var library = Guid.NewGuid();
            await using (var assign = db.CreateCommand("INSERT INTO libraries(id,name) VALUES(@library,'Flow detail fixture'); INSERT INTO library_flow_assignments(library_id,flow_id,flow_version) VALUES(@library,@flow,1)"))
            {
                assign.Parameters.AddWithValue("library", library); assign.Parameters.AddWithValue("flow", flow.Id);
                await assign.ExecuteNonQueryAsync();
            }
            var usages = await client.GetFromJsonAsync<FlowLibraryUsage[]>($"api/v1/flows/{flow.Id}/usage");
            Check(usages is [{ LibraryId: var libraryId, Version: 1 }] && libraryId == library, "Show the precise version assigned to the library.");
            var restored = await repository.SaveDraftAsync(new(second!.DraftRevision, source!), CancellationToken.None);
            Check(restored?.Definition.Name == "Draft one" && restored.PublishedVersion == 1, "Restoration creates a draft without publishing.");
            Check(await repository.SaveDraftAsync(new(second.DraftRevision, source!), CancellationToken.None) is null, "A concurrent draft change prevents restoration.");
            Check((await repository.UsageAsync(flow.Id, CancellationToken.None)).Single().Version == 1, "Restoration preserves the library assignment.");
            Check((await repository.VersionsAsync(flow.Id, CancellationToken.None)).Count == 1, "Restoration preserves published versions.");
            Check((await repository.DraftBackupAsync(flow.Id, first.DraftRevision, CancellationToken.None))!.Name == "Draft one", "Restoration preserves the original snapshot.");
            var (refused, _) = await repository.DeleteFlowAsync(flow.Id, CancellationToken.None);
            Check(refused == FlowDeleteResult.Conflict, "Deleting an assigned flow is refused without explicit detachment.");
            using var detached = await client.DeleteAsync($"api/v1/flows/{flow.Id}?detachLibraries=true");
            Check(detached.StatusCode == System.Net.HttpStatusCode.NoContent, "Deleting an assigned flow succeeds with detachment.");
            Check((await repository.UsageAsync(flow.Id, CancellationToken.None)).Count == 0, "Deleting a flow unassigns its libraries.");
            await using (var revision = db.CreateCommand("SELECT flow_assignment_revision FROM libraries WHERE id=@library"))
            {
                revision.Parameters.AddWithValue("library", library);
                Check(Convert.ToInt32(await revision.ExecuteScalarAsync()) == 1, "Detaching a library advances its assignment revision.");
            }
        }
    }
}
