# Accès SQLite du runtime : plans avant/après

La migration additive `0048_runtime_lookup_indexes.sql` ajoute trois index pour les requêtes existantes. Aucun format de données ni ordre d’attribution n’est modifié. Les tests `RuntimeQueryPlanTests` comparent les plans avec et sans les index sur des bases temporaires utilisant le fournisseur SQLite réel du backend.

| Accès | Avant | Après |
| --- | --- | --- |
| Charge active d’un worker (`worker_node_id`, plage de `lease_until`) | `SCAN w` | `SEARCH w USING COVERING INDEX ix_execution_work_worker_lease` |
| Charge active d’une bibliothèque (`library_id`, plage de `lease_until`) | `SCAN w` | `SEARCH w USING COVERING INDEX ix_execution_work_library_lease` |
| Dernier checkpoint d’une exécution | Recherche par exécution, puis `USE TEMP B-TREE FOR ORDER BY` | `SEARCH execution_steps USING INDEX ix_execution_steps_latest_checkpoint`, sans tri temporaire |
| Doublon actif par bibliothèque et média (migration 0050) | Parcours des travaux de la bibliothèque via `ix_execution_work_library_lease`, filtrage du média | `SEARCH w USING COVERING INDEX ix_execution_work_library_media (library_id=? AND media_key=?)` |

Les deux premiers index incluent les identifiants nécessaires à la jointure et au comptage par worker ; le troisième ne contient que les étapes terminées. Les index existants de sondage des attentes et des sauvegardes sont conservés.

L'index de doublons est construit au démarrage par une nouvelle migration ; aucun fichier de migration appliqué n'est modifié. Il est entretenu lors des insertions/suppressions de travaux, mais pas lors du renouvellement de `lease_until`. Les autres index de la migration 0048 restent utiles.

Ces observations démontrent la suppression de scans et de tris sur ces accès, pas un gain chiffré sous charge réelle. Les index occupent du disque et les deux index de baux sont entretenus lors des renouvellements : comparer aussi le débit d’écriture et l’attente de la file sur un volume représentatif avant de conclure sur le gain global. Leur construction se fait à la migration au démarrage, avant l’activité normale du nœud. Aucune validation de production n’a été exécutée.
