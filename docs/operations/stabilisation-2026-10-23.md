# Stabilisation du 2026-10-23 : bilan et reste à faire

Incident : lenteur générale et page Activité en « Actualisation impossible » sur le cluster de production (.51, avec .50), après l'affectation du flow v6 à 9 bibliothèques (file de plus de 7 000 exécutions).
Ce document sépare ce qui est démontré, ce qui est livré dans le code mais pas encore validé en production, et ce qui reste à faire. Aucune mesure en production n'est revendiquée au-delà de celles datées ci-dessous.

## Causes établies (mesures en lecture seule sur .51)

| Cause | Preuve | Correctif livré |
| --- | --- | --- |
| Les transactions SQLite s'ouvraient toutes en écriture, même pour une lecture ; l'aperçu Activité tenait le verrou unique | requêtes bloquées 30 s (heartbeat, claim, `/cluster`, `/executions`), erreurs `database is locked` | #351 (lecture `Snapshot`), #356 à #361 (file d'écriture, présence prioritaire, capture bornée) |
| Un heartbeat retardé faisait paraître un nœud hors ligne ; 1 057 exécutions saines échouées « aucun nœud source direct disponible » | paquets de 100 échecs à 14:24 et 14:36 UTC, accès directs pourtant corrects | #353 (grâce de 5 min, pré-contrôle en lecture) |
| Disque plein : pool `SSD` à 97 %, 692 Mo libres, attente disque 79 %, pression d'E/S 90 % | `zpool list`, `/proc/pressure/io`, `top` à 18:49 locale | nettoyage manuel de 373 dossiers (95 Go) avec accord de Rémi ; #363 pour que ça ne se reproduise pas |
| Rien ne supprimait `executions/` ni `transfers/`, et la rétention des artefacts était désactivée | lecture du code, 97 Go dans `encoding` | #363 (nettoyage par nœud, rétention activée par défaut) |
| `cluster.db` sur un jeu de données ZFS en `recordsize` 128K | `zfs get recordsize` | **non traité** (voir ci-dessous) |

## Livré dans `dev`

#351, #353, #355 (ADR 0009), #356 à #361, #363, #365 (tests de charge). Leur état de déploiement en production est à confirmer : au 2026-10-23 18:50 locale, l'image tournant sur .51 datait de 17:02 locale ; je n'ai pas vérifié qu'elle contient #353, #356 et la suite, ni #363. Le conteneur `encoding` de .51 a été arrêté à la demande de Rémi et n'a pas été redémarré par ce travail.

## Validé

- Compilation et tests ciblés en local (SDK .NET 10) : `WorkspaceCleanupTests`, `LoadContentionTests`, scénario de rétention.
- #365 : les tests rapides échouent quand on retire respectivement la lecture `Snapshot` et la grâce de 5 min.
- Charge lourde jouée une fois en local avec 6 000 exécutions (passée).

## Non validé

- Aucune mesure avant/après en production d'un correctif de latence. Les mesures de 18:49 locale ont été prises avec le disque saturé, donc elles ne jugent pas les correctifs.
- Le workflow `stress.yml` n'a pas encore tourné sur GitHub.
- Le nettoyage des dossiers de travail sur un vrai nœud (stockage réseau compris), et son comportement quand le serveur ancien ne connaît pas `execution-states` (404, aucune suppression).
- Versions mixtes de nœuds : seule la version de protocole est contrôlée (voir `open-questions.md` n° 9).
- Mesures sur .50 : ses journaux (erreurs de jeton et de heartbeat signalées) n'ont pas été lus.

## Reste à faire

### Sur la production (actions humaines ou à autoriser une par une)

1. Déployer la même image `dev` sur .50 **et** .51, puis redémarrer `encoding` sur .51 (arrêté). Vérifier au démarrage que la rétention n'est pas désactivée.
2. Régler la rétention côté serveur à la journée la plus courte : `Operations__ArtifactRetention__Days=1` et `FailedDays=1` (la plus petite unité est le jour). Le nettoyage des nœuds a ses défauts de 1 h / 24 h.
3. Libérer durablement le pool `SSD` (97 % plein). Les 95 Go supprimés se libèrent en différé : re-mesurer. Garder un seuil d'alerte (le seuil `Operations:MinFreeDiskBytes` existe à 5 Go ; vérifier qu'il alerte avant 90 % du pool).
4. Déplacer `cluster.db` sur un jeu de données ZFS dédié en `recordsize` 16K, conteneur arrêté, avec sauvegarde préalable (procédure : `backup-restore.md`).
5. Relancer en bloc les exécutions échouées à tort (« aucun nœud source direct disponible ») une fois le cluster stable. Aucun outil de relance en masse n'existe : relance une par une via l'API, ou à écrire.
6. Décider de la file de ~6 800 exécutions : désactiver temporairement les deux grosses bibliothèques, ou limiter la concurrence pour ne pas saturer le disque. FileFlows consommait 345 % de CPU sur la même machine.
7. Relire les journaux de .50 (jetons, heartbeats).
8. Mesurer en production, avec accord, les métriques `encoding_sqlite_writer_*` avant/après, et ajuster `Database:Writer:*` (capacité, délais) d'après les observations.

### Dans le code

1. Regroupement de petites écritures dans une transaction (ADR 0009 le prévoit ; la file n'en fait pas encore) et migration des transactions restantes : environ 34 fichiers utilisent encore `BeginTransactionAsync`.
2. Règle vérifiable : interdire les écritures directes hors file (test d'architecture avec liste blanche explicite).
3. Alléger l'aperçu Activité : ne plus charger le checkpoint JSON complet de chaque ligne, index adaptés au tri et au décompte par état.
4. Rétention et purge : granularité inférieure au jour pour les artefacts du serveur ; purge ou archivage des exécutions et événements anciens ; `journal_size_limit` et point de contrôle WAL régulier (seule la sauvegarde exécute un `wal_checkpoint`).
5. Compatibilité de versions : plage de schéma, ordre serveurs avant workers, test d'une image N et N-1 dans le même maillage.
6. Récupération : décider si une exécution attend le retour du nœud d'accès au lieu d'échouer (ADR 0004, questions restantes).
7. Intégration de Raft à la file locale (le mode Raft désactive la file).
8. Charge multi-nœuds réelle : étendre `stress.yml` (perte d'un nœud en plein claim, redémarrage d'un serveur sous charge, disque presque plein) ; `tests/cluster-3-nodes` existe déjà.
9. Garde-fou d'espace disque : proposé puis refusé par Rémi ; à ne pas implémenter sans nouvelle demande.

### Validation de production restant de la mission

Intégrité et restauration des fichiers sources, reprise après perte d'un serveur ou d'un worker, prévention des traitements concurrents, sécurité des plugins et commandes externes, cycle complet encodage et publication, stockages réseau (NFS : atomicité du renommage et des verrous non vérifiée), surveillance et récupération après incident. Ces points figurent déjà dans `open-questions.md` ; ce document n'en prouve aucun.

## Risques résiduels

- Le disque reste le premier facteur de lenteur si le pool se remplit de nouveau.
- Une file d'écriture unique peut devenir un goulot si une unité de travail est longue : à surveiller par les métriques de détention.
- Les gains de #351 à #361 ne sont démontrés qu'en conditions synthétiques.
