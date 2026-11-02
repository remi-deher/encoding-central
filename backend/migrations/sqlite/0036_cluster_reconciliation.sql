CREATE TABLE cluster_sync_identity (
    singleton integer PRIMARY KEY DEFAULT 1 CHECK (singleton = 1),
    origin_id text NOT NULL,
    next_sequence integer NOT NULL DEFAULT 0 CHECK (next_sequence >= 0)
);

CREATE TABLE cluster_change_log (
    event_id text PRIMARY KEY,
    cluster_id text NOT NULL,
    origin_id text NOT NULL,
    sequence integer NOT NULL CHECK (sequence > 0),
    event_type text NOT NULL,
    payload text NOT NULL CHECK (json_valid(payload)),
    occurred_at text NOT NULL,
    UNIQUE (origin_id, sequence)
);
CREATE INDEX ix_cluster_change_log_origin_sequence ON cluster_change_log (origin_id, sequence);

CREATE TABLE cluster_sync_registers (
    register_key text PRIMARY KEY,
    value text NOT NULL CHECK (json_valid(value)),
    occurred_at text NOT NULL,
    origin_id text NOT NULL,
    sequence integer NOT NULL
);

CREATE TABLE cluster_sync_peer_vectors (
    peer_url text NOT NULL,
    origin_id text NOT NULL,
    sequence integer NOT NULL CHECK (sequence >= 0),
    updated_at text NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    PRIMARY KEY (peer_url, origin_id)
);

CREATE TABLE cluster_sync_entities (
    entity_type TEXT NOT NULL,
    entity_key TEXT NOT NULL,
    fingerprint TEXT NOT NULL,
    occurred_at TEXT NOT NULL,
    origin_id TEXT NOT NULL,
    sequence INTEGER NOT NULL CHECK (sequence > 0),
    is_deleted BOOLEAN NOT NULL DEFAULT 0,
    PRIMARY KEY (entity_type, entity_key)
);

CREATE TABLE cluster_sync_pending_projections (
    event_id TEXT PRIMARY KEY REFERENCES cluster_change_log(event_id) ON DELETE CASCADE
);

CREATE TABLE cluster_sync_table_cursors (
    entity_type TEXT PRIMARY KEY,
    cursor_value TEXT NOT NULL
);

CREATE INDEX ix_library_scans_latest_completed ON library_scans(library_id, completed_at DESC, id DESC) WHERE state = 'completed';
CREATE INDEX ix_library_media_scan ON library_media(library_id, scan_id, relative_path);
