# Incident du 27 octobre 2026 : tests Overgeared bloqués par la réconciliation

## Contexte et observations

Deux installations TrueNAS (`192.168.1.50` et `192.168.1.51`), chacune avec sa base SQLite locale, appartiennent au cluster Maison. Le cluster a été créé sur `.51`, puis `.50` l'a rejoint. Les deux conteneurs utilisent la même image Docker.

Les cinq fichiers Overgeared de « Bibliothèque test » étaient détectés. Dix exécutions, cinq créées sur chaque serveur, restaient en file sur le flow v4. Les demandes concurrentes du même média avaient été départagées : cinq étaient actives du point de vue de la propriété, cinq en quarantaine. Aucun encodage n'avait démarré.

Le moteur d'attribution avait `task_server_identity.ready=0`. `TaskOwnershipRepository.PrepareClaimsAsync` attend notamment que les projections soient appliquées et que le rattrapage des pairs soit confirmé. Il ne s'agissait donc pas seulement d'un problème de capacités GPU.

## Cause 1 : deux comptes initiaux portent le nom admin

Les deux installations ont créé indépendamment un compte `admin` le 24 octobre, avant leur convergence dans le cluster :

| Origine | Identifiant du compte | Nom initial |
| --- | --- | --- |
| `.51`, créateur du cluster | `d8fff0a0-f829-40bc-a60b-f482b33e20b0` | `admin` |
| `.50`, nœud rattaché | `dfcf0e3a-47a2-4bba-9821-9ca2785b92a5` | `admin` |

La réplication identifie un utilisateur par son UUID. L'index local `users_username_lower_idx` impose cependant l'unicité du nom de connexion sans tenir compte de la casse. L'insertion de l'autre UUID sous le même nom échouait donc sur chaque serveur. Plusieurs événements historiques pouvaient rester en attente pour cette même collision.

Il ne faut pas fusionner ces identités sur leur seul nom : les mots de passe, les sessions et les droits appartiennent à des comptes distincts.

## Cause 2 : rejeu d'instantanés de brouillon déjà matérialisés

Sur `.51`, les sauvegardes du flow `cd036291-3a04-43b2-9c15-f79e2eac6c65`, révisions de brouillon 1 et 2, existaient déjà avec un contenu JSON sémantiquement identique à celui des événements entrants :

- `0bd28f2a-e4cc-4b7b-83c5-115ca30f609c` : révision 1 ;
- `9321f183-fc9e-4537-9c6b-aa98bcea9c13` : révision 2.

`EnsureFlowBackupProjectionAsync` peut créer la sauvegarde lors de la projection de `flow_drafts`. La projection ultérieure de l'entité `flow_draft_backups` retrouve donc une ligne déjà présente.

Dans le code observé sur `origin/main`, `ClusterEntityReconciliation.Tables` ne déclare pas `flow_draft_backups` comme immuable, contrairement aux versions publiées. L'upsert générique génère alors `DO UPDATE SET definition=...`. Le trigger `flow_draft_snapshot_immutable_update` refuse toute instruction modifiant cette colonne, même si la définition ne change pas. L'événement reste en attente et est retenté.

`ApplyOneAsync` intercepte les erreurs de base et les journalise au niveau Debug. L'état visible du cluster semblait sain alors que ces conflits permanents empêchaient l'ouverture de la file.

## Cause 3 : découverte réseau non réciproque

Après résolution des événements, `.51` ne connaissait toujours pas d'adresse de réconciliation utilisable pour confirmer directement le rattrapage de `.50`. Il ne répertoriait que `http://192.168.1.51:8090/`. Les variables d'adresse annoncée et de pairs explicites étaient absentes des deux déploiements.

La réception de changements venant d'un pair ne suffisait pas à satisfaire la confirmation de rattrapage sortante attendue par la garde d'attribution.

## Réparation de production effectuée

Le propriétaire a explicitement choisi de conserver le compte `admin` de `.51` et de renommer celui de `.50` en `admin-50`.

1. Désactivation temporaire de la bibliothèque test par l'API, pour éviter de démarrer les anciennes demandes pendant la réparation.
2. Arrêt et démarrage de chaque application concernée par le middleware TrueNAS, sans modifier l'image Docker.
3. Sauvegarde SQLite cohérente avant chaque intervention, dans un dossier accessible à root uniquement.
4. Sur `.50`, renommage du seul compte `dfcf0e3a-47a2-4bba-9821-9ca2785b92a5` en `admin-50`, avec actualisation de `updated_at`. Son UUID, son mot de passe, ses droits et ses références de session ont été conservés. La capture normale a ensuite diffusé le changement.
5. Sur `.51`, vérification de l'égalité des deux instantanés avec leurs événements. Acquittement transactionnel de leur projection dans `cluster_sync_entities` et retrait de leurs seuls marqueurs en attente. Les définitions, les statuts locaux des sauvegardes et le journal des événements ont été conservés. Aucun compte ni événement utilisateur n'a été supprimé pour contourner la collision.
6. Configuration réseau réciproque par TrueNAS :

| Hôte | `Cluster__Reconciliation__AdvertisedApiBaseUrl` | `Cluster__Reconciliation__Peers__0` |
| --- | --- | --- |
| `.50` | `http://192.168.1.50:8090/` | `http://192.168.1.51:8090/` |
| `.51` | `http://192.168.1.51:8090/` | `http://192.168.1.50:8090/` |

Les vérifications HTTP entre pairs ont réussi. Les deux bases ont passé `PRAGMA integrity_check`. Aucun indicateur `ready` n'a été forcé : le moteur l'a remis lui-même à 1 après réconciliation.

Sauvegardes conservées sur les hôtes respectifs, avec permissions root uniquement :

- `.50` : `/mnt/DATA/ARR/ENCODING/repair-backups/cluster-before-admin-rename-20261027T145113Z.db` ;
- `.51` : `/mnt/SSD/ARR/ENCODING/repair-backups/cluster-before-snapshot-ack-20261027T145203Z.db`.

Il s'agit d'une réparation ponctuelle des données et de la configuration de production. Le code applicatif n'a pas été corrigé ni déployé dans cette intervention.

## Corrections à prévoir dans le code

- **Sauvegardes immuables** : rendre le rejeu d'une sauvegarde identique idempotent, sans UPDATE de sa définition ni réinitialisation de son état local. Examiner `immutable: true` dans la déclaration de `flow_draft_backups` et traiter explicitement un contenu différent pour une même clé, sans écrasement silencieux.
- **Rattachement et comptes** : définir une résolution explicite des collisions de noms lors du rattachement d'un nœud déjà initialisé. Conserver les UUID, mots de passe et références de session. Ne jamais fusionner automatiquement des comptes homonymes. Le cas présent conserve `admin` du cluster d'accueil et renomme le compte du nœud rattaché après décision de l'opérateur.
- **Diagnostic** : exposer les projections en échec avec leur table, leur ancienneté et une cause structurée sans secret. Distinguer les dépendances temporairement absentes des conflits permanents ; éviter une boucle Debug silencieuse. Afficher pourquoi la garde d'attribution reste fermée.
- **Réconciliation réseau** : assurer le partage réciproque d'adresses de pairs authentifiés et vérifier leur accessibilité au rattachement. Ignorer sa propre adresse comme destination de réconciliation. Une réception de changements ne doit pas faire croire qu'un rattrapage bidirectionnel a été confirmé.
- **Protections** : conserver la garde d'attribution, les preuves de propriété et la quarantaine des demandes concurrentes. Ne pas débloquer les tâches en supprimant arbitrairement les événements ou en forçant `ready=1`.

Tests de régression à prévoir : rejeu d'un instantané déjà matérialisé, conservation de son statut local, définition divergente pour la même révision, comptes `admin` distincts et collision de casse, résolution explicite sans fusion de credentials, véritable dépendance manquante toujours bloquante, rattachement avec découverte partielle et rétablissement de la file après convergence.

## Vérification des tests Overgeared

Après réparation, les deux nœuds annonçaient `ready=1` et zéro projection en attente. Ils voyaient les deux comptes `admin` et `admin-50` avec les mêmes UUID sur chaque hôte.

Les dix anciennes demandes v4 ont été annulées via l'API avant démarrage. La bibliothèque a été réactivée et cinq demandes v5 ont été créées en une seule sélection (`queued=5`, `skipped=0`).

Le passage par « Préparer sur le nœud en accès direct » a été journalisé : l'attribution et le démarrage ont repris. À l'observation suivante, l'épisode 1 a toutefois échoué à 15:54:50 (Europe/Paris), l'épisode 2 était en cours et les trois autres en file. Le journal de l'échec indique qu'aucun remplacement de source n'a été effectué. Le runner de `.51` signale une `IOException` ; celui de `.50` signale une complétion refusée par le cluster. Ces messages ne suffisent pas à identifier la cause de ce nouvel échec.

L'API `GET /api/v1/executions/a42305f7-7824-41fc-b0e1-59aac55cbb77/pipeline` renvoie également HTTP 500. L'encodage H.265 complet, les transferts et la publication réussie des cinq sorties ne sont donc pas validés. Ce nouvel incident d'exécution reste à diagnostiquer ; il ne faut pas présenter la réparation des projections comme une validation complète du traitement média.

## Complément : droits effectifs des conteneurs et accès par bibliothèque

Le test v6 de l'épisode 1 (`4f729894-3365-4fca-981e-84d7bb9a0b54`) a ensuite atteint l'assemblage et la validation, puis échoué à la publication avec `UnauthorizedAccessException`. Le remux a duré environ 14 secondes. Le décodage intégral de validation a duré environ 3 min 52 s et a terminé avec le code 0 ; l'échec est postérieur à ce contrôle.

Sur `.50`, l'utilisateur du conteneur est UID/GID `1654`, avec les groupes supplémentaires GPU `44` et `107`. Le dossier Overgeared appartient à `568:568`, mode `2775`. Le processus pouvait lire, mais `os.access(..., os.W_OK)` était faux. Les chemins sous `/usb*/MEDIA` de `.51` appartiennent eux aussi à `568:568` avec écriture autorisée au groupe.

Correction demandée : ajouter le groupe supplémentaire `568` aux deux services Encoding par TrueNAS, en conservant l'utilisateur `1654` et les groupes GPU. Ne pas remplacer les ACL des médias, ne pas exécuter le conteneur en root. Les racines `/usb*` appartiennent à root et restent non inscriptibles pour cet utilisateur ; les dossiers de bibliothèques sont sous `/usb*/MEDIA`.

Cette correction a été appliquée aux deux déploiements. Le processus conserve UID/GID `1654` et annonce les groupes `44`, `107`, `568`, `1654`. Un contrôle de création, écriture, relecture, renommage et nettoyage de fichiers temporaires dans un sous-dossier `.encoding-access-check-*` a réussi sous cette identité pour Overgeared, `/media2/SERIES` et les quatre `/usb*/MEDIA/SERIES`. Aucun fichier média ni ACL existante n'a été modifié par ce contrôle. Cela vérifie les droits d'accès ; cela ne vaut pas validation d'un traitement complet après correction.

## Diagnostic complémentaire en lecture seule (29 octobre 2026)

Les bases SQLite locales de `.50` et `.51` ont été ouvertes en mode `ro`. Le contrôle ne les a pas modifiées. Les lignes `executions`, `execution_events`, `execution_steps`, `execution_work`, `task_ownership`, `task_checkpoints`, `task_transfers` et `execution_artifacts` donnent les corrélations suivantes :

- L'exécution v5 `a42305f7-7824-41fc-b0e1-59aac55cbb77` (`2026-10-27 14:54:00Z`–`14:54:50Z`) termine en échec pendant `plex-check`. La commande enregistrée sur le worker retourne le code 1 et stderr dit que l'adresse Plex n'est pas configurée. Les étapes d'entrée et d'accès direct sont checkpointées à l'époque worker 1 ; `plex-check` ne l'est pas. La source a son SHA-256, aucun `result_sha` ni publication n'est demandé, et le détail durable confirme qu'aucun remplacement de source n'a eu lieu. Quatre autres exécutions v5 de la même sélection échouent au même bloc. Cela explique ces échecs avant encodage ; pour cet identifiant, l'état terminal `failed` a bien été enregistré et répliqué par le propriétaire.
- L'exécution v6 `4f729894-3365-4fca-981e-84d7bb9a0b54` termine après l'assemblage et la validation de sortie, puis échoue à `publish`. `result_sha` est présent, `publication_requested=1`, `published_path` reste absent et la publication n'est pas confirmée. Source et résultat ont des empreintes distinctes ; aucune ligne de transfert inter-serveurs n'est attachée à cette exécution. La source reste donc conservée. Cela concorde avec l'`UnauthorizedAccessException` déjà relevée après validation et la cause de permissions du conteneur : UID `1654` sans le groupe de la bibliothèque `568` avant correction. Le contrôle courant, sans écriture, retrouve le groupe `568`, le dossier de bibliothèque en `568:568` avec mode `2775`, et lecture/traversée/écriture effectives sur le dossier et les fichiers Overgeared. Aucun ACL n'a été modifiée pendant ce diagnostic.
- Pour les deux identifiants, `command_logs` est une concaténation invalide de deux tableaux JSON (`[]` suivi d'un tableau contenant une commande) sur les deux bases. Les flows v5 et v6 n'ont pas d'ID de nœud dupliqué. `ExecutionRepository.PipelineAsync` désérialisait pourtant la colonne comme un tableau JSON unique : cette `JsonException` explique le HTTP 500 de `/api/v1/executions/{id}/pipeline`. Le correctif lit les tableaux historiques adjacents, déduplique par identifiant de commande et renvoie un diagnostic structuré `command_log_unreadable` si la donnée est irrécupérable. Le journal runtime normalise la forme récupérable et refuse encore une écriture si elle ne l'est pas.

Au moment du contrôle, les deux identités de serveur étaient prêtes (`ready=1`), les deux bases avaient zéro projection en attente, zéro diagnostic de projection et `PRAGMA integrity_check=ok`. Les événements de propriété observés étaient appliqués ; les checkpoints des deux exécutions ciblées restent à l'époque 1 et aucune cession inter-serveurs n'est enregistrée. Les quarantaines et rejets conservés ont été laissés intacts. La validation par l'API n'était pas accessible sans authentification (`401`) et les anciens conteneurs/logs d'octobre 27 ont depuis été remplacés ; `system_logs` ne contient pas de ligne sur cette fenêtre. Ainsi, les messages historiques génériques « IOException côté runner » et « complétion refusée par le cluster » ne peuvent pas être attribués plus précisément à un ID avec les seuls artefacts conservés. Pour `a423…`, les données durables prouvent néanmoins l'échec de configuration Plex et une transition finale enregistrée, pas une propriété encore refusée. Ne pas assimiler ces messages à l'échec de publication v6.

### Fonctionnalité à prévoir : droits d'accès personnalisés par bibliothèque

Demande du propriétaire : proposer une option de configuration lorsque les droits par défaut ne suffisent pas, au niveau de l'accès d'un nœud à une bibliothèque.

- Distinguer les autorisations déclarées (`canRead`, `canWrite`) des droits réellement disponibles pour le processus du nœud.
- Permettre de décrire un accès personnalisé par couple bibliothèque/nœud, avec une identité ou des groupes d'accès adaptés au mécanisme d'hébergement. Les paramètres exacts restent à concevoir ; une valeur UID/GID saisie dans l'interface n'accorde pas à elle seule ces droits au système.
- Vérifier lecture, traversée des répertoires et publication dans un espace temporaire dédié, sans toucher aux fichiers média existants. Afficher une erreur précise avant de lancer un traitement long si la publication n'est pas possible.
- Conserver les droits par défaut lorsqu'aucune dérogation n'est définie et distinguer les besoins du nœud d'accès de ceux d'un worker recevant une copie.
- Ne pas appliquer automatiquement un `chmod` ou `chown` récursif sur les bibliothèques. Ne pas élever globalement les privilèges du conteneur pour une seule bibliothèque.
- Rendre visible la portée réelle d'une configuration Docker : un groupe ajouté au conteneur s'applique à tout son processus, pas seulement à une bibliothèque. Une dérogation réellement isolée doit être prise en charge par l'architecture d'exécution.

### Validation longue : comportement actuel à distinguer d'une option du bloc

Le moteur appelle `EnsureDecodedAsync` avant publication et à la fin d'une exécution ayant produit un média modifié. Il mémorise le décodage par SHA-256 pour le fichier courant. Désactiver `verifyDecoding` dans `probe-validate-output` ne supprime donc pas le décodage obligatoire : cela le reporte à la publication. Un choix entre contrôle rapide et décodage complet nécessiterait une évolution explicite du contrat du runtime et de ses garanties, pas seulement une modification du flow.
