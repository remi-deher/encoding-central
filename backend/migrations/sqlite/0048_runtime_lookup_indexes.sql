-- Lookup paths used by assignment, wait conditions and recovery. Existing data and contracts are unchanged.
CREATE INDEX ix_execution_work_worker_lease
    ON execution_work(worker_node_id, lease_until, execution_id);

CREATE INDEX ix_execution_work_library_lease
    ON execution_work(library_id, lease_until, worker_node_id, execution_id);

CREATE INDEX ix_execution_steps_latest_checkpoint
    ON execution_steps(execution_id, completed_revision DESC)
    WHERE completed_at IS NOT NULL;
