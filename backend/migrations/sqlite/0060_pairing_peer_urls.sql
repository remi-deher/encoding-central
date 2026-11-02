-- Reconciled discovery metadata. URLs become candidates only after the matching peer admission is approved.
CREATE TABLE node_peer_reconciliation_urls(node_id TEXT PRIMARY KEY,peer_id TEXT NOT NULL,url TEXT NOT NULL,approved INTEGER NOT NULL DEFAULT 0 CHECK(approved IN (0,1)));
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('node_peer_reconciliation_urls');
CREATE TRIGGER capture_node_peer_reconciliation_urls_insert AFTER INSERT ON node_peer_reconciliation_urls
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='node_peer_reconciliation_urls';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'node_peer_reconciliation_urls',json_object('node_id',NEW.node_id),revision FROM cluster_sync_capture_tables WHERE entity_type='node_peer_reconciliation_urls'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision;
END;
CREATE TRIGGER capture_node_peer_reconciliation_urls_update AFTER UPDATE OF node_id,peer_id,url,approved ON node_peer_reconciliation_urls
WHEN OLD.node_id IS NOT NEW.node_id OR OLD.peer_id IS NOT NEW.peer_id OR OLD.url IS NOT NEW.url OR OLD.approved IS NOT NEW.approved
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='node_peer_reconciliation_urls';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'node_peer_reconciliation_urls',json_object('node_id',NEW.node_id),revision FROM cluster_sync_capture_tables WHERE entity_type='node_peer_reconciliation_urls'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'node_peer_reconciliation_urls',json_object('node_id',OLD.node_id),revision FROM cluster_sync_capture_tables WHERE entity_type='node_peer_reconciliation_urls'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision;
END;
CREATE TRIGGER capture_node_peer_reconciliation_urls_delete AFTER DELETE ON node_peer_reconciliation_urls
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='node_peer_reconciliation_urls';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'node_peer_reconciliation_urls',json_object('node_id',OLD.node_id),revision FROM cluster_sync_capture_tables WHERE entity_type='node_peer_reconciliation_urls'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision;
END;
