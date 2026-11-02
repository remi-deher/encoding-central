-- Local only: leadership transfers and periodic database backups tracking.
ALTER TABLE cluster_identity ADD COLUMN database_backup_interval_minutes INTEGER DEFAULT 60;
ALTER TABLE cluster_identity ADD COLUMN master_url TEXT;

CREATE TABLE cluster_leadership_transfers (
    id TEXT PRIMARY KEY,
    target_node_id TEXT NOT NULL REFERENCES cluster_nodes(id),
    state TEXT NOT NULL DEFAULT 'pending',
    snapshot_path TEXT,
    snapshot_sha256 TEXT,
    snapshot_size_bytes INTEGER,
    created_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    expires_at TEXT NOT NULL,
    completed_at TEXT
);

CREATE TABLE cluster_database_backups (
    id TEXT PRIMARY KEY,
    digest TEXT NOT NULL,
    size_bytes INTEGER NOT NULL,
    file_path TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z'))
);
CREATE INDEX ix_cluster_database_backups_created_at ON cluster_database_backups(created_at DESC);
