-- Les points de reprise IA ne sont plus chiffrés avec une clé de données propre au central mais avec le trousseau
-- Data Protection (purpose Encoding.Ai.v2). Les anciennes valeurs, illisibles avec ce trousseau, sont supprimées :
-- sessions et discussions de l'assistant, réglages IA partagés et politique des modèles. Les clés d'API restent
-- protégées séparément par les réglages locaux ; l'activation des plugins n'est pas touchée.
DELETE FROM cluster_sync_registers WHERE register_key LIKE 'ai/%';
