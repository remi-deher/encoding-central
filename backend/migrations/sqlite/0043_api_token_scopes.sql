-- Portées d'écriture des jetons API (MCP et opérations métier) : liste séparée par des virgules, NULL = lecture seule pour ces surfaces.
ALTER TABLE api_tokens ADD COLUMN scopes TEXT;
