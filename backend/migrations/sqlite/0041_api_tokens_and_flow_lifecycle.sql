-- Jeton API cluster et cycle de vie des flows (suppression et archivage).
CREATE TABLE api_tokens (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    token_hash BLOB NOT NULL UNIQUE,
    token_prefix TEXT NOT NULL,
    role TEXT NOT NULL DEFAULT 'admin',
    is_active BOOLEAN NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    expires_at TEXT,
    last_used_at TEXT,
    revoked_at TEXT,
    CHECK (role IN ('admin', 'viewer'))
);

ALTER TABLE flows ADD COLUMN deleted_at TEXT;
