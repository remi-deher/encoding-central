-- Local capture bookkeeping only. Never included in mesh events or Raft projection images.
CREATE TABLE cluster_sync_capture_tables (
    entity_type TEXT PRIMARY KEY,
    revision INTEGER NOT NULL DEFAULT 1 CHECK (revision > 0),
    captured_revision INTEGER NOT NULL DEFAULT 0 CHECK (captured_revision >= 0 AND captured_revision <= revision),
    full_scan_revision INTEGER NOT NULL DEFAULT 1 CHECK (full_scan_revision > 0 AND full_scan_revision <= revision)
);
CREATE TABLE cluster_sync_dirty_entities (
    entity_type TEXT NOT NULL,
    entity_key TEXT NOT NULL CHECK (json_valid(entity_key)),
    revision INTEGER NOT NULL CHECK (revision > 0),
    PRIMARY KEY(entity_type, entity_key),
    FOREIGN KEY(entity_type) REFERENCES cluster_sync_capture_tables(entity_type)
);
CREATE INDEX ix_cluster_sync_dirty_revision ON cluster_sync_dirty_entities(entity_type, revision);

INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('cluster_nodes');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('node_enrollment_requests');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('cluster_peer_admissions');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('node_groups');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('libraries');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('flows');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('flow_scripts');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('encoding_profiles');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('users');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('api_tokens');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('ai_credentials');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('plugin_configurations');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('flow_plugin_configurations');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('enrollment_window');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('first_link_acceptance');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('node_processing_settings');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('node_group_members');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('flow_versions');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('flow_backup_jobs');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('flow_drafts');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('flow_draft_backups');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('flow_script_versions');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('encoding_profile_versions');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('flow_resources');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('flow_resource_versions');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('flow_templates');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('flow_component_packs');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('library_flow_assignments');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('library_node_accesses');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('library_node_processing_limits');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('library_processing_settings');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('library_replacement_policies');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('executions');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('execution_events');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('execution_steps');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('execution_artifacts');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('execution_inputs');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('library_scans');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('media_replacement_marks');
INSERT INTO cluster_sync_capture_tables(entity_type) VALUES('library_processing_receipts');

CREATE TRIGGER capture_cluster_nodes_insert AFTER INSERT ON cluster_nodes
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='cluster_nodes';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'cluster_nodes',json_object('id',NEW.id),revision FROM cluster_sync_capture_tables WHERE entity_type='cluster_nodes'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_cluster_nodes_update AFTER UPDATE OF id,name,roles,max_concurrent_tasks,capabilities,last_seen_at,created_at,deleted_at,display_name,labels,inventory,desired_roles,draining,quarantined,incompatible_reason,protocol_version,node_token_hash ON cluster_nodes
WHEN OLD.id IS NOT NEW.id OR OLD.name IS NOT NEW.name OR OLD.roles IS NOT NEW.roles OR OLD.max_concurrent_tasks IS NOT NEW.max_concurrent_tasks OR OLD.capabilities IS NOT NEW.capabilities OR OLD.last_seen_at IS NOT NEW.last_seen_at OR OLD.created_at IS NOT NEW.created_at OR OLD.deleted_at IS NOT NEW.deleted_at OR OLD.display_name IS NOT NEW.display_name OR OLD.labels IS NOT NEW.labels OR OLD.inventory IS NOT NEW.inventory OR OLD.desired_roles IS NOT NEW.desired_roles OR OLD.draining IS NOT NEW.draining OR OLD.quarantined IS NOT NEW.quarantined OR OLD.incompatible_reason IS NOT NEW.incompatible_reason OR OLD.protocol_version IS NOT NEW.protocol_version OR OLD.node_token_hash IS NOT NEW.node_token_hash
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='cluster_nodes';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'cluster_nodes',json_object('id',NEW.id),revision FROM cluster_sync_capture_tables WHERE entity_type='cluster_nodes'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'cluster_nodes',json_object('id',OLD.id),revision FROM cluster_sync_capture_tables WHERE entity_type='cluster_nodes'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_cluster_nodes_delete AFTER DELETE ON cluster_nodes
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='cluster_nodes';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'cluster_nodes',json_object('id',OLD.id),revision FROM cluster_sync_capture_tables WHERE entity_type='cluster_nodes'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_node_enrollment_requests_insert AFTER INSERT ON node_enrollment_requests
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='node_enrollment_requests';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'node_enrollment_requests',json_object('node_id',NEW.node_id),revision FROM cluster_sync_capture_tables WHERE entity_type='node_enrollment_requests'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_node_enrollment_requests_update AFTER UPDATE OF node_id,pairing_key_hash,node_token_hash,peer_id,peer_public_key,name,roles,max_concurrent_tasks,capabilities,created_at,expires_at,inventory,display_name ON node_enrollment_requests
WHEN OLD.node_id IS NOT NEW.node_id OR OLD.pairing_key_hash IS NOT NEW.pairing_key_hash OR OLD.node_token_hash IS NOT NEW.node_token_hash OR OLD.peer_id IS NOT NEW.peer_id OR OLD.peer_public_key IS NOT NEW.peer_public_key OR OLD.name IS NOT NEW.name OR OLD.roles IS NOT NEW.roles OR OLD.max_concurrent_tasks IS NOT NEW.max_concurrent_tasks OR OLD.capabilities IS NOT NEW.capabilities OR OLD.created_at IS NOT NEW.created_at OR OLD.expires_at IS NOT NEW.expires_at OR OLD.inventory IS NOT NEW.inventory OR OLD.display_name IS NOT NEW.display_name
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='node_enrollment_requests';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'node_enrollment_requests',json_object('node_id',NEW.node_id),revision FROM cluster_sync_capture_tables WHERE entity_type='node_enrollment_requests'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'node_enrollment_requests',json_object('node_id',OLD.node_id),revision FROM cluster_sync_capture_tables WHERE entity_type='node_enrollment_requests'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_node_enrollment_requests_delete AFTER DELETE ON node_enrollment_requests
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='node_enrollment_requests';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'node_enrollment_requests',json_object('node_id',OLD.node_id),revision FROM cluster_sync_capture_tables WHERE entity_type='node_enrollment_requests'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_cluster_peer_admissions_insert AFTER INSERT ON cluster_peer_admissions
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='cluster_peer_admissions';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'cluster_peer_admissions',json_object('cluster_id',NEW.cluster_id,'peer_id',NEW.peer_id),revision FROM cluster_sync_capture_tables WHERE entity_type='cluster_peer_admissions'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_cluster_peer_admissions_update AFTER UPDATE OF cluster_id,peer_id,node_id,public_key,approved_by_peer_id,wrapped_data_key,key_fingerprint,issued_at,revoked_at,signature ON cluster_peer_admissions
WHEN OLD.cluster_id IS NOT NEW.cluster_id OR OLD.peer_id IS NOT NEW.peer_id OR OLD.node_id IS NOT NEW.node_id OR OLD.public_key IS NOT NEW.public_key OR OLD.approved_by_peer_id IS NOT NEW.approved_by_peer_id OR OLD.wrapped_data_key IS NOT NEW.wrapped_data_key OR OLD.key_fingerprint IS NOT NEW.key_fingerprint OR OLD.issued_at IS NOT NEW.issued_at OR OLD.revoked_at IS NOT NEW.revoked_at OR OLD.signature IS NOT NEW.signature
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='cluster_peer_admissions';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'cluster_peer_admissions',json_object('cluster_id',NEW.cluster_id,'peer_id',NEW.peer_id),revision FROM cluster_sync_capture_tables WHERE entity_type='cluster_peer_admissions'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'cluster_peer_admissions',json_object('cluster_id',OLD.cluster_id,'peer_id',OLD.peer_id),revision FROM cluster_sync_capture_tables WHERE entity_type='cluster_peer_admissions'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_cluster_peer_admissions_delete AFTER DELETE ON cluster_peer_admissions
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='cluster_peer_admissions';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'cluster_peer_admissions',json_object('cluster_id',OLD.cluster_id,'peer_id',OLD.peer_id),revision FROM cluster_sync_capture_tables WHERE entity_type='cluster_peer_admissions'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_node_groups_insert AFTER INSERT ON node_groups
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='node_groups';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'node_groups',json_object('id',NEW.id),revision FROM cluster_sync_capture_tables WHERE entity_type='node_groups'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_node_groups_update AFTER UPDATE OF id,name,description,mode,criteria,labels,created_at,updated_at ON node_groups
WHEN OLD.id IS NOT NEW.id OR OLD.name IS NOT NEW.name OR OLD.description IS NOT NEW.description OR OLD.mode IS NOT NEW.mode OR OLD.criteria IS NOT NEW.criteria OR OLD.labels IS NOT NEW.labels OR OLD.created_at IS NOT NEW.created_at OR OLD.updated_at IS NOT NEW.updated_at
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='node_groups';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'node_groups',json_object('id',NEW.id),revision FROM cluster_sync_capture_tables WHERE entity_type='node_groups'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'node_groups',json_object('id',OLD.id),revision FROM cluster_sync_capture_tables WHERE entity_type='node_groups'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_node_groups_delete AFTER DELETE ON node_groups
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='node_groups';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'node_groups',json_object('id',OLD.id),revision FROM cluster_sync_capture_tables WHERE entity_type='node_groups'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_libraries_insert AFTER INSERT ON libraries
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='libraries';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'libraries',json_object('id',NEW.id),revision FROM cluster_sync_capture_tables WHERE entity_type='libraries'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_libraries_update AFTER UPDATE OF id,name,created_at,is_active,flow_assignment_revision ON libraries
WHEN OLD.id IS NOT NEW.id OR OLD.name IS NOT NEW.name OR OLD.created_at IS NOT NEW.created_at OR OLD.is_active IS NOT NEW.is_active OR OLD.flow_assignment_revision IS NOT NEW.flow_assignment_revision
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='libraries';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'libraries',json_object('id',NEW.id),revision FROM cluster_sync_capture_tables WHERE entity_type='libraries'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'libraries',json_object('id',OLD.id),revision FROM cluster_sync_capture_tables WHERE entity_type='libraries'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_libraries_delete AFTER DELETE ON libraries
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='libraries';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'libraries',json_object('id',OLD.id),revision FROM cluster_sync_capture_tables WHERE entity_type='libraries'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_flows_insert AFTER INSERT ON flows
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flows';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flows',json_object('id',NEW.id),revision FROM cluster_sync_capture_tables WHERE entity_type='flows'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_flows_update AFTER UPDATE OF id,current_version,updated_at,deleted_at ON flows
WHEN OLD.id IS NOT NEW.id OR OLD.current_version IS NOT NEW.current_version OR OLD.updated_at IS NOT NEW.updated_at OR OLD.deleted_at IS NOT NEW.deleted_at
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flows';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flows',json_object('id',NEW.id),revision FROM cluster_sync_capture_tables WHERE entity_type='flows'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flows',json_object('id',OLD.id),revision FROM cluster_sync_capture_tables WHERE entity_type='flows'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_flows_delete AFTER DELETE ON flows
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flows';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flows',json_object('id',OLD.id),revision FROM cluster_sync_capture_tables WHERE entity_type='flows'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_flow_scripts_insert AFTER INSERT ON flow_scripts
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flow_scripts';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_scripts',json_object('id',NEW.id),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_scripts'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_flow_scripts_update AFTER UPDATE OF id,draft_revision,draft_name,draft_language,draft_description,draft_code,published_version,created_at,updated_at ON flow_scripts
WHEN OLD.id IS NOT NEW.id OR OLD.draft_revision IS NOT NEW.draft_revision OR OLD.draft_name IS NOT NEW.draft_name OR OLD.draft_language IS NOT NEW.draft_language OR OLD.draft_description IS NOT NEW.draft_description OR OLD.draft_code IS NOT NEW.draft_code OR OLD.published_version IS NOT NEW.published_version OR OLD.created_at IS NOT NEW.created_at OR OLD.updated_at IS NOT NEW.updated_at
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flow_scripts';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_scripts',json_object('id',NEW.id),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_scripts'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_scripts',json_object('id',OLD.id),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_scripts'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_flow_scripts_delete AFTER DELETE ON flow_scripts
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flow_scripts';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_scripts',json_object('id',OLD.id),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_scripts'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_encoding_profiles_insert AFTER INSERT ON encoding_profiles
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='encoding_profiles';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'encoding_profiles',json_object('id',NEW.id),revision FROM cluster_sync_capture_tables WHERE entity_type='encoding_profiles'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_encoding_profiles_update AFTER UPDATE OF id,draft_revision,draft_name,draft_description,draft_targets,published_version,created_at,updated_at ON encoding_profiles
WHEN OLD.id IS NOT NEW.id OR OLD.draft_revision IS NOT NEW.draft_revision OR OLD.draft_name IS NOT NEW.draft_name OR OLD.draft_description IS NOT NEW.draft_description OR OLD.draft_targets IS NOT NEW.draft_targets OR OLD.published_version IS NOT NEW.published_version OR OLD.created_at IS NOT NEW.created_at OR OLD.updated_at IS NOT NEW.updated_at
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='encoding_profiles';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'encoding_profiles',json_object('id',NEW.id),revision FROM cluster_sync_capture_tables WHERE entity_type='encoding_profiles'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'encoding_profiles',json_object('id',OLD.id),revision FROM cluster_sync_capture_tables WHERE entity_type='encoding_profiles'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_encoding_profiles_delete AFTER DELETE ON encoding_profiles
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='encoding_profiles';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'encoding_profiles',json_object('id',OLD.id),revision FROM cluster_sync_capture_tables WHERE entity_type='encoding_profiles'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_users_insert AFTER INSERT ON users
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='users';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'users',json_object('id',NEW.id),revision FROM cluster_sync_capture_tables WHERE entity_type='users'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_users_update AFTER UPDATE OF id,username,password_hash,role,is_active,failed_attempts,locked_until,created_at,updated_at ON users
WHEN OLD.id IS NOT NEW.id OR OLD.username IS NOT NEW.username OR OLD.password_hash IS NOT NEW.password_hash OR OLD.role IS NOT NEW.role OR OLD.is_active IS NOT NEW.is_active OR OLD.failed_attempts IS NOT NEW.failed_attempts OR OLD.locked_until IS NOT NEW.locked_until OR OLD.created_at IS NOT NEW.created_at OR OLD.updated_at IS NOT NEW.updated_at
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='users';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'users',json_object('id',NEW.id),revision FROM cluster_sync_capture_tables WHERE entity_type='users'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'users',json_object('id',OLD.id),revision FROM cluster_sync_capture_tables WHERE entity_type='users'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_users_delete AFTER DELETE ON users
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='users';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'users',json_object('id',OLD.id),revision FROM cluster_sync_capture_tables WHERE entity_type='users'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_api_tokens_insert AFTER INSERT ON api_tokens
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='api_tokens';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'api_tokens',json_object('id',NEW.id),revision FROM cluster_sync_capture_tables WHERE entity_type='api_tokens'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_api_tokens_update AFTER UPDATE OF id,name,token_hash,token_prefix,role,is_active,created_at,expires_at,last_used_at,revoked_at,scopes ON api_tokens
WHEN OLD.id IS NOT NEW.id OR OLD.name IS NOT NEW.name OR OLD.token_hash IS NOT NEW.token_hash OR OLD.token_prefix IS NOT NEW.token_prefix OR OLD.role IS NOT NEW.role OR OLD.is_active IS NOT NEW.is_active OR OLD.created_at IS NOT NEW.created_at OR OLD.expires_at IS NOT NEW.expires_at OR OLD.last_used_at IS NOT NEW.last_used_at OR OLD.revoked_at IS NOT NEW.revoked_at OR OLD.scopes IS NOT NEW.scopes
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='api_tokens';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'api_tokens',json_object('id',NEW.id),revision FROM cluster_sync_capture_tables WHERE entity_type='api_tokens'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'api_tokens',json_object('id',OLD.id),revision FROM cluster_sync_capture_tables WHERE entity_type='api_tokens'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_api_tokens_delete AFTER DELETE ON api_tokens
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='api_tokens';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'api_tokens',json_object('id',OLD.id),revision FROM cluster_sync_capture_tables WHERE entity_type='api_tokens'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_ai_credentials_insert AFTER INSERT ON ai_credentials
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='ai_credentials';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'ai_credentials',json_object('provider',NEW.provider),revision FROM cluster_sync_capture_tables WHERE entity_type='ai_credentials'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_ai_credentials_update AFTER UPDATE OF provider,revision,encrypted_values,updated_at ON ai_credentials
WHEN OLD.provider IS NOT NEW.provider OR OLD.revision IS NOT NEW.revision OR OLD.encrypted_values IS NOT NEW.encrypted_values OR OLD.updated_at IS NOT NEW.updated_at
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='ai_credentials';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'ai_credentials',json_object('provider',NEW.provider),revision FROM cluster_sync_capture_tables WHERE entity_type='ai_credentials'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'ai_credentials',json_object('provider',OLD.provider),revision FROM cluster_sync_capture_tables WHERE entity_type='ai_credentials'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_ai_credentials_delete AFTER DELETE ON ai_credentials
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='ai_credentials';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'ai_credentials',json_object('provider',OLD.provider),revision FROM cluster_sync_capture_tables WHERE entity_type='ai_credentials'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_plugin_configurations_insert AFTER INSERT ON plugin_configurations
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='plugin_configurations';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'plugin_configurations',json_object('plugin_id',NEW.plugin_id),revision FROM cluster_sync_capture_tables WHERE entity_type='plugin_configurations'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_plugin_configurations_update AFTER UPDATE OF plugin_id,revision,encrypted_values,updated_at ON plugin_configurations
WHEN OLD.plugin_id IS NOT NEW.plugin_id OR OLD.revision IS NOT NEW.revision OR OLD.encrypted_values IS NOT NEW.encrypted_values OR OLD.updated_at IS NOT NEW.updated_at
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='plugin_configurations';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'plugin_configurations',json_object('plugin_id',NEW.plugin_id),revision FROM cluster_sync_capture_tables WHERE entity_type='plugin_configurations'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'plugin_configurations',json_object('plugin_id',OLD.plugin_id),revision FROM cluster_sync_capture_tables WHERE entity_type='plugin_configurations'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_plugin_configurations_delete AFTER DELETE ON plugin_configurations
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='plugin_configurations';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'plugin_configurations',json_object('plugin_id',OLD.plugin_id),revision FROM cluster_sync_capture_tables WHERE entity_type='plugin_configurations'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_flow_plugin_configurations_insert AFTER INSERT ON flow_plugin_configurations
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flow_plugin_configurations';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_plugin_configurations',json_object('flow_id',NEW.flow_id,'plugin_id',NEW.plugin_id),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_plugin_configurations'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_flow_plugin_configurations_update AFTER UPDATE OF flow_id,plugin_id,revision,encrypted_values,updated_at ON flow_plugin_configurations
WHEN OLD.flow_id IS NOT NEW.flow_id OR OLD.plugin_id IS NOT NEW.plugin_id OR OLD.revision IS NOT NEW.revision OR OLD.encrypted_values IS NOT NEW.encrypted_values OR OLD.updated_at IS NOT NEW.updated_at
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flow_plugin_configurations';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_plugin_configurations',json_object('flow_id',NEW.flow_id,'plugin_id',NEW.plugin_id),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_plugin_configurations'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_plugin_configurations',json_object('flow_id',OLD.flow_id,'plugin_id',OLD.plugin_id),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_plugin_configurations'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_flow_plugin_configurations_delete AFTER DELETE ON flow_plugin_configurations
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flow_plugin_configurations';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_plugin_configurations',json_object('flow_id',OLD.flow_id,'plugin_id',OLD.plugin_id),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_plugin_configurations'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_enrollment_window_insert AFTER INSERT ON enrollment_window
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='enrollment_window';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'enrollment_window',json_object('id',NEW.id),revision FROM cluster_sync_capture_tables WHERE entity_type='enrollment_window'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_enrollment_window_update AFTER UPDATE OF id,open_until ON enrollment_window
WHEN OLD.id IS NOT NEW.id OR OLD.open_until IS NOT NEW.open_until
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='enrollment_window';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'enrollment_window',json_object('id',NEW.id),revision FROM cluster_sync_capture_tables WHERE entity_type='enrollment_window'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'enrollment_window',json_object('id',OLD.id),revision FROM cluster_sync_capture_tables WHERE entity_type='enrollment_window'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_enrollment_window_delete AFTER DELETE ON enrollment_window
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='enrollment_window';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'enrollment_window',json_object('id',OLD.id),revision FROM cluster_sync_capture_tables WHERE entity_type='enrollment_window'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_first_link_acceptance_insert AFTER INSERT ON first_link_acceptance
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='first_link_acceptance';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'first_link_acceptance',json_object('id',NEW.id),revision FROM cluster_sync_capture_tables WHERE entity_type='first_link_acceptance'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_first_link_acceptance_update AFTER UPDATE OF id,expires_at,enabled_by ON first_link_acceptance
WHEN OLD.id IS NOT NEW.id OR OLD.expires_at IS NOT NEW.expires_at OR OLD.enabled_by IS NOT NEW.enabled_by
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='first_link_acceptance';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'first_link_acceptance',json_object('id',NEW.id),revision FROM cluster_sync_capture_tables WHERE entity_type='first_link_acceptance'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'first_link_acceptance',json_object('id',OLD.id),revision FROM cluster_sync_capture_tables WHERE entity_type='first_link_acceptance'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_first_link_acceptance_delete AFTER DELETE ON first_link_acceptance
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='first_link_acceptance';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'first_link_acceptance',json_object('id',OLD.id),revision FROM cluster_sync_capture_tables WHERE entity_type='first_link_acceptance'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_node_processing_settings_insert AFTER INSERT ON node_processing_settings
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='node_processing_settings';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'node_processing_settings',json_object('node_id',NEW.node_id),revision FROM cluster_sync_capture_tables WHERE entity_type='node_processing_settings'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_node_processing_settings_update AFTER UPDATE OF node_id,max_concurrent_tasks,revision,schedule_enabled,schedule_timezone,schedule_slots ON node_processing_settings
WHEN OLD.node_id IS NOT NEW.node_id OR OLD.max_concurrent_tasks IS NOT NEW.max_concurrent_tasks OR OLD.revision IS NOT NEW.revision OR OLD.schedule_enabled IS NOT NEW.schedule_enabled OR OLD.schedule_timezone IS NOT NEW.schedule_timezone OR OLD.schedule_slots IS NOT NEW.schedule_slots
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='node_processing_settings';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'node_processing_settings',json_object('node_id',NEW.node_id),revision FROM cluster_sync_capture_tables WHERE entity_type='node_processing_settings'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'node_processing_settings',json_object('node_id',OLD.node_id),revision FROM cluster_sync_capture_tables WHERE entity_type='node_processing_settings'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_node_processing_settings_delete AFTER DELETE ON node_processing_settings
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='node_processing_settings';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'node_processing_settings',json_object('node_id',OLD.node_id),revision FROM cluster_sync_capture_tables WHERE entity_type='node_processing_settings'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_node_group_members_insert AFTER INSERT ON node_group_members
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='node_group_members';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'node_group_members',json_object('group_id',NEW.group_id,'node_id',NEW.node_id),revision FROM cluster_sync_capture_tables WHERE entity_type='node_group_members'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_node_group_members_update AFTER UPDATE OF group_id,node_id ON node_group_members
WHEN OLD.group_id IS NOT NEW.group_id OR OLD.node_id IS NOT NEW.node_id
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='node_group_members';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'node_group_members',json_object('group_id',NEW.group_id,'node_id',NEW.node_id),revision FROM cluster_sync_capture_tables WHERE entity_type='node_group_members'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'node_group_members',json_object('group_id',OLD.group_id,'node_id',OLD.node_id),revision FROM cluster_sync_capture_tables WHERE entity_type='node_group_members'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_node_group_members_delete AFTER DELETE ON node_group_members
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='node_group_members';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'node_group_members',json_object('group_id',OLD.group_id,'node_id',OLD.node_id),revision FROM cluster_sync_capture_tables WHERE entity_type='node_group_members'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_flow_versions_insert AFTER INSERT ON flow_versions
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flow_versions';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_versions',json_object('flow_id',NEW.flow_id,'version',NEW.version),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_versions'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_flow_versions_update AFTER UPDATE OF flow_id,version,definition,created_at ON flow_versions
WHEN OLD.flow_id IS NOT NEW.flow_id OR OLD.version IS NOT NEW.version OR OLD.definition IS NOT NEW.definition OR OLD.created_at IS NOT NEW.created_at
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flow_versions';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_versions',json_object('flow_id',NEW.flow_id,'version',NEW.version),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_versions'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_versions',json_object('flow_id',OLD.flow_id,'version',OLD.version),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_versions'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_flow_versions_delete AFTER DELETE ON flow_versions
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flow_versions';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_versions',json_object('flow_id',OLD.flow_id,'version',OLD.version),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_versions'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_flow_backup_jobs_insert AFTER INSERT ON flow_backup_jobs
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flow_backup_jobs';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_backup_jobs',json_object('flow_id',NEW.flow_id,'version',NEW.version),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_backup_jobs'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_flow_backup_jobs_update AFTER UPDATE OF flow_id,version ON flow_backup_jobs
WHEN OLD.flow_id IS NOT NEW.flow_id OR OLD.version IS NOT NEW.version
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flow_backup_jobs';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_backup_jobs',json_object('flow_id',NEW.flow_id,'version',NEW.version),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_backup_jobs'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_backup_jobs',json_object('flow_id',OLD.flow_id,'version',OLD.version),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_backup_jobs'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_flow_backup_jobs_delete AFTER DELETE ON flow_backup_jobs
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flow_backup_jobs';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_backup_jobs',json_object('flow_id',OLD.flow_id,'version',OLD.version),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_backup_jobs'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_flow_drafts_insert AFTER INSERT ON flow_drafts
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flow_drafts';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_drafts',json_object('flow_id',NEW.flow_id),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_drafts'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_flow_drafts_update AFTER UPDATE OF flow_id,revision,definition,updated_at ON flow_drafts
WHEN OLD.flow_id IS NOT NEW.flow_id OR OLD.revision IS NOT NEW.revision OR OLD.definition IS NOT NEW.definition OR OLD.updated_at IS NOT NEW.updated_at
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flow_drafts';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_drafts',json_object('flow_id',NEW.flow_id),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_drafts'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_drafts',json_object('flow_id',OLD.flow_id),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_drafts'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_flow_drafts_delete AFTER DELETE ON flow_drafts
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flow_drafts';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_drafts',json_object('flow_id',OLD.flow_id),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_drafts'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_flow_draft_backups_insert AFTER INSERT ON flow_draft_backups
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flow_draft_backups';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_draft_backups',json_object('flow_id',NEW.flow_id,'revision',NEW.revision),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_draft_backups'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_flow_draft_backups_update AFTER UPDATE OF flow_id,revision,definition ON flow_draft_backups
WHEN OLD.flow_id IS NOT NEW.flow_id OR OLD.revision IS NOT NEW.revision OR OLD.definition IS NOT NEW.definition
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flow_draft_backups';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_draft_backups',json_object('flow_id',NEW.flow_id,'revision',NEW.revision),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_draft_backups'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_draft_backups',json_object('flow_id',OLD.flow_id,'revision',OLD.revision),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_draft_backups'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_flow_draft_backups_delete AFTER DELETE ON flow_draft_backups
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flow_draft_backups';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_draft_backups',json_object('flow_id',OLD.flow_id,'revision',OLD.revision),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_draft_backups'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_flow_script_versions_insert AFTER INSERT ON flow_script_versions
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flow_script_versions';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_script_versions',json_object('script_id',NEW.script_id,'version',NEW.version),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_script_versions'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_flow_script_versions_update AFTER UPDATE OF script_id,version,name,language,description,code,created_at ON flow_script_versions
WHEN OLD.script_id IS NOT NEW.script_id OR OLD.version IS NOT NEW.version OR OLD.name IS NOT NEW.name OR OLD.language IS NOT NEW.language OR OLD.description IS NOT NEW.description OR OLD.code IS NOT NEW.code OR OLD.created_at IS NOT NEW.created_at
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flow_script_versions';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_script_versions',json_object('script_id',NEW.script_id,'version',NEW.version),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_script_versions'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_script_versions',json_object('script_id',OLD.script_id,'version',OLD.version),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_script_versions'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_flow_script_versions_delete AFTER DELETE ON flow_script_versions
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flow_script_versions';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_script_versions',json_object('script_id',OLD.script_id,'version',OLD.version),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_script_versions'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_encoding_profile_versions_insert AFTER INSERT ON encoding_profile_versions
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='encoding_profile_versions';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'encoding_profile_versions',json_object('profile_id',NEW.profile_id,'version',NEW.version),revision FROM cluster_sync_capture_tables WHERE entity_type='encoding_profile_versions'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_encoding_profile_versions_update AFTER UPDATE OF profile_id,version,name,description,targets,created_at ON encoding_profile_versions
WHEN OLD.profile_id IS NOT NEW.profile_id OR OLD.version IS NOT NEW.version OR OLD.name IS NOT NEW.name OR OLD.description IS NOT NEW.description OR OLD.targets IS NOT NEW.targets OR OLD.created_at IS NOT NEW.created_at
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='encoding_profile_versions';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'encoding_profile_versions',json_object('profile_id',NEW.profile_id,'version',NEW.version),revision FROM cluster_sync_capture_tables WHERE entity_type='encoding_profile_versions'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'encoding_profile_versions',json_object('profile_id',OLD.profile_id,'version',OLD.version),revision FROM cluster_sync_capture_tables WHERE entity_type='encoding_profile_versions'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_encoding_profile_versions_delete AFTER DELETE ON encoding_profile_versions
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='encoding_profile_versions';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'encoding_profile_versions',json_object('profile_id',OLD.profile_id,'version',OLD.version),revision FROM cluster_sync_capture_tables WHERE entity_type='encoding_profile_versions'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

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

CREATE TRIGGER capture_flow_templates_insert AFTER INSERT ON flow_templates
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flow_templates';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_templates',json_object('id',NEW.id),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_templates'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_flow_templates_update AFTER UPDATE OF id,name,description,source_flow_id,source_version,source_flow_name,media_type,definition,created_at ON flow_templates
WHEN OLD.id IS NOT NEW.id OR OLD.name IS NOT NEW.name OR OLD.description IS NOT NEW.description OR OLD.source_flow_id IS NOT NEW.source_flow_id OR OLD.source_version IS NOT NEW.source_version OR OLD.source_flow_name IS NOT NEW.source_flow_name OR OLD.media_type IS NOT NEW.media_type OR OLD.definition IS NOT NEW.definition OR OLD.created_at IS NOT NEW.created_at
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

CREATE TRIGGER capture_flow_templates_delete AFTER DELETE ON flow_templates
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flow_templates';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_templates',json_object('id',OLD.id),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_templates'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_flow_component_packs_insert AFTER INSERT ON flow_component_packs
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flow_component_packs';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_component_packs',json_object('id',NEW.id),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_component_packs'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_flow_component_packs_update AFTER UPDATE OF id,name,description,source_flow_id,source_version,source_flow_name,media_type,input_node_id,definition,outputs,created_at ON flow_component_packs
WHEN OLD.id IS NOT NEW.id OR OLD.name IS NOT NEW.name OR OLD.description IS NOT NEW.description OR OLD.source_flow_id IS NOT NEW.source_flow_id OR OLD.source_version IS NOT NEW.source_version OR OLD.source_flow_name IS NOT NEW.source_flow_name OR OLD.media_type IS NOT NEW.media_type OR OLD.input_node_id IS NOT NEW.input_node_id OR OLD.definition IS NOT NEW.definition OR OLD.outputs IS NOT NEW.outputs OR OLD.created_at IS NOT NEW.created_at
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flow_component_packs';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_component_packs',json_object('id',NEW.id),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_component_packs'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_component_packs',json_object('id',OLD.id),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_component_packs'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_flow_component_packs_delete AFTER DELETE ON flow_component_packs
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='flow_component_packs';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'flow_component_packs',json_object('id',OLD.id),revision FROM cluster_sync_capture_tables WHERE entity_type='flow_component_packs'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_library_flow_assignments_insert AFTER INSERT ON library_flow_assignments
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='library_flow_assignments';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'library_flow_assignments',json_object('library_id',NEW.library_id),revision FROM cluster_sync_capture_tables WHERE entity_type='library_flow_assignments'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_library_flow_assignments_update AFTER UPDATE OF library_id,flow_id,flow_version,revision ON library_flow_assignments
WHEN OLD.library_id IS NOT NEW.library_id OR OLD.flow_id IS NOT NEW.flow_id OR OLD.flow_version IS NOT NEW.flow_version OR OLD.revision IS NOT NEW.revision
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='library_flow_assignments';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'library_flow_assignments',json_object('library_id',NEW.library_id),revision FROM cluster_sync_capture_tables WHERE entity_type='library_flow_assignments'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'library_flow_assignments',json_object('library_id',OLD.library_id),revision FROM cluster_sync_capture_tables WHERE entity_type='library_flow_assignments'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_library_flow_assignments_delete AFTER DELETE ON library_flow_assignments
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='library_flow_assignments';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'library_flow_assignments',json_object('library_id',OLD.library_id),revision FROM cluster_sync_capture_tables WHERE entity_type='library_flow_assignments'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_library_node_accesses_insert AFTER INSERT ON library_node_accesses
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='library_node_accesses';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'library_node_accesses',json_object('library_id',NEW.library_id,'node_id',NEW.node_id),revision FROM cluster_sync_capture_tables WHERE entity_type='library_node_accesses'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_library_node_accesses_update AFTER UPDATE OF library_id,node_id,mode,local_path,can_read,can_write,transfer_source_node_id,updated_at,is_enabled ON library_node_accesses
WHEN OLD.library_id IS NOT NEW.library_id OR OLD.node_id IS NOT NEW.node_id OR OLD.mode IS NOT NEW.mode OR OLD.local_path IS NOT NEW.local_path OR OLD.can_read IS NOT NEW.can_read OR OLD.can_write IS NOT NEW.can_write OR OLD.transfer_source_node_id IS NOT NEW.transfer_source_node_id OR OLD.updated_at IS NOT NEW.updated_at OR OLD.is_enabled IS NOT NEW.is_enabled
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='library_node_accesses';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'library_node_accesses',json_object('library_id',NEW.library_id,'node_id',NEW.node_id),revision FROM cluster_sync_capture_tables WHERE entity_type='library_node_accesses'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'library_node_accesses',json_object('library_id',OLD.library_id,'node_id',OLD.node_id),revision FROM cluster_sync_capture_tables WHERE entity_type='library_node_accesses'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_library_node_accesses_delete AFTER DELETE ON library_node_accesses
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='library_node_accesses';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'library_node_accesses',json_object('library_id',OLD.library_id,'node_id',OLD.node_id),revision FROM cluster_sync_capture_tables WHERE entity_type='library_node_accesses'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_library_node_processing_limits_insert AFTER INSERT ON library_node_processing_limits
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='library_node_processing_limits';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'library_node_processing_limits',json_object('library_id',NEW.library_id,'node_id',NEW.node_id),revision FROM cluster_sync_capture_tables WHERE entity_type='library_node_processing_limits'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_library_node_processing_limits_update AFTER UPDATE OF library_id,node_id,max_concurrent_tasks ON library_node_processing_limits
WHEN OLD.library_id IS NOT NEW.library_id OR OLD.node_id IS NOT NEW.node_id OR OLD.max_concurrent_tasks IS NOT NEW.max_concurrent_tasks
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='library_node_processing_limits';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'library_node_processing_limits',json_object('library_id',NEW.library_id,'node_id',NEW.node_id),revision FROM cluster_sync_capture_tables WHERE entity_type='library_node_processing_limits'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'library_node_processing_limits',json_object('library_id',OLD.library_id,'node_id',OLD.node_id),revision FROM cluster_sync_capture_tables WHERE entity_type='library_node_processing_limits'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_library_node_processing_limits_delete AFTER DELETE ON library_node_processing_limits
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='library_node_processing_limits';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'library_node_processing_limits',json_object('library_id',OLD.library_id,'node_id',OLD.node_id),revision FROM cluster_sync_capture_tables WHERE entity_type='library_node_processing_limits'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_library_processing_settings_insert AFTER INSERT ON library_processing_settings
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='library_processing_settings';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'library_processing_settings',json_object('library_id',NEW.library_id),revision FROM cluster_sync_capture_tables WHERE entity_type='library_processing_settings'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_library_processing_settings_update AFTER UPDATE OF library_id,watch_enabled,scan_interval_seconds,max_concurrent_tasks,revision ON library_processing_settings
WHEN OLD.library_id IS NOT NEW.library_id OR OLD.watch_enabled IS NOT NEW.watch_enabled OR OLD.scan_interval_seconds IS NOT NEW.scan_interval_seconds OR OLD.max_concurrent_tasks IS NOT NEW.max_concurrent_tasks OR OLD.revision IS NOT NEW.revision
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='library_processing_settings';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'library_processing_settings',json_object('library_id',NEW.library_id),revision FROM cluster_sync_capture_tables WHERE entity_type='library_processing_settings'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'library_processing_settings',json_object('library_id',OLD.library_id),revision FROM cluster_sync_capture_tables WHERE entity_type='library_processing_settings'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_library_processing_settings_delete AFTER DELETE ON library_processing_settings
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='library_processing_settings';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'library_processing_settings',json_object('library_id',OLD.library_id),revision FROM cluster_sync_capture_tables WHERE entity_type='library_processing_settings'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_library_replacement_policies_insert AFTER INSERT ON library_replacement_policies
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='library_replacement_policies';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'library_replacement_policies',json_object('library_id',NEW.library_id),revision FROM cluster_sync_capture_tables WHERE entity_type='library_replacement_policies'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_library_replacement_policies_update AFTER UPDATE OF library_id,enabled,grace_period_hours,revision ON library_replacement_policies
WHEN OLD.library_id IS NOT NEW.library_id OR OLD.enabled IS NOT NEW.enabled OR OLD.grace_period_hours IS NOT NEW.grace_period_hours OR OLD.revision IS NOT NEW.revision
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='library_replacement_policies';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'library_replacement_policies',json_object('library_id',NEW.library_id),revision FROM cluster_sync_capture_tables WHERE entity_type='library_replacement_policies'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'library_replacement_policies',json_object('library_id',OLD.library_id),revision FROM cluster_sync_capture_tables WHERE entity_type='library_replacement_policies'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_library_replacement_policies_delete AFTER DELETE ON library_replacement_policies
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='library_replacement_policies';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'library_replacement_policies',json_object('library_id',OLD.library_id),revision FROM cluster_sync_capture_tables WHERE entity_type='library_replacement_policies'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_executions_insert AFTER INSERT ON executions
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='executions';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'executions',json_object('id',NEW.id),revision FROM cluster_sync_capture_tables WHERE entity_type='executions'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_executions_update AFTER UPDATE OF id,media,library_name,flow_name,flow_version,flow_definition,state,node_id,progress,detail,attempt,retry_of,revision,queued_at,started_at,finished_at,updated_at ON executions
WHEN OLD.id IS NOT NEW.id OR OLD.media IS NOT NEW.media OR OLD.library_name IS NOT NEW.library_name OR OLD.flow_name IS NOT NEW.flow_name OR OLD.flow_version IS NOT NEW.flow_version OR OLD.flow_definition IS NOT NEW.flow_definition OR OLD.state IS NOT NEW.state OR OLD.node_id IS NOT NEW.node_id OR OLD.progress IS NOT NEW.progress OR OLD.detail IS NOT NEW.detail OR OLD.attempt IS NOT NEW.attempt OR OLD.retry_of IS NOT NEW.retry_of OR OLD.revision IS NOT NEW.revision OR OLD.queued_at IS NOT NEW.queued_at OR OLD.started_at IS NOT NEW.started_at OR OLD.finished_at IS NOT NEW.finished_at OR OLD.updated_at IS NOT NEW.updated_at
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='executions';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'executions',json_object('id',NEW.id),revision FROM cluster_sync_capture_tables WHERE entity_type='executions'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'executions',json_object('id',OLD.id),revision FROM cluster_sync_capture_tables WHERE entity_type='executions'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_executions_delete AFTER DELETE ON executions
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='executions';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'executions',json_object('id',OLD.id),revision FROM cluster_sync_capture_tables WHERE entity_type='executions'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_execution_events_insert AFTER INSERT ON execution_events
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='execution_events';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'execution_events',json_object('id',NEW.id),revision FROM cluster_sync_capture_tables WHERE entity_type='execution_events'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_execution_events_update AFTER UPDATE OF id,execution_id,revision,state,detail,created_at ON execution_events
WHEN OLD.id IS NOT NEW.id OR OLD.execution_id IS NOT NEW.execution_id OR OLD.revision IS NOT NEW.revision OR OLD.state IS NOT NEW.state OR OLD.detail IS NOT NEW.detail OR OLD.created_at IS NOT NEW.created_at
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='execution_events';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'execution_events',json_object('id',NEW.id),revision FROM cluster_sync_capture_tables WHERE entity_type='execution_events'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'execution_events',json_object('id',OLD.id),revision FROM cluster_sync_capture_tables WHERE entity_type='execution_events'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_execution_events_delete AFTER DELETE ON execution_events
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='execution_events';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'execution_events',json_object('id',OLD.id),revision FROM cluster_sync_capture_tables WHERE entity_type='execution_events'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_execution_steps_insert AFTER INSERT ON execution_steps
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='execution_steps';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'execution_steps',json_object('execution_id',NEW.execution_id,'node_id',NEW.node_id),revision FROM cluster_sync_capture_tables WHERE entity_type='execution_steps'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_execution_steps_update AFTER UPDATE OF execution_id,node_id,invocation_id,started_at,completed_at,completed_revision,idempotent,checkpoint,completed_epoch,completed_worker_node_id,command_logs ON execution_steps
WHEN OLD.execution_id IS NOT NEW.execution_id OR OLD.node_id IS NOT NEW.node_id OR OLD.invocation_id IS NOT NEW.invocation_id OR OLD.started_at IS NOT NEW.started_at OR OLD.completed_at IS NOT NEW.completed_at OR OLD.completed_revision IS NOT NEW.completed_revision OR OLD.idempotent IS NOT NEW.idempotent OR OLD.checkpoint IS NOT NEW.checkpoint OR OLD.completed_epoch IS NOT NEW.completed_epoch OR OLD.completed_worker_node_id IS NOT NEW.completed_worker_node_id OR OLD.command_logs IS NOT NEW.command_logs
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='execution_steps';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'execution_steps',json_object('execution_id',NEW.execution_id,'node_id',NEW.node_id),revision FROM cluster_sync_capture_tables WHERE entity_type='execution_steps'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'execution_steps',json_object('execution_id',OLD.execution_id,'node_id',OLD.node_id),revision FROM cluster_sync_capture_tables WHERE entity_type='execution_steps'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_execution_steps_delete AFTER DELETE ON execution_steps
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='execution_steps';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'execution_steps',json_object('execution_id',OLD.execution_id,'node_id',OLD.node_id),revision FROM cluster_sync_capture_tables WHERE entity_type='execution_steps'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_execution_artifacts_insert AFTER INSERT ON execution_artifacts
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='execution_artifacts';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'execution_artifacts',json_object('execution_id',NEW.execution_id,'sha256',NEW.sha256),revision FROM cluster_sync_capture_tables WHERE entity_type='execution_artifacts'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_execution_artifacts_update AFTER UPDATE OF execution_id,sha256,byte_length ON execution_artifacts
WHEN OLD.execution_id IS NOT NEW.execution_id OR OLD.sha256 IS NOT NEW.sha256 OR OLD.byte_length IS NOT NEW.byte_length
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='execution_artifacts';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'execution_artifacts',json_object('execution_id',NEW.execution_id,'sha256',NEW.sha256),revision FROM cluster_sync_capture_tables WHERE entity_type='execution_artifacts'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'execution_artifacts',json_object('execution_id',OLD.execution_id,'sha256',OLD.sha256),revision FROM cluster_sync_capture_tables WHERE entity_type='execution_artifacts'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_execution_artifacts_delete AFTER DELETE ON execution_artifacts
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='execution_artifacts';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'execution_artifacts',json_object('execution_id',OLD.execution_id,'sha256',OLD.sha256),revision FROM cluster_sync_capture_tables WHERE entity_type='execution_artifacts'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_execution_inputs_insert AFTER INSERT ON execution_inputs
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='execution_inputs';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'execution_inputs',json_object('execution_id',NEW.execution_id,'input_path',NEW.input_path),revision FROM cluster_sync_capture_tables WHERE entity_type='execution_inputs'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_execution_inputs_update AFTER UPDATE OF execution_id,input_path,sha256 ON execution_inputs
WHEN OLD.execution_id IS NOT NEW.execution_id OR OLD.input_path IS NOT NEW.input_path OR OLD.sha256 IS NOT NEW.sha256
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='execution_inputs';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'execution_inputs',json_object('execution_id',NEW.execution_id,'input_path',NEW.input_path),revision FROM cluster_sync_capture_tables WHERE entity_type='execution_inputs'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'execution_inputs',json_object('execution_id',OLD.execution_id,'input_path',OLD.input_path),revision FROM cluster_sync_capture_tables WHERE entity_type='execution_inputs'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_execution_inputs_delete AFTER DELETE ON execution_inputs
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='execution_inputs';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'execution_inputs',json_object('execution_id',OLD.execution_id,'input_path',OLD.input_path),revision FROM cluster_sync_capture_tables WHERE entity_type='execution_inputs'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_library_scans_insert AFTER INSERT ON library_scans
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='library_scans';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'library_scans',json_object('id',NEW.id),revision FROM cluster_sync_capture_tables WHERE entity_type='library_scans'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_library_scans_update AFTER UPDATE OF id,library_id,media_type,state,node_id,created_at,completed_at,detail ON library_scans
WHEN OLD.id IS NOT NEW.id OR OLD.library_id IS NOT NEW.library_id OR OLD.media_type IS NOT NEW.media_type OR OLD.state IS NOT NEW.state OR OLD.node_id IS NOT NEW.node_id OR OLD.created_at IS NOT NEW.created_at OR OLD.completed_at IS NOT NEW.completed_at OR OLD.detail IS NOT NEW.detail
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='library_scans';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'library_scans',json_object('id',NEW.id),revision FROM cluster_sync_capture_tables WHERE entity_type='library_scans'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'library_scans',json_object('id',OLD.id),revision FROM cluster_sync_capture_tables WHERE entity_type='library_scans'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_library_scans_delete AFTER DELETE ON library_scans
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='library_scans';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'library_scans',json_object('id',OLD.id),revision FROM cluster_sync_capture_tables WHERE entity_type='library_scans'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_media_replacement_marks_insert AFTER INSERT ON media_replacement_marks
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='media_replacement_marks';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'media_replacement_marks',json_object('library_id',NEW.library_id,'relative_path',NEW.relative_path,'byte_length',NEW.byte_length,'modified_at',NEW.modified_at),revision FROM cluster_sync_capture_tables WHERE entity_type='media_replacement_marks'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_media_replacement_marks_update AFTER UPDATE OF library_id,relative_path,byte_length,modified_at,execution_id ON media_replacement_marks
WHEN OLD.library_id IS NOT NEW.library_id OR OLD.relative_path IS NOT NEW.relative_path OR OLD.byte_length IS NOT NEW.byte_length OR OLD.modified_at IS NOT NEW.modified_at OR OLD.execution_id IS NOT NEW.execution_id
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='media_replacement_marks';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'media_replacement_marks',json_object('library_id',NEW.library_id,'relative_path',NEW.relative_path,'byte_length',NEW.byte_length,'modified_at',NEW.modified_at),revision FROM cluster_sync_capture_tables WHERE entity_type='media_replacement_marks'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'media_replacement_marks',json_object('library_id',OLD.library_id,'relative_path',OLD.relative_path,'byte_length',OLD.byte_length,'modified_at',OLD.modified_at),revision FROM cluster_sync_capture_tables WHERE entity_type='media_replacement_marks'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_media_replacement_marks_delete AFTER DELETE ON media_replacement_marks
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='media_replacement_marks';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'media_replacement_marks',json_object('library_id',OLD.library_id,'relative_path',OLD.relative_path,'byte_length',OLD.byte_length,'modified_at',OLD.modified_at),revision FROM cluster_sync_capture_tables WHERE entity_type='media_replacement_marks'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_library_processing_receipts_insert AFTER INSERT ON library_processing_receipts
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='library_processing_receipts';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'library_processing_receipts',json_object('library_id',NEW.library_id,'relative_path',COALESCE((SELECT relative_path FROM library_media WHERE id=NEW.file_id),''),'flow_id',NEW.flow_id,'flow_version',NEW.flow_version,'byte_length',NEW.byte_length,'modified_at',NEW.modified_at),revision FROM cluster_sync_capture_tables WHERE entity_type='library_processing_receipts'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_library_processing_receipts_update AFTER UPDATE OF library_id,file_id,flow_id,flow_version,byte_length,modified_at,execution_id ON library_processing_receipts
WHEN OLD.library_id IS NOT NEW.library_id OR OLD.file_id IS NOT NEW.file_id OR OLD.flow_id IS NOT NEW.flow_id OR OLD.flow_version IS NOT NEW.flow_version OR OLD.byte_length IS NOT NEW.byte_length OR OLD.modified_at IS NOT NEW.modified_at OR OLD.execution_id IS NOT NEW.execution_id
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='library_processing_receipts';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'library_processing_receipts',json_object('library_id',NEW.library_id,'relative_path',COALESCE((SELECT relative_path FROM library_media WHERE id=NEW.file_id),''),'flow_id',NEW.flow_id,'flow_version',NEW.flow_version,'byte_length',NEW.byte_length,'modified_at',NEW.modified_at),revision FROM cluster_sync_capture_tables WHERE entity_type='library_processing_receipts'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'library_processing_receipts',json_object('library_id',OLD.library_id,'relative_path',COALESCE((SELECT relative_path FROM library_media WHERE id=OLD.file_id),''),'flow_id',OLD.flow_id,'flow_version',OLD.flow_version,'byte_length',OLD.byte_length,'modified_at',OLD.modified_at),revision FROM cluster_sync_capture_tables WHERE entity_type='library_processing_receipts'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_library_processing_receipts_delete AFTER DELETE ON library_processing_receipts
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1 WHERE entity_type='library_processing_receipts';
    INSERT INTO cluster_sync_dirty_entities(entity_type,entity_key,revision)
    SELECT 'library_processing_receipts',json_object('library_id',OLD.library_id,'relative_path',COALESCE((SELECT relative_path FROM library_media WHERE id=OLD.file_id),''),'flow_id',OLD.flow_id,'flow_version',OLD.flow_version,'byte_length',OLD.byte_length,'modified_at',OLD.modified_at),revision FROM cluster_sync_capture_tables WHERE entity_type='library_processing_receipts'
    ON CONFLICT(entity_type,entity_key) DO UPDATE SET revision=excluded.revision
    WHERE cluster_sync_dirty_entities.revision < excluded.revision;
END;

CREATE TRIGGER capture_library_media_insert AFTER INSERT ON library_media
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1,full_scan_revision=revision+1
      WHERE entity_type IN('library_scans','library_processing_receipts');
    DELETE FROM cluster_sync_table_cursors WHERE entity_type='scan-inventory/' || NEW.scan_id;
END;

CREATE TRIGGER capture_library_media_update AFTER UPDATE OF library_id,relative_path,byte_length,modified_at,scan_id,stable_since ON library_media
WHEN OLD.library_id IS NOT NEW.library_id OR OLD.relative_path IS NOT NEW.relative_path OR OLD.byte_length IS NOT NEW.byte_length OR OLD.modified_at IS NOT NEW.modified_at OR OLD.scan_id IS NOT NEW.scan_id OR OLD.stable_since IS NOT NEW.stable_since
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1,full_scan_revision=revision+1
      WHERE entity_type IN('library_scans','library_processing_receipts');
    DELETE FROM cluster_sync_table_cursors WHERE entity_type='scan-inventory/' || NEW.scan_id;
    DELETE FROM cluster_sync_table_cursors WHERE entity_type='scan-inventory/' || OLD.scan_id;
END;

CREATE TRIGGER capture_library_media_delete AFTER DELETE ON library_media
BEGIN
    UPDATE cluster_sync_capture_tables SET revision=revision+1,full_scan_revision=revision+1
      WHERE entity_type IN('library_scans','library_processing_receipts');
    DELETE FROM cluster_sync_table_cursors WHERE entity_type='scan-inventory/' || OLD.scan_id;
END;
