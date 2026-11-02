using System.Globalization;
using System.Text.Json;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Executions;
using Encoding.NodeAgent.Services;
using Microsoft.Extensions.Logging;

namespace Encoding.NodeAgent.Runtime.Media;

public sealed record MediaActionOutcome(string? Primary,IReadOnlyList<string> Additional,JsonElement? Value=null,
    IReadOnlyList<ResolvedVideoEncoding>? VideoEncodings=null,bool NeedsChanges=false);

public sealed partial class MediaActions
{
    private readonly IProcessToolRunner tools;
    private readonly MediaScanner scanner;
    private readonly IToolCapabilityProbe capabilities;
    private readonly ILogger<MediaActions>? logger;
    private readonly IReadOnlyList<IMediaActionHandler> _handlers;

    public MediaActions(
        IProcessToolRunner tools,
        MediaScanner scanner,
        IToolCapabilityProbe capabilities,
        ILogger<MediaActions>? logger = null,
        IEnumerable<IMediaActionHandler>? handlers = null)
    {
        this.tools = tools;
        this.scanner = scanner;
        this.capabilities = capabilities;
        this.logger = logger;
        var handlerList = handlers?.ToArray();
        _handlers = (handlerList is not null && handlerList.Length > 0)
            ? handlerList
            : [
                new Handlers.MediaInspectionActionHandler(tools, scanner),
                new Handlers.FfmpegEncodingActionHandler(this),
                new Handlers.MediaExtractionActionHandler(tools, scanner),
                new Handlers.ImageActionHandler(tools, scanner),
                new Handlers.MatroskaActionHandler(tools)
            ];
    }

    public static string Text(IReadOnlyDictionary<string,JsonElement> c,string key,string fallback="")=>c.TryGetValue(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()!:fallback;
    public static string Number(IReadOnlyDictionary<string,JsonElement> c,string key,decimal fallback=0)=>c.TryGetValue(key,
        out var v)&&v.ValueKind==JsonValueKind.Number&&v.TryGetDecimal(out var n)?n.ToString(CultureInfo.InvariantCulture):fallback.ToString(CultureInfo.InvariantCulture);
    public static bool Flag(IReadOnlyDictionary<string,JsonElement> c,string key,bool fallback=false)=>c.TryGetValue(key,
        out var v)&&v.ValueKind is JsonValueKind.True or JsonValueKind.False?v.GetBoolean():fallback;
    public static int[] Ids(string value)=>string.IsNullOrWhiteSpace(value)?[]:value.Split(',',
        StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Select(value=>int.TryParse(value,
        out var id)&&id>=0?id:throw new IOException("Identifiant de piste invalide.")).Distinct().ToArray();
    public static string Output(string directory,string node,string extension)=>Path.Combine(directory,
        "out-"+Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(node)))[..16]+extension);

    public async Task<MediaActionOutcome> ExecuteAsync(string id,IReadOnlyDictionary<string,JsonElement> config,string current,string directory,string node,
        Func<string,Task<string>> extra,Guid workerId,CancellationToken ct,JsonElement? knownMetadata=null)
    {
        var context = new MediaActionContext(id, config, current, directory, node, extra, workerId, knownMetadata);
        foreach (var handler in _handlers)
        {
            if (handler.CanHandle(id))
                return await handler.ExecuteAsync(context, ct);
        }
        throw new IOException("Action média inconnue.");
    }
}
