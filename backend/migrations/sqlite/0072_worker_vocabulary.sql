-- Vocabulaire central / worker dans le schéma (ADR 0040). Les index gardent leurs noms ; SQLite met à jour les
-- références (clés étrangères, index, contraintes) lors des renommages. Les colonnes node_id de execution_steps et
-- execution_waits désignent un bloc de flow (pas un worker) et deviennent flow_node_id.
ALTER TABLE cluster_nodes RENAME TO workers;
ALTER TABLE cluster_identity RENAME TO central_identity;
ALTER TABLE node_groups RENAME TO worker_groups;
ALTER TABLE node_group_members RENAME TO worker_group_members;
ALTER TABLE node_enrollment_requests RENAME TO worker_enrollment_requests;
ALTER TABLE node_processing_settings RENAME TO worker_processing_settings;
ALTER TABLE library_node_accesses RENAME TO library_worker_accesses;
ALTER TABLE library_node_processing_limits RENAME TO library_worker_processing_limits;
ALTER TABLE node_directory_browse_requests RENAME TO worker_directory_browse_requests;

ALTER TABLE workers RENAME COLUMN node_token_hash TO worker_token_hash;
ALTER TABLE executions RENAME COLUMN node_id TO worker_id;
ALTER TABLE execution_steps RENAME COLUMN node_id TO flow_node_id;
ALTER TABLE execution_steps RENAME COLUMN completed_worker_node_id TO completed_worker_id;
ALTER TABLE execution_waits RENAME COLUMN node_id TO flow_node_id;
ALTER TABLE execution_work RENAME COLUMN source_node_id TO source_worker_id;
ALTER TABLE execution_work RENAME COLUMN worker_node_id TO worker_id;
ALTER TABLE execution_work RENAME COLUMN previous_worker_node_id TO previous_worker_id;
ALTER TABLE library_worker_accesses RENAME COLUMN node_id TO worker_id;
ALTER TABLE library_worker_accesses RENAME COLUMN transfer_source_node_id TO transfer_source_worker_id;
ALTER TABLE library_worker_processing_limits RENAME COLUMN node_id TO worker_id;
ALTER TABLE library_scans RENAME COLUMN node_id TO worker_id;
ALTER TABLE worker_directory_browse_requests RENAME COLUMN node_id TO worker_id;
ALTER TABLE worker_enrollment_requests RENAME COLUMN node_id TO worker_id;
ALTER TABLE worker_enrollment_requests RENAME COLUMN node_token_hash TO worker_token_hash;
ALTER TABLE worker_group_members RENAME COLUMN node_id TO worker_id;
ALTER TABLE worker_processing_settings RENAME COLUMN node_id TO worker_id;
ALTER TABLE source_replacements RENAME COLUMN source_node_id TO source_worker_id;
ALTER TABLE system_logs RENAME COLUMN node_id TO worker_id;
ALTER TABLE system_logs RENAME COLUMN node_name TO worker_name;
