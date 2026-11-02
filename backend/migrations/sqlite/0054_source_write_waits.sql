-- Local coordination only: neither worker leases nor pending source writes are mesh entities.
CREATE TABLE runtime_source_write_waits (
    execution_id TEXT PRIMARY KEY REFERENCES executions(id) ON DELETE RESTRICT,
    flow_node_id TEXT NOT NULL,
    reason TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z'))
);
CREATE TABLE pending_source_writes (
    execution_id TEXT PRIMARY KEY REFERENCES executions(id) ON DELETE RESTRICT,
    source_node_id TEXT NOT NULL,
    library_id TEXT NOT NULL,
    original_path TEXT NOT NULL,
    published_path TEXT NOT NULL,
    source_sha TEXT NOT NULL,
    result_sha TEXT NOT NULL,
    result_extension TEXT NOT NULL,
    grace_hours INTEGER NOT NULL,
    state TEXT NOT NULL DEFAULT 'waiting' CHECK(state IN ('waiting','claimed','completed','failed')),
    claim_id TEXT,
    claim_until TEXT,
    detail TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    finished_at TEXT
);
CREATE INDEX ix_pending_source_writes_node ON pending_source_writes(source_node_id,state,claim_until);
