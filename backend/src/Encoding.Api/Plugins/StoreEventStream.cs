namespace Encoding.Api.Plugins;

public enum StoreEventKind { None, Snapshot, DesiredState }

/// <summary>
/// Règles du flux SSE <c>/api/v1/store/events</c> (issue #17, P2). L'identifiant d'événement est opaque pour le worker :
/// <c>v1.{révision}</c>. Sans journal des révisions intermédiaires, une reprise envoie l'état désiré courant (qui contient tous les
/// changements manqués) ; un curseur absent, illisible ou postérieur à l'état du central (base restaurée) redonne un snapshot complet.
/// </summary>
public static class StoreEventStream
{
    public const string SnapshotEvent = "snapshot";
    public const string DesiredStateEvent = "desired-state";
    private const string Prefix = "v1.";

    public static string EventId(long revision) => Prefix + revision.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static bool TryParseEventId(string? id, out long revision)
    {
        revision = 0;
        return id is not null && id.StartsWith(Prefix, StringComparison.Ordinal)
            && long.TryParse(id.AsSpan(Prefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out revision);
    }

    /// <summary>Premier événement d'une connexion selon le <c>Last-Event-ID</c> reçu.</summary>
    public static StoreEventKind Initial(string? lastEventId, long currentRevision)
    {
        if (!TryParseEventId(lastEventId, out var seen) || seen > currentRevision) return StoreEventKind.Snapshot;
        return seen < currentRevision ? StoreEventKind.DesiredState : StoreEventKind.None;
    }

    public static string Frame(StoreEventKind kind, StoreDesiredState state)
    {
        var name = kind == StoreEventKind.Snapshot ? SnapshotEvent : DesiredStateEvent;
        var data = System.Text.Json.JsonSerializer.Serialize(
            new { revision = state.Revision, items = state.Items }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        return $"id: {EventId(state.Revision)}\nevent: {name}\ndata: {data}\n\n";
    }

    /// <summary>Battement : commentaire SSE, sans identifiant, donc sans effet sur la révision ni sur <c>Last-Event-ID</c>.</summary>
    public const string Heartbeat = ": ping\n\n";
}
