using System.Text.Json;

namespace Encoding.WorkerAgent.Runtime.Media;

public sealed record MediaActionContext(
    string Id,
    IReadOnlyDictionary<string, JsonElement> Config,
    string Current,
    string Directory,
    string Node,
    Func<string, Task<string>> Extra,
    Guid WorkerId,
    JsonElement? KnownMetadata = null);
