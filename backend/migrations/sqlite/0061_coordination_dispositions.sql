-- Local only: scheduling and inert evidence. Signed registers remain the replicated authority.
ALTER TABLE task_coordination_inbox ADD COLUMN next_attempt_at TEXT;
ALTER TABLE task_coordination_inbox ADD COLUMN reason TEXT;
ALTER TABLE task_coordination_rejections ADD COLUMN disposition TEXT NOT NULL DEFAULT 'rejected';
CREATE TABLE task_coordination_parked(
 origin_id TEXT NOT NULL,sequence INTEGER NOT NULL,register_key TEXT NOT NULL,body TEXT NOT NULL,
 execution_id TEXT NOT NULL,reason TEXT NOT NULL,
 PRIMARY KEY(origin_id,sequence));
CREATE INDEX ix_task_coordination_due ON task_coordination_inbox(next_attempt_at,attempts);
