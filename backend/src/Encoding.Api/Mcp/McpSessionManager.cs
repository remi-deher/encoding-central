using System.Text.Json;
using System.Threading.Channels;

namespace Encoding.Api.Mcp;

/// <summary>Gère les sessions SSE actives, leur propriétaire et les identifiants de requêtes déjà acceptés.</summary>
public sealed class McpSessionManager
{
    private const int MaximumSessions = 256;
    private const int MaximumQueuedResponses = 64;
    private const int MaximumRequestIds = 512;
    private const int MaximumRequestIdLength = 256;
    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    public bool TryCreateSession(string sessionId, string ownerId, out ChannelReader<McpJsonRpcResponse> reader)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(ownerId) || _sessions.Count >= MaximumSessions || _sessions.ContainsKey(sessionId))
            {
                reader = null!;
                return false;
            }
            var channel = Channel.CreateBounded<McpJsonRpcResponse>(new BoundedChannelOptions(MaximumQueuedResponses)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
            _sessions.Add(sessionId, new(ownerId, channel));
            reader = channel.Reader;
            return true;
        }
    }

    /// <returns>true if this owner may process the request, false if the session is missing or belongs to another user.</returns>
    public bool TryReserveRequest(string sessionId, string ownerId, JsonElement? requestId, out bool duplicate, out bool limitReached)
    {
        duplicate = false;
        limitReached = false;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var session) || session.OwnerId != ownerId)
                return false;
            if (requestId is null) return true;
            if (requestId.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
            {
                limitReached = true;
                return true;
            }
            var key = ((int)requestId.Value.ValueKind).ToString() + ":" + requestId.Value.GetRawText();
            if (key.Length > MaximumRequestIdLength)
            {
                limitReached = true;
                return true;
            }
            if (session.RequestIds.Contains(key))
            {
                duplicate = true;
                return true;
            }
            if (session.RequestIds.Count >= MaximumRequestIds)
            {
                limitReached = true;
                return true;
            }
            session.RequestIds.Add(key);
            return true;
        }
    }

    public async Task<bool> SendMessageAsync(string sessionId, string ownerId, McpJsonRpcResponse message, CancellationToken ct = default)
    {
        Session session;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var existing) || existing.OwnerId != ownerId) return false;
            session = existing;
        }
        try
        {
            await session.Channel.Writer.WriteAsync(message, ct);
            return true;
        }
        catch (ChannelClosedException) { return false; }
    }

    public void CloseSession(string sessionId)
    {
        Session? session = null;
        lock (_gate)
        {
            if (_sessions.Remove(sessionId, out var removed)) session = removed;
        }
        if (session is null) return;
        session.Channel.Writer.TryComplete();
    }

    private sealed class Session(string ownerId, Channel<McpJsonRpcResponse> channel)
    {
        public string OwnerId { get; } = ownerId;
        public Channel<McpJsonRpcResponse> Channel { get; } = channel;
        public HashSet<string> RequestIds { get; } = new(StringComparer.Ordinal);
    }
}
