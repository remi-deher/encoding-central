-- Connexions IA répliquées entre nœuds (Codex CLI, Claude Code, clé OpenAI). La valeur est chiffrée au repos par le nœud
-- et chiffrée par la clé de données du cluster lors de la réconciliation. Une déconnexion est propagée par une valeur « supprimée ».
CREATE TABLE ai_credentials (
    provider TEXT PRIMARY KEY,
    revision INTEGER NOT NULL DEFAULT 1,
    encrypted_values BLOB NOT NULL,
    updated_at TEXT NOT NULL DEFAULT ((strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')),
    CHECK ((revision > 0))
);
