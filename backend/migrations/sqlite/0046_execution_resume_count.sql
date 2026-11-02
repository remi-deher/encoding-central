-- Reprises automatiques au dernier checkpoint après perte du worker (ADR 0004) : bornées par exécution.
ALTER TABLE execution_work ADD COLUMN resume_count INTEGER NOT NULL DEFAULT 0;
