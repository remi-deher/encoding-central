-- Local projections of signed immutable registers; never replicate these tables as LWW entities.
CREATE TABLE task_server_identity(singleton INTEGER PRIMARY KEY CHECK(singleton=1),peer_id TEXT NOT NULL,ready INTEGER NOT NULL DEFAULT 0 CHECK(ready IN (0,1)));
CREATE TABLE task_ownership(
    execution_id TEXT PRIMARY KEY,
    cluster_id TEXT NOT NULL,
    owner_peer_id TEXT NOT NULL,
    epoch INTEGER NOT NULL CHECK(epoch>0),
    last_event_id TEXT NOT NULL,
    library_id TEXT NOT NULL,
    media_key TEXT NOT NULL,
    queued_at TEXT NOT NULL,
    state TEXT NOT NULL CHECK(state IN ('active','frozen','received','transferred','quarantined'))
);
CREATE INDEX ix_task_ownership_media ON task_ownership(cluster_id,library_id,media_key,queued_at,owner_peer_id);
CREATE TABLE task_ownership_events(event_id TEXT PRIMARY KEY,register_key TEXT NOT NULL,body TEXT NOT NULL,state TEXT NOT NULL CHECK(state IN ('pending','applied','rejected')),detail TEXT NOT NULL);
CREATE INDEX ix_task_ownership_events_key ON task_ownership_events(register_key);
CREATE TABLE task_ownership_inbox(origin_id TEXT NOT NULL,sequence INTEGER NOT NULL,register_key TEXT NOT NULL,body TEXT NOT NULL,PRIMARY KEY(origin_id,sequence));
-- Queue every immutable journal value, including versions losing the register's LWW comparison.
CREATE TRIGGER task_ownership_journal_insert AFTER INSERT ON cluster_change_log
WHEN NEW.event_type='register.set' AND json_extract(NEW.payload,'$.key') LIKE 'tasks/v1/events/%'
  AND NEW.cluster_id=(SELECT id FROM cluster_identity WHERE singleton=1)
BEGIN
    INSERT INTO task_ownership_inbox(origin_id,sequence,register_key,body)
    VALUES(NEW.origin_id,NEW.sequence,json_extract(NEW.payload,'$.key'),json_extract(NEW.payload,'$.value')) ON CONFLICT DO NOTHING;
END;
