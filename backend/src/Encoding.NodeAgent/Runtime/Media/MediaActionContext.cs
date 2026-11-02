using System.Text.Json;

namespace Encoding.NodeAgent.Runtime.Media;

public sealed record MediaActionContext(
    string Id,
    IReadOnlyDictionary<string, JsonElement> Config,
    string Current,
    string Directory,
    string Node,
    Func<string, Task<string>> Extra,
    Guid WorkerId,
    JsonElement? KnownMetadata = null);
