using System.Text.Json;
using System.Text.Json.Serialization;

namespace Encoding.NodeAgent.Services;

/// <summary>
/// Options JSON des échanges de l'agent avec l'API : celles du web (camelCase) et les énumérations en chaînes,
/// comme les écrit et les lit l'API (« roles »: ["server", "worker"]). Sans le convertisseur, la lecture d'un NodeResponse échoue.
/// </summary>
public static class AgentJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
