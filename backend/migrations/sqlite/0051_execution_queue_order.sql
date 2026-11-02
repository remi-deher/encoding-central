-- Stable keyset pagination of the durable queue, without re-sorting its history per page.
CREATE INDEX ix_executions_queue_order ON executions(queued_at, id);
