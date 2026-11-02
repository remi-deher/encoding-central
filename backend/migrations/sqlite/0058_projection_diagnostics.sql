-- Local operational state, never reconciled. The immutable journal remains authoritative.
CREATE TABLE cluster_projection_diagnostics (
    event_id TEXT PRIMARY KEY REFERENCES cluster_change_log(event_id),
    entity_type TEXT NOT NULL,
    cause TEXT NOT NULL,
    first_failed_at TEXT NOT NULL,
    last_failed_at TEXT NOT NULL,
    attempts INTEGER NOT NULL DEFAULT 1
);
