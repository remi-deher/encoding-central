CREATE TABLE node_directory_browse_requests (
    id TEXT PRIMARY KEY,
    node_id TEXT NOT NULL,
    path TEXT NOT NULL,
    state TEXT NOT NULL CHECK(state IN ('queued','claimed','completed','failed')),
    entries TEXT NOT NULL DEFAULT '[]',
    has_more INTEGER NOT NULL DEFAULT 0,
    error TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    claim_until TEXT
);

CREATE INDEX ix_node_directory_browse_queue ON node_directory_browse_requests(node_id,state,created_at);
CREATE INDEX ix_node_directory_browse_expiry ON node_directory_browse_requests(expires_at);

INSERT OR IGNORE INTO cluster_sync_capture_tables(entity_type) VALUES('node_directory_browse_requests');

CREATE TRIGGER capture_node_directory_browse_requests_insert AFTER INSERT ON node_directory_browse_requests
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='node_directory_browse_requests';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'node_directory_browse_requests',json_object('id',NEW.id),revision FROM cluster_sync_capture_tables WHERE entity_type='node_directory_browse_requests'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_node_directory_browse_requests_update AFTER UPDATE OF id,node_id,path,state,entries,has_more,error,created_at,updated_at,expires_at,claim_until ON node_directory_browse_requests
WHEN OLD.id IS NOT NEW.id OR OLD.node_id IS NOT NEW.node_id OR OLD.path IS NOT NEW.path OR OLD.state IS NOT NEW.state
  OR OLD.entries IS NOT NEW.entries OR OLD.has_more IS NOT NEW.has_more OR OLD.error IS NOT NEW.error
  OR OLD.created_at IS NOT NEW.created_at OR OLD.updated_at IS NOT NEW.updated_at OR OLD.expires_at IS NOT NEW.expires_at
  OR OLD.claim_until IS NOT NEW.claim_until
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='node_directory_browse_requests';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'node_directory_browse_requests',json_object('id',NEW.id),revision FROM cluster_sync_capture_tables WHERE entity_type='node_directory_browse_requests'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_node_directory_browse_requests_delete AFTER DELETE ON node_directory_browse_requests
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='node_directory_browse_requests';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'node_directory_browse_requests',json_object('id',OLD.id),revision FROM cluster_sync_capture_tables WHERE entity_type='node_directory_browse_requests'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;
