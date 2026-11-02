-- Publication comme étape de flow (ADR 0003) : lorsque le flow publie par un bloc, l'exécution reste active après la confirmation
-- du nœud d'accès et le worker reprend le flow sur le fichier publié.
ALTER TABLE execution_work ADD COLUMN continue_after_publication BOOLEAN NOT NULL DEFAULT 0;
