using Encoding.Api.Executions;
using Encoding.Api.Flows;
using Encoding.Api.Infrastructure;
using Encoding.Api.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class IdleBackgroundWriteTests
{
    [Theory]
    [InlineData("recovery")]
    [InlineData("waits")]
    [InlineData("published-backups")]
    [InlineData("draft-backups")]
    public async Task EmptyPollCompletesWhileAnotherConnectionOwnsTheWriter(string poll)
    {
        await using var database = await TestDatabase.CreateSqliteAsync("idle-poll");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);
        await using var holder = await database.Db.OpenConnectionAsync();
        await using var held = await holder.BeginTransactionAsync();
        var operation = Task.Run(async () =>
        {
            if (poll == "recovery")
            {
                using var service = new RuntimeRecoveryService(database.Db, NullLogger<RuntimeRecoveryService>.Instance);
                return await service.ProcessOnceAsync(CancellationToken.None);
            }
            if (poll == "waits")
                return await new ExecutionWaitRepository(database.Db, new NodePresenceOptions(TimeSpan.FromSeconds(30)))
                    .ProcessDueAsync(CancellationToken.None);
            using var backups = new FlowBackupService(database.Db, new ConfigurationBuilder().Build(), NullLogger<FlowBackupService>.Instance);
            return await backups.ProcessOneAsync(poll == "draft-backups", CancellationToken.None) ? 1 : 0;
        });
        try { Assert.Equal(0, await operation.WaitAsync(TimeSpan.FromSeconds(5))); }
        finally
        {
            await held.RollbackAsync();
            await operation;
        }
    }
}
