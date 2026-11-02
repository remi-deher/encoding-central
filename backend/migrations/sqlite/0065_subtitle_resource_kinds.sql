-- SQLite ne sait pas élargir une contrainte CHECK. Les deux tables sont donc
-- reconstruites ensemble pour conserver la clé étrangère et les captures de réplication.
DROP TRIGGER capture_flow_resource_versions_delete;
DROP TRIGGER capture_flow_resource_versions_update;
DROP TRIGGER capture_flow_resource_versions_insert;
DROP TRIGGER capture_flow_resources_delete;
DROP TRIGGER capture_flow_resources_update;
DROP TRIGGER capture_flow_resources_insert;

CREATE TABLE flow_resources_replacement (
    id TEXT PRIMARY KEY,
    kind TEXT NOT NULL,
    name TEXT NOT NULL,
    description TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    updated_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    CHECK (kind IN ('font', 'subtitle-profile', 'subtitle')),
    CHECK (((length(name) >= 1) AND (length(name) <= 128))),
    CHECK ((length(description) <= 1000))
);

CREATE TABLE flow_resource_versions_replacement (
    resource_id TEXT NOT NULL,
    version INTEGER NOT NULL,
    files TEXT NOT NULL,
    total_bytes INTEGER NOT NULL,
    created_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    PRIMARY KEY (resource_id, version),
    FOREIGN KEY (resource_id) REFERENCES flow_resources_replacement(id) ON DELETE RESTRICT,
    CHECK ((version > 0)),
    CHECK ((total_bytes >= 0))
);

INSERT INTO flow_resources_replacement(id, kind, name, description, created_at, updated_at)
SELECT id, kind, name, description, created_at, updated_at
FROM flow_resources;

INSERT INTO flow_resource_versions_replacement(resource_id, version, files, total_bytes, created_at)
SELECT resource_id, version, files, total_bytes, created_at
FROM flow_resource_versions;

DROP TABLE flow_resource_versions;
DROP TABLE flow_resources;
ALTER TABLE flow_resources_replacement RENAME TO flow_resources;
ALTER TABLE flow_resource_versions_replacement RENAME TO flow_resource_versions;

CREATE UNIQUE INDEX ux_flow_resources_name ON flow_resources (lower(name));

CREATE TRIGGER capture_flow_resources_insert AFTER INSERT ON flow_resources
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flow_resources';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_resources',json_object('id',NEW.id),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_resources'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_flow_resources_update AFTER UPDATE OF id,kind,name,description,created_at,updated_at ON flow_resources
WHEN OLD.id IS NOT NEW.id OR OLD.kind IS NOT NEW.kind OR OLD.name IS NOT NEW.name OR OLD.description IS NOT NEW.description OR OLD.created_at IS NOT NEW.created_at OR OLD.updated_at IS NOT NEW.updated_at
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flow_resources';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_resources',json_object('id',NEW.id),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_resources'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_resources',json_object('id',OLD.id),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_resources'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_flow_resources_delete AFTER DELETE ON flow_resources
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flow_resources';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_resources',json_object('id',OLD.id),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_resources'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_flow_resource_versions_insert AFTER INSERT ON flow_resource_versions
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flow_resource_versions';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_resource_versions',json_object('resource_id',NEW.resource_id,'version',NEW.version),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_resource_versions'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_flow_resource_versions_update AFTER UPDATE OF resource_id,version,files,total_bytes,created_at ON flow_resource_versions
WHEN OLD.resource_id IS NOT NEW.resource_id OR OLD.version IS NOT NEW.version OR OLD.files IS NOT NEW.files OR OLD.total_bytes IS NOT NEW.total_bytes OR OLD.created_at IS NOT NEW.created_at
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flow_resource_versions';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_resource_versions',json_object('resource_id',NEW.resource_id,'version',NEW.version),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_resource_versions'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_resource_versions',json_object('resource_id',OLD.resource_id,'version',OLD.version),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_resource_versions'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_flow_resource_versions_delete AFTER DELETE ON flow_resource_versions
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flow_resource_versions';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_resource_versions',json_object('resource_id',OLD.resource_id,'version',OLD.version),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_resource_versions'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;
