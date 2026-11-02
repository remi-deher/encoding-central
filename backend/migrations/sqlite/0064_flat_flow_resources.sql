-- Une ressource du catalogue est un fichier. Les paquets historiques (plusieurs fichiers dans une
-- même version) sont recopiés : un fichier, une ressource, une version par contenu successif.
-- Le paquet d'origine reste, car les flows déjà publiés sont immuables et le référencent encore.
-- Les identifiants sont dérivés du paquet et du nom de fichier, pour que chaque nœud produise les mêmes lignes.

CREATE TEMP TABLE resource_flat_files (
    id TEXT NOT NULL,
    kind TEXT NOT NULL,
    file_name TEXT NOT NULL,
    sha TEXT NOT NULL,
    bytes INTEGER NOT NULL,
    origin_name TEXT NOT NULL,
    origin_version INTEGER NOT NULL,
    version_at TEXT NOT NULL
);

WITH RECURSIVE expanded AS (
    SELECT
        r.id AS origin_id,
        r.kind,
        r.name AS origin_name,
        v.version AS origin_version,
        v.created_at AS version_at,
        COALESCE(json_extract(j.value, '$.name'), json_extract(j.value, '$.Name')) AS file_name,
        lower(COALESCE(json_extract(j.value, '$.sha256'), json_extract(j.value, '$.Sha256'))) AS sha,
        CAST(COALESCE(json_extract(j.value, '$.bytes'), json_extract(j.value, '$.Bytes')) AS INTEGER) AS bytes
    FROM flow_resources r
    JOIN flow_resource_versions v ON v.resource_id = r.id
    JOIN json_each(v.files) j
    WHERE json_array_length(v.files) > 1
),
names AS (
    SELECT DISTINCT origin_id, file_name FROM expanded WHERE file_name IS NOT NULL AND length(file_name) BETWEEN 1 AND 128
),
hash_step AS (
    SELECT origin_id, file_name, 1 AS i, 5381 AS h FROM names
    UNION ALL
    SELECT origin_id, file_name, i + 1, ((h * 33) + unicode(substr(file_name, i, 1))) & 4294967295
    FROM hash_step
    WHERE i <= length(file_name)
),
file_ids AS (
    SELECT
        origin_id,
        file_name,
        lower(
            substr(replace(origin_id, '-', ''), 1, 8) || '-' ||
            substr(replace(origin_id, '-', ''), 9, 4) || '-' ||
            substr(replace(origin_id, '-', ''), 13, 4) || '-' ||
            substr(printf('%08x', h), 1, 4) || '-' ||
            substr(printf('%012x', h), 1, 12)
        ) AS id
    FROM hash_step
    WHERE i = length(file_name) + 1
)
INSERT INTO resource_flat_files(id, kind, file_name, sha, bytes, origin_name, origin_version, version_at)
SELECT f.id, e.kind, e.file_name, e.sha, e.bytes, e.origin_name, e.origin_version, e.version_at
FROM expanded e
JOIN file_ids f ON f.origin_id = e.origin_id AND f.file_name = e.file_name
WHERE length(e.sha) = 64 AND e.bytes >= 0;

INSERT INTO flow_resources(id, kind, name, description, created_at, updated_at)
SELECT
    ranked.id,
    ranked.kind,
    CASE
        WHEN ranked.name_rank > 1 OR EXISTS (
            SELECT 1 FROM flow_resources existing
            WHERE lower(existing.name) = lower(ranked.file_name) AND existing.id <> ranked.id
        )
        THEN substr(ranked.file_name, 1, 117) || ' · ' || substr(ranked.id, 1, 8)
        ELSE ranked.file_name
    END,
    substr('Fichier extrait de « ' || ranked.origin_name || ' ».', 1, 1000),
    ranked.created_at,
    ranked.updated_at
FROM (
    SELECT
        id,
        kind,
        file_name,
        origin_name,
        min(version_at) AS created_at,
        max(version_at) AS updated_at,
        row_number() OVER (PARTITION BY lower(file_name) ORDER BY id) AS name_rank
    FROM (
        SELECT DISTINCT id, kind, file_name, origin_name, version_at FROM resource_flat_files
    )
    GROUP BY id
) ranked
WHERE NOT EXISTS (SELECT 1 FROM flow_resources existing WHERE existing.id = ranked.id);

INSERT INTO flow_resource_versions(resource_id, version, files, total_bytes, created_at)
SELECT
    numbered.id,
    numbered.version,
    json_array(json_object('name', numbered.file_name, 'sha256', numbered.sha, 'bytes', numbered.bytes)),
    numbered.bytes,
    numbered.version_at
FROM (
    SELECT
        id,
        file_name,
        sha,
        bytes,
        version_at,
        row_number() OVER (PARTITION BY id ORDER BY origin_version, sha) AS version
    FROM (
        SELECT id, file_name, sha, bytes, min(origin_version) AS origin_version, min(version_at) AS version_at
        FROM resource_flat_files
        GROUP BY id, file_name, sha, bytes
    )
) numbered
WHERE NOT EXISTS (
    SELECT 1 FROM flow_resource_versions existing
    WHERE existing.resource_id = numbered.id AND existing.version = numbered.version
);

DROP TABLE resource_flat_files;
