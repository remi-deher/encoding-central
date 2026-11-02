-- Local scheduling state; execution_work is never reconciled between servers.
ALTER TABLE execution_work ADD COLUMN source_preparation_requested INTEGER NOT NULL DEFAULT 1;
