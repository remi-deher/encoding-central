namespace Encoding.Api.Infrastructure;

public sealed record ClusterReconciliationPeerHealth(
    string Url,
    bool Configured,
    bool Discovered,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessAt,
    string? LastError, Guid? AuthenticatedPeerId = null, bool CatchUpConfirmed = false);

public sealed record ClusterReconciliationHealthStatus(
    bool Enabled,
    string? DisabledReason,
    bool DiscoveryAvailable,
    string? DiscoveryError,
    IReadOnlyList<ClusterReconciliationPeerHealth> Peers);

/// <summary>Publie l'état opérationnel du maillage sans exposer les clés privées du cluster.</summary>
public sealed class ClusterReconciliationHealth(TimeProvider? timeProvider=null)
{
    private readonly TimeProvider clock=timeProvider??TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<string, MutablePeerHealth> peers = new(StringComparer.OrdinalIgnoreCase);
    private bool enabled;
    private string? disabledReason;
    private bool discoveryAvailable;
    private string? discoveryError;

    private readonly Dictionary<Guid,(string Url,long At)> caughtUp = new();
    private readonly Dictionary<Guid,(string Url,long At)> authenticated=new();
    private readonly Dictionary<Guid,long> sourceWriteSupport=new();
    internal void ConfirmSourceWriteSupport(Guid peer,bool supported)
    {
        lock(gate) {if(supported) sourceWriteSupport[peer]=clock.GetTimestamp();else sourceWriteSupport.Remove(peer);}
    }
    internal bool HasFreshSourceWriteSupport(Guid peer,TimeSpan maximumAge)
    {
        lock(gate) return enabled && sourceWriteSupport.TryGetValue(peer,out var at) && clock.GetElapsedTime(at)<=maximumAge;
    }
    internal void ConfirmCatchUp(string url,Guid peer,bool complete)
    {
        lock(gate)
        {
            foreach(var other in authenticated.Where(item=>item.Key!=peer && string.Equals(item.Value.Url,url,StringComparison.OrdinalIgnoreCase)).Select(item=>item.Key).ToArray())
            {caughtUp.Remove(other);authenticated.Remove(other);sourceWriteSupport.Remove(other);}
            authenticated[peer]=(url,clock.GetTimestamp());
            if(complete) caughtUp[peer]=(url,clock.GetTimestamp());
            else caughtUp.Remove(peer);
        }
    }
    internal string? AuthenticatedEndpoint(Guid peer)
    {
        lock(gate) return authenticated.TryGetValue(peer,out var state)?state.Url:null;
    }
    internal bool HasFreshAuthenticated(Guid peer,TimeSpan maximumAge)
    {lock(gate) return enabled && authenticated.TryGetValue(peer,out var state) && clock.GetElapsedTime(state.At)<=maximumAge;}
    internal bool HasFreshCatchUp(Guid peer,TimeSpan maximumAge)
    {
        lock(gate) return enabled && caughtUp.TryGetValue(peer,out var state)
            && clock.GetElapsedTime(state.At)<=maximumAge;
    }

    public void Disable(string reason)
    {
        lock (gate)
        {
            enabled = false;
            caughtUp.Clear();
            authenticated.Clear();
            sourceWriteSupport.Clear();
            disabledReason = reason;
        }
    }

    public void Enable()
    {
        lock (gate)
        {
            enabled = true;
            disabledReason = null;
        }
    }

    public void SetDiscovery(bool available, string? error = null)
    {
        lock (gate)
        {
            discoveryAvailable = available;
            discoveryError = error;
        }
    }

    public void SetPeers(IEnumerable<string> configured, IEnumerable<string> discovered)
    {
        lock (gate)
        {
            foreach (var peer in peers.Values)
            {
                peer.Configured = false;
                peer.Discovered = false;
            }

            foreach (var url in configured)
                GetOrAdd(url).Configured = true;
            foreach (var url in discovered)
                GetOrAdd(url).Discovered = true;

            foreach (var stale in peers.Where(entry => !entry.Value.Configured && !entry.Value.Discovered
                         && entry.Value.LastAttemptAt is null).Select(entry => entry.Key).ToArray())
                peers.Remove(stale);
        }
    }

    public void ExcludeEndpoint(string url) { lock (gate) peers.Remove(url); }

    public void BeginAttempt(string url)
    {
        lock (gate) GetOrAdd(url).LastAttemptAt = DateTimeOffset.UtcNow;
    }

    public void Succeed(string url)
    {
        lock (gate)
        {
            var peer = GetOrAdd(url);
            peer.LastAttemptAt = DateTimeOffset.UtcNow;
            peer.LastSuccessAt = peer.LastAttemptAt;
            peer.LastError = null;
        }
    }

    public void Fail(string url, string error)
    {
        lock (gate)
        {
            var peer = GetOrAdd(url);
            peer.LastAttemptAt = DateTimeOffset.UtcNow;
            peer.LastError = error;
            foreach(var id in authenticated.Where(item=>string.Equals(item.Value.Url,url,
                StringComparison.OrdinalIgnoreCase)).Select(item=>item.Key).ToArray()) {caughtUp.Remove(id);authenticated.Remove(id);
                sourceWriteSupport.Remove(id);}
        }
    }

    public ClusterReconciliationHealthStatus GetStatus()
    {
        lock (gate)
            return new(enabled, disabledReason, discoveryAvailable, discoveryError,
                peers.OrderBy(peer => peer.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(entry => new ClusterReconciliationPeerHealth(entry.Key, entry.Value.Configured,
                        entry.Value.Discovered, entry.Value.LastAttemptAt, entry.Value.LastSuccessAt, entry.Value.LastError,
                        authenticated.FirstOrDefault(pair => pair.Value.Url.Equals(entry.Key, StringComparison.OrdinalIgnoreCase)).Key is var peer && peer != Guid.Empty ? peer : null,
                        enabled && caughtUp.Any(pair => pair.Value.Url.Equals(entry.Key, StringComparison.OrdinalIgnoreCase) && clock.GetElapsedTime(pair.Value.At) <= TimeSpan.FromSeconds(45))))
                    .ToArray());
    }

    private MutablePeerHealth GetOrAdd(string url)
    {
        if (!peers.TryGetValue(url, out var peer)) peers[url] = peer = new MutablePeerHealth();
        return peer;
    }

    private sealed class MutablePeerHealth
    {
        public bool Configured { get; set; }
        public bool Discovered { get; set; }
        public DateTimeOffset? LastAttemptAt { get; set; }
        public DateTimeOffset? LastSuccessAt { get; set; }
        public string? LastError { get; set; }
    }
}
