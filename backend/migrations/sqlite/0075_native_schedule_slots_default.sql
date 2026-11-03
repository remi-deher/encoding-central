-- Le défaut de worker_processing_settings.schedule_slots de la baseline (0033) est écrit repeat('1', 672), une fonction
-- PostgreSQL absente de SQLite. Les bases créées depuis le début l'ont gardé tel quel, alors que le SQL n'est plus traduit.
-- La table est reconstruite avec le défaut natif équivalent (672 créneaux ouverts) ; les données sont conservées.
CREATE TABLE worker_processing_settings_native (
    worker_id TEXT PRIMARY KEY,
    max_concurrent_tasks INTEGER,
    revision INTEGER NOT NULL DEFAULT 1,
    schedule_enabled BOOLEAN NOT NULL DEFAULT 0,
    schedule_timezone TEXT NOT NULL DEFAULT 'UTC',
    schedule_slots TEXT NOT NULL DEFAULT (replace(substr(hex(zeroblob(336)), 1, 672), '0', '1')),
    FOREIGN KEY (worker_id) REFERENCES "workers"(id),
    CHECK (((max_concurrent_tasks >= 1) AND (max_concurrent_tasks <= 1000)))
);
INSERT INTO worker_processing_settings_native(worker_id, max_concurrent_tasks, revision, schedule_enabled, schedule_timezone, schedule_slots)
    SELECT worker_id, max_concurrent_tasks, revision, schedule_enabled, schedule_timezone, schedule_slots FROM worker_processing_settings;
DROP TABLE worker_processing_settings;
ALTER TABLE worker_processing_settings_native RENAME TO worker_processing_settings;
