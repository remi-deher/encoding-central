using System.Text.Json;
using Encoding.Contracts.Workers;
using Encoding.WorkerAgent.Services;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class AgentJsonTests
{
    private const string Json = """
        {"id":"11111111-1111-1111-1111-111111111111","name":"n","roles":["server","worker"],"maxConcurrentTasks":1,
         "capabilities":[],"status":"online","lastSeenAt":"2026-10-15T10:00:00Z"}
        """;

    [Fact]
    public void AgentClientsReadAndWriteRolesAsStrings()
    {
        var node = JsonSerializer.Deserialize<WorkerResponse>(Json, AgentJson.Options)!;
        Assert.Equal([NodeRole.Server, NodeRole.Worker], node.Roles);
        Assert.Contains("\"worker\"", JsonSerializer.Serialize(node, AgentJson.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<WorkerResponse>(Json, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
