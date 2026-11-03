-- Fin de la propriété des tâches entre serveurs (ADR 0036) : le central est l'unique serveur, donc l'unique propriétaire.
-- Le fil des tâches repose sur executions et execution_work, qui portent déjà la bibliothèque et la clé du média.
DROP TABLE IF EXISTS task_ownership;
DROP TABLE IF EXISTS task_server_identity;
