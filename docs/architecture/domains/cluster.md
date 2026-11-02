---
domain: cluster
updated: 2026-10-27
legacy_adr: [0001, 0002, 0017, 0034, 0036, 0037, 0038, 0039, 0040, 0041, 0044, 0045, 0046, 0047, 0048, 0050, 0051, 0052, 0053, 0066]
code:
  - backend/src/Encoding.Api/Nodes/
  - backend/src/Encoding.Core/Nodes/
  - backend/src/Encoding.NodeAgent/Services/
  - backend/src/Encoding.Launcher/
  - backend/src/Encoding.Contracts/Nodes/
  - frontend/src/app/features/nodes/
tags: [nœud, cluster, rattachement, jumelage, image-unique, groupes, matériel, gpu]
---

# Cluster et nœuds

Un cluster est un ensemble de nœuds sans coordinateur permanent. Une installation seule est un **nœud autonome** ; un cluster n'existe que s'il est créé ou rejoint. Le stockage et la réplication sont dans [replication-storage.md](replication-storage.md).

## Vocabulaire (décidé par Rémi, 2026-10-15)

- **Nœud** : une installation. *Serveur* et *worker* sont des rôles d'un nœud (un nœud peut avoir les deux), jamais des types de machine.
- **Nœud autonome** : n'appartient à aucun cluster (`standalone`, vrai tant que le cluster n'est pas « établi » et qu'il y a au plus un nœud actif). **Cluster** : unité d'administration (état partagé, bibliothèques, utilisateurs).
- **Groupe de nœuds** : classement à l'intérieur d'un cluster, sans effet sur la gestion.

## État courant

### Image unique et modes
- Une image `encoding` embarque `Encoding.Api` (port 8080), `Encoding.NodeAgent` (8081, boucle locale) et `Encoding.Launcher` (point d'entrée, contrôle sur `127.0.0.1:8082`). Les anciennes images `encoding-api` et `encoding-node-agent` n'existent plus.
- Le mode (`server`, `worker`, `server,worker`) est persisté dans `node-mode.json` ; `Node__Roles` ne donne que la valeur initiale. Le mode change à chaud (`GET/PUT /api/v1/node/mode`, admin) ; l'agent tourne toujours (identité, présence). Un processus tombé est relancé avec délai croissant ; SIGTERM puis arrêt forcé à 45 s.
- Un administrateur peut changer le mode d'un autre nœud : `PUT /api/v1/nodes/{id}/mode` écrit `desired_roles`, livré par la réponse du heartbeat (`desiredRoles`), appliqué via le lanceur local. On ne peut pas retirer la fonction serveur du dernier serveur en ligne (409).
- Le nœud local de l'image est enrôlé d'office par un secret de conteneur (`local-enrollment.secret`, présenté seulement à un serveur en boucle locale). Le nom convivial est choisi dans l'assistant `/setup`.

### Architecture Maître / Workers et Gouvernance
- **Modèle Maître / Workers** : Un seul nœud détient la base SQLite du cluster (`cluster.db`) et le rôle serveur actif (API REST, ordonnanceur, interface Angular). Les autres nœuds fonctionnent en tant que Workers purs (`Node__Roles=worker`), exécutant `Encoding.NodeAgent` sans processus serveur API ni base SQLite, évitant toute contention de verrous concurrents ou boucle de réconciliation P2P.
- **Passation planifiée (Cas A, zéro perte)** : Un administrateur déclenche `POST /api/v1/cluster/leadership/transfer` vers un worker en ligne. Le Maître actuel gèle les nouvelles attributions (`draining`), crée un instantané atomique SQLite (`VACUUM INTO`), et délivre l'instruction au worker lors du heartbeat suivant (`LeadershipHandoffInstruction`). Le worker télécharge le snapshot, vérifie l'empreinte SHA-256, accuse réception, adopte `cluster.db` et démarre le rôle serveur via le lanceur local (`server=true, worker=true`). L'ancien maître rétrograde son mode local en worker pur (`server=false, worker=true`) et active la redirection permanente `HTTP 308` vers le nouveau Maître.
- **Sauvegarde miroir et promotion d'urgence (Cas B, reprise après sinistre)** : Le Maître génère périodiquement un snapshot SQLite en tâche de fond selon l'intervalle configuré (`database_backup_interval_minutes`, 60 min par défaut). La disponibilité du snapshot est annoncée aux workers lors des heartbeats (`ClusterDatabaseBackupNotification`). Les workers téléchargent passivement le snapshot dans `backup/cluster-latest.db` sans ouvrir de connexion SQLite (zéro verrou, stockage inerte). En cas de crash définitif du Maître, une promotion d'urgence (`POST /api/v1/worker/emergency-promote`) initialise `cluster.db` depuis le dernier miroir disponible et démarre le worker en tant que nouveau Maître.

### Création, rattachement, départ
- Dans `/setup`, « Rejoindre un cluster » crée d’abord l’administrateur local et ouvre sa session avant tout appel protégé de découverte, vérification d’adresse ou jumelage. L’assistant reste ouvert pour envoyer la demande et comparer le code ; une panne distante peut être retentée sans recréer le compte. Le succès est affiché seulement après confirmation par l’agent. Une demande en attente peut être suivie dans Nœuds ; recharger l’assistant après création du compte ramène au parcours authentifié habituel. Aucune route de jonction n’est ouverte anonymement.
- Créer un cluster : `POST /api/v1/cluster` (nom obligatoire, 409 si déjà créé) ; renvoie un jeton API cluster par défaut ([auth-ops.md](auth-ops.md)). Un second nœud rattaché établit aussi le cluster.
- Rejoindre : toujours approuvé par le cluster d'accueil. Deux voies : **enrôlement direct** (fenêtre ouverte par un admin, 1 à 60 min) ou **demande de jumelage** avec clé hexadécimale à usage unique (10 min) approuvée par un admin après comparaison d'un **code de vérification** à 6 caractères (calculé des deux côtés depuis l'identifiant du nœud et l'empreinte SHA-256 du jeton). Option admin « accepter la première liaison » (`PUT /api/v1/nodes/first-link`, 1 à 60 min, consommée atomiquement, journalisée).
- Changement de cluster en **deux phases** : l'ancienne identité reste en service jusqu'à la confirmation du nouveau cluster ; retour arrière à l'identique si refus, injoignable ou échéance (12 min). Un rattachement qui n'aboutit pas est abandonné et le nœud autonome redevient autonome (`switchFailure` garde la raison).
- Après rattachement, le serveur local reste actif, adopte l'identifiant du cluster d'accueil et rejoint le maillage ([replication-storage.md](replication-storage.md)). Aucune base n'est fusionnée ; les bibliothèques d'un nœud autonome ne sont pas importées (export JSON `GET /api/v1/libraries/export`, pas d'import).
- La découverte DNS-SD (`_encoding._tcp`) annonce uniquement les nœuds ayant le rôle serveur. Les annonces mDNS de pairs restent en mémoire et ne sont pas relayées dans le maillage ; les URL mDNS persistées par les anciennes versions sont supprimées au démarrage du service de réconciliation.
- Quitter : `POST /api/v1/worker/leave` (agent) → `POST /api/v1/nodes/{id}/leave` ; refusé (409) si un traitement est en cours. Une identité retirée ne se réactive jamais : heartbeat d'un identifiant retiré = 410, l'agent prend une identité neuve.
- Retrait par un admin : `DELETE /api/v1/nodes/{id}` révoque le jeton, supprime les accès directs, conserve l'historique, refuse (409) un nœud qui détient un bail valide.
- Gestion par le navigateur : uniquement via l'API serveur (`/api/v1/local-node/*`, relais vers l'agent local avec le secret de conteneur). Le navigateur n'appelle jamais l'agent ; pas de CORS ni de port 8081 à publier. La route `/worker` redirige vers `/nodes`.

### États, compatibilité, identités
- État de cycle de vie dérivé, le plus restrictif gagne : `Quarantined` > `Incompatible` > `Draining` > `Offline` > `Online`. `cluster_nodes.accepts_work` est faux pour les trois premiers : aucun bail n'est accordé (`RuntimeRepository.ClaimAsync`, `LibraryWorkflowRepository.ClaimScanAsync`).
- Vidange : `PUT /api/v1/nodes/{id}/draining`. Incompatible : version de protocole hors plage (`ClusterProtocol.Current = 1`, `Minimum = 0`) ; se lève après mise à jour. Quarantaine : nonce de session alterné a→b→a en moins de 10 min (identité dupliquée) ; levée par `POST /api/v1/nodes/{id}/quarantine/clear`.
- Présence : `lastSeenAt` est horodaté par le serveur ; hors ligne après `NodePresence:OfflineAfterSeconds` (30). Le heartbeat est protégé par `X-Node-Token`.

### Groupes, étiquettes, activité
- Groupes manuels ou automatiques (critères : rôles, OS, architecture, mémoire, CPU, outils, concurrence). Étiquettes effectives = union des groupes, puis étiquettes du nœud (prioritaires). Résolution **à l'attribution**, jamais figée à la publication d'un flow. Supprimer un groupe ciblé par un flow publié ou brouillon : 409.
- `GET /api/v1/nodes/{id}/activity` et `GET /api/v1/nodes/activity` : baux actifs, totaux, dernière activité (lus localement, voir limites).

### Matériel
- Premier démarrage : la fiche anonyme, disponible seulement avant création de l'administrateur, expose les adresses IPv4/IPv6 assignées à toutes les interfaces avec leur nom (sans troncature arbitraire, dédoublonnées par interface/adresse). En bridge, ces interfaces appartiennent au conteneur ; l'adresse d'accès de l'hôte provient d'une URL annoncée configurée ou, à défaut, de l'origine utilisée par le navigateur. Sans métadonnées de déploiement, aucune détection de l'IP LAN de l'hôte Docker n'est revendiquée.
- Inventaire déclaré par l'agent (`HardwareInventory` : OS, architecture, CPU, mémoire, GPU via `/proc` et `/sys/class/drm`, noms commerciaux via `hwdata`). Aucune charge CPU/GPU n'existe : ne pas en inventer.
- Encodage matériel : pilotes VA-API Intel/AMD dans l'image, NVIDIA via NVIDIA Container Toolkit (`docker-compose.gpu.yml`, optionnel). Un encodeur n'est « utilisable » que si une mire synthétique 64×64 réussit (`ToolCapabilityProbe`). `GpuDeviceSelector` : `ENCODING_GPU_DEVICE`, sinon premier `renderD*`. Un seul périphérique de rendu par nœud.

### Interface (page Nœuds)
La page **Nœuds** comporte trois sous-vues : **Ce nœud**, **Cluster** et **Groupes de nœuds**. Les vues Ce nœud et Cluster partagent `NodeOverviewCardComponent` : l'en-tête présente l'identité et l'icône de plateforme, les valeurs CPU/RAM/GPU, l'admission et la capacité de traitement ; les fonctions occupent un bloc sous l'identité, les ressources et l'admission, et le dernier signal reste sous la capacité. Les sections « État et activité » et « Matériel déclaré » sont retirées de ces cartes : leur état courant est résumé dans l'en-tête, tandis que les métriques historiques ne sont plus répétées dans la carte. Les rôles worker/serveur et l'admission de nouveaux traitements se règlent avec des commutateurs ; la confirmation d'un changement de rôle rappelle les effets et les contrôles métier. Les demandes de rattachement restent en tableau et les actions groupées appellent l'API unitaire. L'inventaire facultatif annonce aussi l'environnement d'exécution (`docker`, `podman`, `container`, `native`), sans invalider les agents antérieurs qui ne le transmettent pas. L'assistant de rattachement utilise `GuidedSetup` (stepper CDK). Détails visuels dans [ui.md](ui.md).

Dans **Ce nœud** comme dans **Cluster**, l'icône correspond à l'environnement Docker quand il est signalé, sinon au système Linux ou Windows déclaré ; les mesures matérielles sont centrées et agrandies dans leur bloc. La synthèse de capacité affiche les baux actifs du worker et les places simultanées restantes. Quand les données sont indisponibles, l'interface l'indique sans inventer de compte. Le bandeau d'isolement et de réconciliation conserve son icône et son texte sans cadre ni fond de panneau.

## Invariants
1. Aucun serveur permanent ; chaque serveur actif sert la gestion depuis sa copie locale, la convergence est éventuelle.
2. Un nœud appartient à un seul cluster ; l'arrivée est toujours approuvée par le cluster d'accueil ; secrets jamais envoyés à un autre cluster que celui choisi par un admin.
3. Une identité retirée n'est jamais réactivée implicitement (heartbeat, enrôlement).
4. Les routes de gestion sont fermées par défaut ([auth-ops.md](auth-ops.md)).

## Non livré / limites
- **Relations entre clusters** (transfert de nœuds, partage de bibliothèques, propriété avec époque) : conçu dans l'ancien ADR 0038 §9, **non implémenté**, numéro 0042 réservé jamais utilisé. Voir [open-questions.md](../open-questions.md).
- État « Silencieux » (en ligne sans progrès) absent. Contrôle de compatibilité limité à la version de protocole (pas de plage de schéma ni règle « serveurs avant workers »).
- Activité d'exécution (baux) locale au serveur qui les possède : indisponible depuis un autre serveur.
- Non vérifié en conditions réelles : GPU réel, changement de mode en charge sur un cluster multi-hôtes, image Docker sous Landlock.

## Documents opérationnels
`docs/operations/release.md`, `docs/operations/backup-restore.md`, `backend/README.md`, `backend/docker-compose.yml`.

## Présence et métadonnées
- Un heartbeat dont nom, rôles, capacité, outils et inventaire sont identiques ne modifie que `last_seen_at`. Toute modification ou confirmation des rôles demandés emprunte la mise à jour complète, toujours authentifiée par le hash du jeton et refusée après suppression du nœud. Aucun cache de métadonnées ne remplace la base.
- Le protocole et le nonce de session ne sont réécrits que s’ils changent ; la détection des identités concurrentes, la quarantaine et les raisons d’incompatibilité sont conservées. Le contrat de heartbeat et le protocole réseau ne changent pas.

## Modèle unique sans quorum (2026-10-25)
Le mode optionnel de consensus et son transport dédié sont retirés : chaque serveur utilise sa base locale et le maillage signé. Le travail local ne dépend d'aucun quorum. Voir ADR [0013](../adr/0013-retrait-mode-raft.md). Cette première étape est complétée par les transferts et la reprise manuelle de l'ADR 0016, sans quorum sur les données.

### Attribution initiale et redémarrage (ADR 0015)
Chaque nouveau travail local possède désormais un plan signé, créé dans la même transaction que la mise en file. Le serveur initiateur est propriétaire initial ; les workers sont choisis selon les règles existantes. Après redémarrage, les claims attendent le rattrapage authentifié et la projection du journal. Une file déjà ouverte continue son travail possédé pendant une partition. L'époque initiale vaut 1 ; un transfert ou une reprise journalisée l'augmente. Les étapes suivantes sont livrées par ADR 0016.

### Partition, relais et reprise
La page Nœuds montre isolement, réceptions inactives, préparation/cache des checkpoints et neutralisation locale. `GET /api/v1/cluster/tasks/status` expose aussi l'identité de maillage du serveur, les observations avec provenance et leurs expirations.

Les changements de fonctions et retraits de serveurs sont refusés pendant une partition ; les chemins local et distant utilisent la même garde. Les files actives doivent être vidées ou cédées avant retrait de serveur. Un simple worker sans bail peut quitter selon la fusion LWW/tombstone existante, sans transférer la propriété d'une tâche.

À deux serveurs (ADR [0026](../adr/0026-reprise-auto-reparatrice-deux-noeuds.md)), la maintenance assure une reprise auto-réparatrice : lorsqu'un serveur redémarre alors que le pair est hors ligne dans `cluster_nodes`, il débloque sa file locale (`ready = 1`) après projection de ses inboxes ; si l'unique pair serveur reste hors ligne (> 90 s) et injoignable alors que des tâches, quarantaines ou remplacements de source en dépendent, le survivant le neutralise automatiquement, reprend après le délai de grâce de 135 s les tâches éligibles (au checkpoint vérifié ou depuis la source à révision 0 si aucun effet non idempotent ni publication n'a eu lieu), et réadmet automatiquement le pair dès son retour et rattrapage confirmé. Les commandes manuelles (`neutralize`, `recover`, `force-source-retry`, `readmit`) restent disponibles. À trois serveurs ou plus, la reprise attend le futur témoin/mini-consensus dédié ; calculs et transferts coopératifs n'en dépendent pas.

Le rattachement d’un nœud déjà initialisé vérifie une liste UUID/nom signée du cluster d’accueil. Une collision insensible à la casse suspend l’adoption et se résout explicitement dans Paramètres > Utilisateurs par renommage du compte entrant, sans modification d’identité, credentials ou sessions. Un hôte ancien ne peut rattacher une installation avec comptes sans ce préflight signé. Les adresses annoncées lors du jumelage sont conservées dans `node_peer_reconciliation_urls` (0060, réconciliée), utilisables après admission du pair correspondant et vérification de sa réponse signée. Les alias de l’identité locale sont exclus du rattrapage distant. Voir [ADR 0022](../adr/0022-controle-media-explicite-et-rattachement.md).
