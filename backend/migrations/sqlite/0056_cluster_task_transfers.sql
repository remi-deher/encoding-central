-- Local projections/coordinators. Signed registers in cluster_change_log are the replicated authority.
CREATE TABLE task_coordination_inbox(origin_id TEXT NOT NULL,sequence INTEGER NOT NULL,register_key TEXT NOT NULL,body TEXT NOT NULL,attempts INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(origin_id,sequence));
CREATE TRIGGER task_coordination_capture AFTER INSERT ON cluster_change_log
WHEN NEW.event_type='register.set' AND json_extract(NEW.payload,'$.key') LIKE 'tasks/v2/%'
 AND NEW.cluster_id=(SELECT id FROM cluster_identity WHERE singleton=1)
BEGIN
 INSERT INTO task_coordination_inbox(origin_id,sequence,register_key,body) VALUES(NEW.origin_id,NEW.sequence,json_extract(NEW.payload,'$.key'),json_extract(NEW.payload,'$.value')) ON CONFLICT DO NOTHING;
END;
CREATE TABLE task_transfers(
 id TEXT PRIMARY KEY,execution_id TEXT NOT NULL,source_peer_id TEXT NOT NULL,target_peer_id TEXT NOT NULL,
 epoch INTEGER NOT NULL,previous_event_id TEXT NOT NULL,snapshot TEXT NOT NULL,receipt TEXT,target_source_node_id TEXT,
 state TEXT NOT NULL CHECK(state IN ('proposed','received','activated','aborted')),
 created_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')));
CREATE UNIQUE INDEX ix_task_transfer_pending ON task_transfers(execution_id) WHERE state IN ('proposed','received');
CREATE TABLE task_checkpoints(execution_id TEXT NOT NULL,epoch INTEGER NOT NULL,revision INTEGER NOT NULL,author_peer_id TEXT NOT NULL,body TEXT NOT NULL,PRIMARY KEY(execution_id,epoch));
CREATE TABLE task_neutralizations(peer_id TEXT PRIMARY KEY,event_id TEXT NOT NULL,author_peer_id TEXT NOT NULL,recorded_at TEXT NOT NULL,reason TEXT NOT NULL,released_at TEXT);
CREATE TABLE task_peer_observations(observer_peer_id TEXT NOT NULL,subject_peer_id TEXT NOT NULL,sequence INTEGER NOT NULL,observed_at TEXT NOT NULL,expires_at TEXT NOT NULL,reachable INTEGER NOT NULL,PRIMARY KEY(observer_peer_id,subject_peer_id));
CREATE TABLE task_observation_sequence(singleton INTEGER PRIMARY KEY CHECK(singleton=1),sequence INTEGER NOT NULL);
INSERT INTO task_observation_sequence VALUES(1,0);
CREATE TABLE task_quarantines(execution_id TEXT PRIMARY KEY,winner_id TEXT NOT NULL,reason TEXT NOT NULL,created_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')));
CREATE TABLE task_coordination_rejections(register_key TEXT PRIMARY KEY,reason TEXT NOT NULL);

CREATE TABLE task_checkpoint_dirty(execution_id TEXT PRIMARY KEY,attempts INTEGER NOT NULL DEFAULT 0);
CREATE TABLE task_checkpoint_cached(execution_id TEXT PRIMARY KEY,body TEXT NOT NULL);

INSERT INTO task_checkpoint_dirty(execution_id)
SELECT w.execution_id FROM execution_work w JOIN executions e ON e.id=w.execution_id
JOIN task_ownership o ON o.execution_id=w.execution_id JOIN task_server_identity s ON s.peer_id=o.owner_peer_id
WHERE o.state='active' AND e.state IN ('queued','running');

CREATE TABLE task_transfer_aborts(transfer_id TEXT PRIMARY KEY,event_id TEXT NOT NULL);
