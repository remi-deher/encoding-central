-- Journaux système locaux pour les avertissements et erreurs (architecture 0073).
-- Règle absolue : table strictement locale à l'hôte, non réconciliée par le maillage ou Raft.
CREATE TABLE system_logs (
    id TEXT PRIMARY KEY,
    timestamp TEXT NOT NULL,
    level TEXT NOT NULL,
    category TEXT NOT NULL,
    message TEXT NOT NULL,
    exception TEXT,
    node_id TEXT,
    node_name TEXT,
    event_id INTEGER,
    created_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z'))
);

CREATE INDEX ix_system_logs_timestamp ON system_logs (timestamp DESC);
CREATE INDEX ix_system_logs_level ON system_logs (level, timestamp DESC);
CREATE INDEX ix_system_logs_category ON system_logs (category, timestamp DESC);
