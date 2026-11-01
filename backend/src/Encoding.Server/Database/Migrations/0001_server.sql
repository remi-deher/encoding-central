-- Autorité unique. Une ligne dans server, des workers enregistrés auprès d'elle.
-- Aucune table de cluster, de pair ou de bascule.

CREATE TABLE schema_migrations (
  id INTEGER PRIMARY KEY,
  name TEXT NOT NULL,
  applied_at TEXT NOT NULL
);

CREATE TABLE server (
  id TEXT PRIMARY KEY,
  display_name TEXT NOT NULL,
  public_base_url TEXT NOT NULL,
  created_at INTEGER NOT NULL
);

CREATE TRIGGER server_is_singleton
BEFORE INSERT ON server
WHEN (SELECT COUNT(*) FROM server) >= 1
BEGIN
  SELECT RAISE(ABORT, 'un seul serveur');
END;

CREATE TABLE users (
  id TEXT PRIMARY KEY,
  username TEXT NOT NULL COLLATE NOCASE UNIQUE,
  password_hash TEXT NOT NULL,
  role TEXT NOT NULL CHECK (role = 'admin'),
  created_at INTEGER NOT NULL
);

CREATE TABLE sessions (
  token_hash TEXT PRIMARY KEY,
  user_id TEXT NOT NULL REFERENCES users (id),
  expires_at INTEGER NOT NULL,
  created_at INTEGER NOT NULL
);

-- Registre de l'autorité. Les lignes arriveront avec le principe de worker.
-- Le jeton n'est jamais relu en clair : seule son empreinte est conservée.
CREATE TABLE workers (
  id TEXT PRIMARY KEY,
  display_name TEXT NOT NULL,
  token_hash TEXT NOT NULL UNIQUE,
  created_at INTEGER NOT NULL
);
