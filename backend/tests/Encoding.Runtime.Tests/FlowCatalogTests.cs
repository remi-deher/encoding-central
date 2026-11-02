using Encoding.Api.Flows;
using Encoding.Api.Infrastructure;
using Encoding.Contracts.Flows;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class FlowCatalogTests
{
    [Fact]
    public async Task PublishedFlowsWithoutDraftsHaveAModificationDate()
    {
        await using var database = await TestDatabase.CreateSqliteAsync("flow-catalog");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);
        var repository = new FlowRepository(database.Db);
        var definition = new FlowDefinition(1, Guid.NewGuid(), 1, "Films", "video", [], []);
        var published = await repository.SaveAsync(new(0, definition), CancellationToken.None);
        Assert.NotNull(published);
        var summary = Assert.Single(await repository.ListAsync(CancellationToken.None));
        Assert.Equal(definition.Id, summary.Id);
        Assert.False(summary.IsDraft);
        Assert.NotEqual(default, summary.UpdatedAt);
        var draft = await repository.SaveDraftAsync(new(0, definition with { Name = "Brouillon" }), CancellationToken.None);
        Assert.NotNull(draft);
        summary = Assert.Single(await repository.ListAsync(CancellationToken.None));
        Assert.True(summary.IsDraft);
        Assert.Equal("Brouillon", summary.Name);
        Assert.True(summary.UpdatedAt >= published.CreatedAt);
    }
}
