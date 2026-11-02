-- Schéma SQLite de référence (architecture 0043) pour toute nouvelle installation.
-- Les migrations suivantes font évoluer ce schéma de façon incrémentale.
-- Instants : texte UTC de largeur fixe « AAAA-MM-JJ HH:MM:SS.ffffffZ ». JSON et tableaux : texte JSON.

CREATE TABLE cluster_identity (
    singleton BOOLEAN DEFAULT 1 PRIMARY KEY,
    id TEXT NOT NULL DEFAULT ((lower(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-4' || substr(hex(randomblob(2)), 2) || '-' || substr('89ab', abs(random()) % 4 + 1, 1) || substr(hex(randomblob(2)), 2) || '-' || hex(randomblob(6))))),
    name TEXT NOT NULL DEFAULT 'Encoding',
    UNIQUE (id),
    CHECK (singleton)
);

CREATE TABLE cluster_nodes (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    roles TEXT NOT NULL,
    max_concurrent_tasks INTEGER NOT NULL,
    capabilities TEXT NOT NULL DEFAULT ('[]'),
    last_seen_at TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    node_token_hash BLOB,
    deleted_at TEXT,
    display_name TEXT,
    labels TEXT NOT NULL DEFAULT ('{}'),
    inventory TEXT NOT NULL DEFAULT ('{}'),
    desired_roles TEXT,
    draining BOOLEAN NOT NULL DEFAULT 0,
    quarantined BOOLEAN NOT NULL DEFAULT 0,
    incompatible_reason TEXT,
    protocol_version INTEGER,
    session_nonce TEXT,
    previous_nonce TEXT,
    nonce_changed_at TEXT,
    accepts_work BOOLEAN GENERATED ALWAYS AS (((NOT draining) AND (NOT quarantined) AND (incompatible_reason IS NULL))) VIRTUAL,
    CHECK (((max_concurrent_tasks >= 0) AND (max_concurrent_tasks <= 1024)))
);

CREATE TABLE encoding_profile_versions (
    profile_id TEXT NOT NULL,
    version INTEGER NOT NULL,
    name TEXT NOT NULL,
    description TEXT NOT NULL DEFAULT '',
    targets TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    PRIMARY KEY (profile_id, version),
    FOREIGN KEY (profile_id) REFERENCES encoding_profiles(id) ON DELETE RESTRICT,
    CHECK ((version > 0)),
    CHECK (((length(name) >= 1) AND (length(name) <= 128))),
    CHECK ((length(description) <= 1000))
);

CREATE TABLE encoding_profiles (
    id TEXT PRIMARY KEY,
    draft_revision INTEGER NOT NULL DEFAULT 1,
    draft_name TEXT NOT NULL,
    draft_description TEXT NOT NULL DEFAULT '',
    draft_targets TEXT NOT NULL,
    published_version INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    updated_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    CHECK ((draft_revision > 0)),
    CHECK (((length(draft_name) >= 1) AND (length(draft_name) <= 128))),
    CHECK ((length(draft_description) <= 1000)),
    CHECK ((published_version >= 0))
);

CREATE TABLE enrollment_window (
    id BOOLEAN DEFAULT 1 PRIMARY KEY,
    open_until TEXT NOT NULL,
    CHECK (id)
);

CREATE TABLE execution_artifacts (
    execution_id TEXT NOT NULL,
    sha256 TEXT NOT NULL,
    byte_length INTEGER NOT NULL,
    PRIMARY KEY (execution_id, sha256),
    FOREIGN KEY (execution_id) REFERENCES executions(id),
    CHECK ((byte_length > 0))
);

CREATE TABLE execution_events (
    id TEXT PRIMARY KEY,
    execution_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    state TEXT NOT NULL,
    detail TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    UNIQUE (execution_id, revision),
    FOREIGN KEY (execution_id) REFERENCES executions(id) ON DELETE RESTRICT
);

CREATE TABLE execution_inputs (
    execution_id TEXT NOT NULL,
    input_path TEXT NOT NULL,
    sha256 TEXT NOT NULL,
    PRIMARY KEY (execution_id, input_path),
    FOREIGN KEY (execution_id) REFERENCES executions(id),
    FOREIGN KEY (execution_id, sha256) REFERENCES execution_artifacts(execution_id, sha256)
);

CREATE TABLE execution_steps (
    execution_id TEXT NOT NULL,
    node_id TEXT NOT NULL,
    invocation_id TEXT NOT NULL,
    started_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    completed_at TEXT,
    completed_revision INTEGER,
    idempotent BOOLEAN NOT NULL,
    checkpoint TEXT,
    completed_epoch INTEGER,
    completed_worker_node_id TEXT,
    command_logs TEXT NOT NULL DEFAULT ('[]'),
    PRIMARY KEY (execution_id, node_id),
    UNIQUE (invocation_id),
    FOREIGN KEY (execution_id) REFERENCES executions(id),
    FOREIGN KEY (completed_worker_node_id) REFERENCES cluster_nodes(id)
);

CREATE TABLE execution_waits (
    execution_id TEXT PRIMARY KEY,
    node_id TEXT NOT NULL,
    spec TEXT NOT NULL,
    state TEXT NOT NULL DEFAULT 'waiting',
    started_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    deadline TEXT,
    due_at TEXT,
    next_check_at TEXT NOT NULL,
    resolved_at TEXT,
    probe_count INTEGER NOT NULL DEFAULT 0,
    last_probe_at TEXT,
    last_probe_output TEXT,
    FOREIGN KEY (execution_id) REFERENCES executions(id) ON DELETE RESTRICT,
    CHECK (state IN ('waiting', 'met', 'timeout', 'cancelled')),
    CHECK (((state = 'waiting') = (resolved_at IS NULL)))
);

CREATE TABLE execution_work (
    execution_id TEXT PRIMARY KEY,
    library_id TEXT NOT NULL,
    media_path TEXT NOT NULL,
    media_key TEXT NOT NULL,
    source_node_id TEXT,
    worker_node_id TEXT,
    lease_epoch INTEGER NOT NULL DEFAULT 0,
    lease_until TEXT,
    available_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    source_sha TEXT,
    result_sha TEXT,
    result_extension TEXT,
    publication_requested BOOLEAN NOT NULL DEFAULT 0,
    published_path TEXT,
    before_metadata TEXT,
    after_metadata TEXT,
    handoff_pending BOOLEAN NOT NULL DEFAULT 0,
    handoff_selector TEXT,
    previous_worker_node_id TEXT,
    plugin_configuration_snapshot BLOB,
    artifacts_purged_at TEXT,
    replacement_grace_hours INTEGER,
    wait_probe_due BOOLEAN NOT NULL DEFAULT 0,
    media_policy TEXT NOT NULL DEFAULT 'keep',
    FOREIGN KEY (execution_id) REFERENCES executions(id) ON DELETE RESTRICT,
    FOREIGN KEY (library_id) REFERENCES libraries(id) ON DELETE RESTRICT,
    FOREIGN KEY (source_node_id) REFERENCES cluster_nodes(id),
    FOREIGN KEY (worker_node_id) REFERENCES cluster_nodes(id),
    FOREIGN KEY (previous_worker_node_id) REFERENCES cluster_nodes(id),
    CHECK (((NOT handoff_pending) OR ((worker_node_id IS NULL) AND (lease_until IS NULL) AND (handoff_selector IS NOT NULL) AND (previous_worker_node_id IS NOT NULL)))),
    CHECK (((replacement_grace_hours >= 1) AND (replacement_grace_hours <= 8760))),
    CHECK (media_policy IN ('keep', 'beside', 'overwrite'))
);

CREATE TABLE executions (
    id TEXT PRIMARY KEY,
    media TEXT NOT NULL,
    library_name TEXT NOT NULL,
    flow_name TEXT NOT NULL,
    flow_version INTEGER NOT NULL,
    flow_definition TEXT NOT NULL,
    state TEXT NOT NULL,
    node_id TEXT,
    progress NUMERIC,
    detail TEXT NOT NULL DEFAULT '',
    attempt INTEGER NOT NULL,
    retry_of TEXT,
    revision INTEGER NOT NULL DEFAULT 0,
    queued_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    started_at TEXT,
    finished_at TEXT,
    updated_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    UNIQUE (retry_of),
    FOREIGN KEY (node_id) REFERENCES cluster_nodes(id) ON DELETE RESTRICT,
    FOREIGN KEY (retry_of) REFERENCES executions(id),
    CHECK ((flow_version > 0)),
    CHECK (((progress >= 0) AND (progress <= 100))),
    CHECK ((attempt > 0)),
    CHECK (state IN ('queued', 'running', 'cancelling', 'succeeded', 'failed', 'cancelled', 'deferred'))
);

CREATE TABLE first_link_acceptance (
    id BOOLEAN DEFAULT 1 PRIMARY KEY,
    expires_at TEXT NOT NULL,
    enabled_by TEXT NOT NULL,
    CHECK (id)
);

CREATE TABLE flow_backup_jobs (
    flow_id TEXT NOT NULL,
    version INTEGER NOT NULL,
    status TEXT NOT NULL DEFAULT 'pending',
    checked_at TEXT,
    next_attempt_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    PRIMARY KEY (flow_id, version),
    FOREIGN KEY (flow_id, version) REFERENCES flow_versions(flow_id, version) ON DELETE RESTRICT,
    CHECK (status IN ('pending', 'ready', 'failed'))
);

CREATE TABLE flow_component_packs (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    description TEXT NOT NULL DEFAULT '',
    source_flow_id TEXT NOT NULL,
    source_version INTEGER NOT NULL,
    source_flow_name TEXT NOT NULL,
    media_type TEXT NOT NULL,
    input_node_id TEXT NOT NULL,
    definition TEXT NOT NULL,
    outputs TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    FOREIGN KEY (source_flow_id, source_version) REFERENCES flow_versions(flow_id, version) ON DELETE RESTRICT,
    CHECK ((length(description) <= 1000)),
    CHECK (((length(trim(name)) >= 1) AND (length(trim(name)) <= 128))),
    CHECK ((source_version > 0)),
    CHECK (((length(source_flow_name) >= 1) AND (length(source_flow_name) <= 128))),
    CHECK (media_type IN ('video', 'image', 'music', 'subtitles'))
);

CREATE TABLE flow_draft_backups (
    flow_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    definition TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'pending',
    checked_at TEXT,
    next_attempt_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    PRIMARY KEY (flow_id, revision),
    FOREIGN KEY (flow_id) REFERENCES flows(id),
    CHECK (status IN ('pending', 'ready', 'failed'))
);

CREATE TABLE flow_drafts (
    flow_id TEXT PRIMARY KEY,
    revision INTEGER NOT NULL,
    definition TEXT NOT NULL,
    updated_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    FOREIGN KEY (flow_id) REFERENCES flows(id) ON DELETE RESTRICT,
    CHECK ((revision > 0))
);

CREATE TABLE flow_plugin_configurations (
    flow_id TEXT NOT NULL,
    plugin_id TEXT NOT NULL,
    revision INTEGER NOT NULL DEFAULT 1,
    encrypted_values BLOB NOT NULL,
    updated_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    PRIMARY KEY (flow_id, plugin_id),
    FOREIGN KEY (flow_id) REFERENCES flows(id) ON DELETE CASCADE,
    CHECK ((revision > 0))
);

CREATE TABLE flow_script_versions (
    script_id TEXT NOT NULL,
    version INTEGER NOT NULL,
    name TEXT NOT NULL,
    language TEXT NOT NULL,
    description TEXT NOT NULL DEFAULT '',
    code TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    PRIMARY KEY (script_id, version),
    FOREIGN KEY (script_id) REFERENCES flow_scripts(id) ON DELETE RESTRICT,
    CHECK ((version > 0)),
    CHECK (((length(name) >= 1) AND (length(name) <= 128))),
    CHECK (language IN ('javascript', 'python')),
    CHECK ((length(description) <= 1000)),
    CHECK (((length(code) >= 1) AND (length(code) <= 200000)))
);

CREATE TABLE flow_scripts (
    id TEXT PRIMARY KEY,
    draft_revision INTEGER NOT NULL DEFAULT 1,
    draft_name TEXT NOT NULL,
    draft_language TEXT NOT NULL,
    draft_description TEXT NOT NULL DEFAULT '',
    draft_code TEXT NOT NULL DEFAULT '',
    published_version INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    updated_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    CHECK ((draft_revision > 0)),
    CHECK (((length(draft_name) >= 1) AND (length(draft_name) <= 128))),
    CHECK (draft_language IN ('javascript', 'python')),
    CHECK ((length(draft_description) <= 1000)),
    CHECK ((length(draft_code) <= 200000)),
    CHECK ((published_version >= 0))
);

CREATE TABLE flow_templates (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    description TEXT NOT NULL DEFAULT '',
    source_flow_id TEXT NOT NULL,
    source_version INTEGER NOT NULL,
    source_flow_name TEXT NOT NULL,
    media_type TEXT NOT NULL,
    definition TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    FOREIGN KEY (source_flow_id, source_version) REFERENCES flow_versions(flow_id, version) ON DELETE RESTRICT,
    CHECK (((length(trim(name)) >= 1) AND (length(trim(name)) <= 128))),
    CHECK ((length(description) <= 1000)),
    CHECK ((source_version > 0)),
    CHECK (((length(source_flow_name) >= 1) AND (length(source_flow_name) <= 128))),
    CHECK (media_type IN ('video', 'image', 'music', 'subtitles'))
);

CREATE TABLE flow_versions (
    flow_id TEXT NOT NULL,
    version INTEGER NOT NULL,
    definition TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    PRIMARY KEY (flow_id, version),
    FOREIGN KEY (flow_id) REFERENCES flows(id) ON DELETE RESTRICT,
    CHECK ((version > 0))
);

CREATE TABLE flows (
    id TEXT PRIMARY KEY,
    current_version INTEGER NOT NULL DEFAULT 0,
    updated_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z'))
);

CREATE TABLE integration_trigger_requests (
    id TEXT PRIMARY KEY,
    plugin_id TEXT NOT NULL,
    library_id TEXT NOT NULL,
    relative_path TEXT NOT NULL,
    state TEXT NOT NULL DEFAULT 'pending',
    detail TEXT NOT NULL DEFAULT '',
    requested_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    claimed_until TEXT,
    completed_at TEXT,
    FOREIGN KEY (library_id) REFERENCES libraries(id) ON DELETE CASCADE,
    CHECK (state IN ('pending', 'processing', 'queued', 'skipped', 'expired'))
);

CREATE TABLE libraries (
    id TEXT DEFAULT ((lower(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-4' || substr(hex(randomblob(2)), 2) || '-' || substr('89ab', abs(random()) % 4 + 1, 1) || substr(hex(randomblob(2)), 2) || '-' || hex(randomblob(6))))) PRIMARY KEY,
    name TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    is_active BOOLEAN NOT NULL DEFAULT 1,
    flow_assignment_revision INTEGER NOT NULL DEFAULT 0,
    UNIQUE (name)
);

CREATE TABLE library_flow_assignments (
    library_id TEXT PRIMARY KEY,
    flow_id TEXT NOT NULL,
    flow_version INTEGER NOT NULL,
    revision INTEGER NOT NULL DEFAULT 1,
    FOREIGN KEY (flow_id, flow_version) REFERENCES flow_versions(flow_id, version),
    FOREIGN KEY (library_id) REFERENCES libraries(id) ON DELETE CASCADE
);

CREATE TABLE library_media (
    id TEXT PRIMARY KEY,
    library_id TEXT NOT NULL,
    relative_path TEXT NOT NULL,
    byte_length INTEGER NOT NULL,
    modified_at TEXT NOT NULL,
    scan_id TEXT NOT NULL,
    stable_since TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    UNIQUE (library_id, relative_path),
    FOREIGN KEY (library_id) REFERENCES libraries(id) ON DELETE CASCADE,
    FOREIGN KEY (scan_id) REFERENCES library_scans(id),
    CHECK ((byte_length >= 0))
);

CREATE TABLE library_node_accesses (
    library_id TEXT NOT NULL,
    node_id TEXT NOT NULL,
    mode TEXT NOT NULL,
    local_path TEXT,
    can_read BOOLEAN NOT NULL DEFAULT 0,
    can_write BOOLEAN NOT NULL DEFAULT 0,
    transfer_source_node_id TEXT,
    updated_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    is_enabled BOOLEAN NOT NULL DEFAULT 1,
    PRIMARY KEY (library_id, node_id),
    FOREIGN KEY (library_id) REFERENCES libraries(id) ON DELETE CASCADE,
    FOREIGN KEY (node_id) REFERENCES cluster_nodes(id) ON DELETE CASCADE,
    FOREIGN KEY (transfer_source_node_id) REFERENCES cluster_nodes(id) ON DELETE RESTRICT,
    CHECK (mode IN ('direct', 'unsupported', 'transfer')),
    CHECK (((((mode) = 'direct') AND (local_path IS NOT NULL) AND (length(trim(local_path)) > 0) AND can_read AND (transfer_source_node_id IS NULL)) OR (((mode) = 'unsupported') AND (local_path IS NULL) AND (NOT can_read) AND (NOT can_write) AND (transfer_source_node_id IS NULL)) OR (((mode) = 'transfer') AND (local_path IS NULL) AND can_read AND can_write AND (transfer_source_node_id IS NOT NULL) AND (transfer_source_node_id <> node_id)))),
    CHECK ((((mode) <> 'unsupported') OR (NOT is_enabled)))
);

CREATE TABLE library_node_processing_limits (
    library_id TEXT NOT NULL,
    node_id TEXT NOT NULL,
    max_concurrent_tasks INTEGER NOT NULL,
    PRIMARY KEY (library_id, node_id),
    FOREIGN KEY (library_id) REFERENCES libraries(id) ON DELETE CASCADE,
    FOREIGN KEY (node_id) REFERENCES cluster_nodes(id),
    CHECK (((max_concurrent_tasks >= 1) AND (max_concurrent_tasks <= 1000)))
);

CREATE TABLE library_processing_receipts (
    library_id TEXT NOT NULL,
    file_id TEXT NOT NULL,
    flow_id TEXT NOT NULL,
    flow_version INTEGER NOT NULL,
    byte_length INTEGER NOT NULL,
    modified_at TEXT NOT NULL,
    execution_id TEXT NOT NULL,
    PRIMARY KEY (library_id, file_id, flow_id, flow_version, byte_length, modified_at),
    FOREIGN KEY (library_id) REFERENCES libraries(id) ON DELETE CASCADE,
    FOREIGN KEY (file_id) REFERENCES library_media(id) ON DELETE CASCADE,
    FOREIGN KEY (execution_id) REFERENCES executions(id)
);

CREATE TABLE library_processing_settings (
    library_id TEXT PRIMARY KEY,
    watch_enabled BOOLEAN NOT NULL DEFAULT 1,
    scan_interval_seconds INTEGER NOT NULL DEFAULT 60,
    max_concurrent_tasks INTEGER,
    revision INTEGER NOT NULL DEFAULT 1,
    FOREIGN KEY (library_id) REFERENCES libraries(id) ON DELETE CASCADE,
    CHECK (((scan_interval_seconds >= 15) AND (scan_interval_seconds <= 86400))),
    CHECK (((max_concurrent_tasks >= 1) AND (max_concurrent_tasks <= 1000)))
);

CREATE TABLE library_replacement_policies (
    library_id TEXT PRIMARY KEY,
    enabled BOOLEAN NOT NULL DEFAULT 0,
    grace_period_hours INTEGER NOT NULL DEFAULT 168,
    revision INTEGER NOT NULL DEFAULT 1,
    FOREIGN KEY (library_id) REFERENCES libraries(id) ON DELETE CASCADE,
    CHECK (((grace_period_hours >= 1) AND (grace_period_hours <= 8760)))
);

CREATE TABLE library_scans (
    id TEXT PRIMARY KEY,
    library_id TEXT NOT NULL,
    media_type TEXT NOT NULL,
    state TEXT NOT NULL DEFAULT 'queued',
    node_id TEXT,
    source_root TEXT,
    epoch INTEGER NOT NULL DEFAULT 0,
    lease_until TEXT,
    created_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    completed_at TEXT,
    detail TEXT NOT NULL DEFAULT '',
    FOREIGN KEY (library_id) REFERENCES libraries(id) ON DELETE CASCADE,
    FOREIGN KEY (node_id) REFERENCES cluster_nodes(id),
    CHECK (state IN ('queued', 'running', 'completed', 'failed'))
);

CREATE TABLE media_replacement_marks (
    library_id TEXT NOT NULL,
    relative_path TEXT NOT NULL,
    byte_length INTEGER NOT NULL,
    modified_at TEXT NOT NULL,
    execution_id TEXT NOT NULL,
    PRIMARY KEY (library_id, relative_path, byte_length, modified_at),
    FOREIGN KEY (execution_id) REFERENCES executions(id),
    FOREIGN KEY (library_id) REFERENCES libraries(id) ON DELETE CASCADE
);

CREATE TABLE media_reservations (
    library_id TEXT NOT NULL,
    media_key TEXT NOT NULL,
    execution_id TEXT NOT NULL,
    lease_epoch INTEGER NOT NULL,
    lease_until TEXT NOT NULL,
    PRIMARY KEY (library_id, media_key),
    FOREIGN KEY (execution_id) REFERENCES executions(id),
    FOREIGN KEY (library_id) REFERENCES libraries(id)
);

CREATE TABLE node_enrollment_requests (
    node_id TEXT PRIMARY KEY,
    pairing_key_hash BLOB NOT NULL,
    node_token_hash BLOB NOT NULL,
    peer_id TEXT,
    peer_public_key TEXT,
    name TEXT NOT NULL,
    roles TEXT NOT NULL,
    max_concurrent_tasks INTEGER NOT NULL,
    capabilities TEXT NOT NULL DEFAULT ('[]'),
    created_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    expires_at TEXT NOT NULL,
    inventory TEXT NOT NULL DEFAULT ('{}'),
    UNIQUE (pairing_key_hash),
    CHECK (((max_concurrent_tasks >= 0) AND (max_concurrent_tasks <= 1024)))
);

-- Membership proof and a cluster-key envelope for each server API admitted to the mesh.
-- The private signing/decryption key never leaves its server; only this signed public grant is replicated.
CREATE TABLE cluster_peer_admissions (
    cluster_id TEXT NOT NULL,
    peer_id TEXT NOT NULL,
    node_id TEXT,
    public_key TEXT NOT NULL,
    approved_by_peer_id TEXT NOT NULL,
    wrapped_data_key TEXT,
    key_fingerprint TEXT NOT NULL,
    issued_at TEXT NOT NULL,
    revoked_at TEXT,
    signature TEXT NOT NULL,
    PRIMARY KEY (cluster_id, peer_id),
    UNIQUE (cluster_id, node_id)
);
CREATE INDEX ix_cluster_peer_admissions_node ON cluster_peer_admissions (cluster_id, node_id);

CREATE TABLE node_group_members (
    group_id TEXT NOT NULL,
    node_id TEXT NOT NULL,
    PRIMARY KEY (group_id, node_id),
    FOREIGN KEY (group_id) REFERENCES node_groups(id) ON DELETE CASCADE,
    FOREIGN KEY (node_id) REFERENCES cluster_nodes(id) ON DELETE CASCADE
);

CREATE TABLE node_groups (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    description TEXT NOT NULL DEFAULT '',
    mode TEXT NOT NULL,
    criteria TEXT NOT NULL DEFAULT ('{}'),
    labels TEXT NOT NULL DEFAULT ('{}'),
    created_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    updated_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    UNIQUE (name),
    CHECK (((length(trim(name)) >= 1) AND (length(trim(name)) <= 128))),
    CHECK ((length(description) <= 2000)),
    CHECK (mode IN ('manual', 'automatic'))
);

CREATE TABLE node_processing_settings (
    node_id TEXT PRIMARY KEY,
    max_concurrent_tasks INTEGER,
    revision INTEGER NOT NULL DEFAULT 1,
    schedule_enabled BOOLEAN NOT NULL DEFAULT 0,
    schedule_timezone TEXT NOT NULL DEFAULT 'UTC',
    schedule_slots TEXT NOT NULL DEFAULT (repeat('1', 672)),
    FOREIGN KEY (node_id) REFERENCES cluster_nodes(id),
    CHECK (((max_concurrent_tasks >= 1) AND (max_concurrent_tasks <= 1000)))
);

CREATE TABLE plugin_configurations (
    plugin_id TEXT PRIMARY KEY,
    revision INTEGER NOT NULL DEFAULT 1,
    encrypted_values BLOB NOT NULL,
    updated_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    CHECK ((revision > 0))
);

CREATE TABLE source_replacements (
    execution_id TEXT PRIMARY KEY,
    library_id TEXT NOT NULL,
    source_node_id TEXT NOT NULL,
    original_path TEXT NOT NULL,
    final_path TEXT,
    backup_path TEXT,
    source_sha TEXT NOT NULL,
    result_sha TEXT NOT NULL,
    state TEXT NOT NULL,
    detail TEXT,
    replaced_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    grace_until TEXT,
    finished_at TEXT,
    FOREIGN KEY (execution_id) REFERENCES executions(id) ON DELETE RESTRICT,
    FOREIGN KEY (library_id) REFERENCES libraries(id) ON DELETE RESTRICT,
    FOREIGN KEY (source_node_id) REFERENCES cluster_nodes(id),
    CHECK (state IN ('replaced', 'rollback_requested', 'rolled_back', 'finalized', 'retained', 'failed')),
    CHECK (((state = 'failed') OR ((final_path IS NOT NULL) AND (backup_path IS NOT NULL) AND (grace_until IS NOT NULL))))
);

CREATE TABLE user_sessions (
    token_hash BLOB PRIMARY KEY,
    user_id TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    last_seen_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    expires_at TEXT NOT NULL,
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE
);

CREATE TABLE users (
    id TEXT PRIMARY KEY,
    username TEXT NOT NULL,
    password_hash TEXT NOT NULL,
    role TEXT NOT NULL,
    is_active BOOLEAN NOT NULL DEFAULT 1,
    failed_attempts INTEGER NOT NULL DEFAULT 0,
    locked_until TEXT,
    created_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    updated_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    CHECK (role IN ('admin', 'viewer')),
    CHECK (((length(username) >= 1) AND (length(username) <= 64)))
);

CREATE UNIQUE INDEX ux_flow_component_packs_name ON flow_component_packs (lower(name));

CREATE INDEX ix_execution_work_due ON execution_work (available_at, lease_until);

CREATE INDEX ix_execution_waits_due ON execution_waits (next_check_at) WHERE (state = 'waiting');

CREATE INDEX ix_source_replacements_due ON source_replacements (source_node_id, state, grace_until);

CREATE INDEX ix_flow_draft_backups_due ON flow_draft_backups (next_attempt_at);

CREATE UNIQUE INDEX ux_encoding_profiles_name ON encoding_profiles (lower(draft_name));

CREATE INDEX ix_node_group_members_node ON node_group_members (node_id);

CREATE UNIQUE INDEX ux_flow_templates_name ON flow_templates (lower(name));

CREATE UNIQUE INDEX ix_integration_trigger_open ON integration_trigger_requests (library_id, upper(relative_path)) WHERE (state IN ('pending', 'processing'));

CREATE UNIQUE INDEX ux_flow_scripts_name ON flow_scripts (lower(draft_name));

CREATE UNIQUE INDEX users_username_lower_idx ON users (lower(username));

CREATE INDEX ix_flow_component_packs_source ON flow_component_packs (source_flow_id, source_version);

CREATE INDEX user_sessions_user_idx ON user_sessions (user_id);

CREATE INDEX ix_flow_backup_due ON flow_backup_jobs (next_attempt_at);

CREATE INDEX ix_integration_trigger_state ON integration_trigger_requests (state, requested_at);

CREATE INDEX ix_node_enrollment_requests_expires_at ON node_enrollment_requests (expires_at);

CREATE INDEX ix_execution_work_artifacts_pending ON execution_work (execution_id) WHERE (artifacts_purged_at IS NULL);

CREATE INDEX ix_library_node_accesses_node_id ON library_node_accesses (node_id);

CREATE INDEX ix_cluster_nodes_last_seen_at ON cluster_nodes (last_seen_at);

CREATE UNIQUE INDEX ix_library_scan_active ON library_scans (library_id) WHERE (state IN ('queued', 'running'));

CREATE INDEX user_sessions_expiry_idx ON user_sessions (expires_at);

CREATE INDEX ix_flow_templates_source ON flow_templates (source_flow_id, source_version);

CREATE INDEX ix_executions_updated ON executions (updated_at DESC, id);

CREATE INDEX ix_execution_work_handoff ON execution_work (available_at) WHERE handoff_pending;

CREATE TRIGGER flow_version_immutable_update BEFORE UPDATE ON flow_versions
BEGIN SELECT RAISE(ABORT, 'Published flow versions are immutable'); END;
CREATE TRIGGER flow_version_immutable_delete BEFORE DELETE ON flow_versions
BEGIN SELECT RAISE(ABORT, 'Published flow versions are immutable'); END;
CREATE TRIGGER flow_draft_snapshot_immutable_update BEFORE UPDATE OF definition ON flow_draft_backups
BEGIN SELECT RAISE(ABORT, 'Published flow versions are immutable'); END;
CREATE TRIGGER flow_draft_snapshot_immutable_delete BEFORE DELETE ON flow_draft_backups
BEGIN SELECT RAISE(ABORT, 'Published flow versions are immutable'); END;

-- Données initiales de 0003_node_enrollment.
INSERT INTO cluster_identity (singleton) VALUES (1);
