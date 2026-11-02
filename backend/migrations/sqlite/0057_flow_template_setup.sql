ALTER TABLE flow_templates ADD COLUMN configuration TEXT NOT NULL DEFAULT '{"nodes":[]}';
ALTER TABLE flow_templates ADD COLUMN revision INTEGER NOT NULL DEFAULT 1;

DROP TRIGGER capture_flow_templates_update;
CREATE TRIGGER capture_flow_templates_update AFTER UPDATE OF id,name,description,source_flow_id,source_version,source_flow_name,media_type,definition,created_at,configuration,revision ON flow_templates
WHEN OLD.id IS NOT NEW.id OR OLD.name IS NOT NEW.name OR OLD.description IS NOT NEW.description OR OLD.source_flow_id IS NOT NEW.source_flow_id OR OLD.source_version IS NOT NEW.source_version OR OLD.source_flow_name IS NOT NEW.source_flow_name OR OLD.media_type IS NOT NEW.media_type OR OLD.definition IS NOT NEW.definition OR OLD.created_at IS NOT NEW.created_at OR OLD.configuration IS NOT NEW.configuration OR OLD.revision IS NOT NEW.revision
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flow_templates';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_templates',json_object('id',NEW.id),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_templates'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_templates',json_object('id',OLD.id),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_templates'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;
