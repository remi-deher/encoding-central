-- Retrait des tables et colonnes de la réplication entre serveurs (ADR 0036). Le central est l'unique serveur :
-- journal, pairs, capture des changements, projections, transferts, neutralisation et sauvegarde miroir n'existent plus.
-- Conservés : cluster_identity, cluster_nodes, cluster_sync_registers (registres locaux), task_ownership et task_server_identity.
DROP TABLE IF EXISTS cluster_sync_dirty_entities;
DROP TABLE IF EXISTS ai_credentials;
DROP TABLE IF EXISTS cluster_change_log;
DROP TABLE IF EXISTS cluster_database_backups;
DROP TABLE IF EXISTS cluster_join_user_collisions;
DROP TABLE IF EXISTS cluster_leadership_transfers;
DROP TABLE IF EXISTS cluster_peer_admissions;
DROP TABLE IF EXISTS cluster_projection_diagnostics;
DROP TABLE IF EXISTS cluster_sync_entities;
DROP TABLE IF EXISTS cluster_sync_identity;
DROP TABLE IF EXISTS cluster_sync_peer_endpoints;
DROP TABLE IF EXISTS cluster_sync_peer_vectors;
DROP TABLE IF EXISTS cluster_sync_pending_projections;
DROP TABLE IF EXISTS cluster_sync_table_cursors;
DROP TABLE IF EXISTS node_peer_reconciliation_urls;
DROP TABLE IF EXISTS pending_source_writes;
DROP TABLE IF EXISTS task_checkpoint_cached;
DROP TABLE IF EXISTS task_checkpoint_dirty;
DROP TABLE IF EXISTS task_checkpoints;
DROP TABLE IF EXISTS task_coordination_inbox;
DROP TABLE IF EXISTS task_coordination_parked;
DROP TABLE IF EXISTS task_coordination_rejections;
DROP TABLE IF EXISTS task_observation_sequence;
DROP TABLE IF EXISTS task_ownership_events;
DROP TABLE IF EXISTS task_ownership_inbox;
DROP TABLE IF EXISTS task_peer_observations;
DROP TABLE IF EXISTS task_transfer_aborts;
DROP TABLE IF EXISTS task_neutralizations;
DROP TABLE IF EXISTS task_quarantines;
DROP TABLE IF EXISTS task_transfers;
DROP TABLE IF EXISTS cluster_sync_capture_tables;
ALTER TABLE cluster_nodes DROP COLUMN desired_roles;
ALTER TABLE node_enrollment_requests DROP COLUMN peer_id;
ALTER TABLE node_enrollment_requests DROP COLUMN peer_public_key;
