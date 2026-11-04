-- Etat Store par worker (issue #17, P0) : l'état désiré est écrit par le central (administrateur), l'état appliqué est rapporté
-- par le worker lui-même. Les deux révisions sont monotones et indépendantes. Une ligne n'existe qu'après la première écriture :
-- un worker sans ligne a un état désiré vide (révision 0), donc aucun outil ni plugin activé par défaut.
CREATE TABLE worker_store_state (
    worker_id TEXT PRIMARY KEY,
    desired_revision INTEGER NOT NULL DEFAULT 0,
    desired_items TEXT NOT NULL DEFAULT '[]',
    desired_updated_at TEXT,
    applied_revision INTEGER NOT NULL DEFAULT 0,
    applied_items TEXT NOT NULL DEFAULT '[]',
    applied_reported_at TEXT,
    FOREIGN KEY (worker_id) REFERENCES "workers"(id) ON DELETE CASCADE,
    CHECK (desired_revision >= 0 AND applied_revision >= 0 AND applied_revision <= desired_revision)
);
