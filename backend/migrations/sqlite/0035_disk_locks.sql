-- Architecture 0058 : exclusion mutuelle des opérations lourdes sur un même disque, avec bail renouvelé comme celui de l'exécution.
CREATE TABLE disk_locks (
    disk_key TEXT PRIMARY KEY CHECK (length(disk_key) BETWEEN 1 AND 64),
    execution_id TEXT NOT NULL,
    lease_epoch INTEGER NOT NULL,
    lease_until TEXT NOT NULL,
    acquired_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    FOREIGN KEY (execution_id) REFERENCES executions(id)
);
CREATE INDEX ix_disk_locks_execution ON disk_locks (execution_id);
