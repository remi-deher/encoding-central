-- État Store global du central (ADR 0046, T1) : une seule ligne, la révision monotone de l'ensemble des paquets (plugins et outils)
-- installés sur le central. Elle est incrémentée chaque fois que cet ensemble change (installation, mise à jour, retrait).
-- state_digest est l'empreinte SHA-256 de l'ensemble installé (vide pour aucun paquet) : elle rend l'incrément idempotent
-- (rejouer le même ensemble ne change pas la révision) et détecte au démarrage un index de paquets modifié hors du central.
-- Les colonnes de conformité par worker (ADR 0046, point 16) ne sont pas dans cette migration : elles relèvent du ticket T3.
CREATE TABLE central_store_state (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision >= 0),
    state_digest TEXT NOT NULL DEFAULT '',
    updated_at TEXT NOT NULL
);
INSERT INTO central_store_state(id, revision, state_digest, updated_at) VALUES (1, 0, '', (strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z'));
