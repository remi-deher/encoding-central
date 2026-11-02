-- Ressources de flow (ADR 0006) : métadonnées réconciliées, octets dans le magasin adressé par empreinte de chaque serveur.
CREATE TABLE flow_resources (
    id TEXT PRIMARY KEY,
    kind TEXT NOT NULL,
    name TEXT NOT NULL,
    description TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    updated_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    CHECK (kind IN ('font')),
    CHECK (((length(name) >= 1) AND (length(name) <= 128))),
    CHECK ((length(description) <= 1000))
);

CREATE UNIQUE INDEX ux_flow_resources_name ON flow_resources (lower(name));

CREATE TABLE flow_resource_versions (
    resource_id TEXT NOT NULL,
    version INTEGER NOT NULL,
    files TEXT NOT NULL,
    total_bytes INTEGER NOT NULL,
    created_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    PRIMARY KEY (resource_id, version),
    FOREIGN KEY (resource_id) REFERENCES flow_resources(id) ON DELETE RESTRICT,
    CHECK ((version > 0)),
    CHECK ((total_bytes >= 0))
);
