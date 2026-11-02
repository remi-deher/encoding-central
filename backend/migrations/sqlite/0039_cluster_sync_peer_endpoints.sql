CREATE TABLE cluster_sync_peer_endpoints (
    peer_url TEXT PRIMARY KEY,
    source TEXT NOT NULL DEFAULT 'auto',
    added_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    last_seen_at TEXT
);
