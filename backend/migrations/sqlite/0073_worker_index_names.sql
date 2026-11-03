-- Noms d'index en vocabulaire worker (les définitions ne changent pas).
DROP INDEX IF EXISTS ix_node_group_members_node;
CREATE INDEX ix_worker_group_members_worker ON worker_group_members (worker_id);
DROP INDEX IF EXISTS ix_node_enrollment_requests_expires_at;
CREATE INDEX ix_worker_enrollment_requests_expires_at ON worker_enrollment_requests (expires_at);
DROP INDEX IF EXISTS ix_library_node_accesses_node_id;
CREATE INDEX ix_library_worker_accesses_worker_id ON library_worker_accesses (worker_id);
DROP INDEX IF EXISTS ix_cluster_nodes_last_seen_at;
CREATE INDEX ix_workers_last_seen_at ON workers (last_seen_at);
DROP INDEX IF EXISTS ix_node_directory_browse_queue;
CREATE INDEX ix_worker_directory_browse_queue ON worker_directory_browse_requests (worker_id, state, created_at);
DROP INDEX IF EXISTS ix_node_directory_browse_expiry;
CREATE INDEX ix_worker_directory_browse_expiry ON worker_directory_browse_requests (expires_at);
