---
domain: cluster
updated: 2026-11-02
legacy_adr: [0001, 0002, 0017, 0034, 0036, 0037, 0038, 0039, 0040, 0041, 0044, 0045, 0046, 0047, 0048, 0050, 0051, 0052, 0053, 0066]
code:
  - backend/src/Encoding.Api/Nodes/
  - backend/src/Encoding.Core/Nodes/
  - backend/src/Encoding.NodeAgent/Services/
  - backend/src/Encoding.Launcher/
  - backend/src/Encoding.Contracts/Nodes/
  - frontend/src/app/features/nodes/
tags: [nœud, cluster, rattachement, jumelage, central, worker, deux-images, groupes, matériel, gpu]
---

# Cluster et nœuds

Un cluster est un ensemble de nœuds sans coordinateur permanent. Une installation seule est un **nœud autonome** ; un cluster n'existe que s'il est créé ou rejoint. Le stockage et la réplication sont dans [replication-storage.md](replication-storage.md).

## Vocabulaire (décidé par Rémi, 2026-10-15)

- **Nœud** : une installation. *Serveur* et *worker* sont des rôles d'un nœud (un nœud peut avoir les deux), jamais des types de machine.
- **Nœud autonome** : n'appartient à aucun cluster (`standalone`, vrai tant que le cluster n'est pas « établi » et qu'il y a au plus un nœud actif). **Cluster** : unité d'administration (état partagé, bibliothèques, utilisateurs).
- **Groupe de nœuds** : classement à l'intérieur d'un cluster, sans effet sur la gestion.

## État courant

### Deux images, un central
- Le Dockerfile publie `central` et `worker` ([ADR 0036](../adr/0036-separation-stricte-central-worker.md)). `central` embarque `Encoding.Api` (8080), l'interface, la base SQLite, `Encoding.NodeAgent` (worker intégré, 8081, boucle locale) et `Encoding.Launcher`, qui démarre et surveille les deux processus sans jamais changer de mode. Il est autonome et reste l'unique autorité du cluster : il pilote les workers.
- `worker` n'embarque que l'agent et son interface Angular (8081) : ni API, ni lanceur, ni base de données. Il n'est jamais autonome : tant qu'il n'a pas de central, son interface ne propose que le choix du central. Son interface est alimentée par l'API du central (relayée par `CentralUiProxy`) et son flux SSE.
- Le worker est enrôlé à son setup : l'assistant demande l'adresse du central et un compte administrateur de ce central ([ADR 0034](../adr/0034-rattachement-par-compte-administrateur.md)). `POST /api/v1/worker/clusters` (choix du central) n'est ouvert sans secret local que tant que le worker n'a pas de central.
- Le worker intégré du central est enrôlé d'office par un secret de conteneur (`local-enrollment.secret`, présenté seulement à un serveur en boucle locale). Le nom convivial est choisi dans l'assistant `/setup`.
- **Aucune passation** : la fonction serveur ne se donne pas et ne se retire pas. Pas de changement de mode à chaud, local ou distant, pas de promotion d'urgence, pas de sauvegarde miroir envoyée aux workers. Les routes correspondantes sont retirées (elles rendaient la base et les clés Data Protection à tout porteur d'un jeton de nœud).

### Création, rattachement, départ
- Le central est créé par l'assistant `/setup` : administrateur, noms du nœud et du cluster, puis « Initialiser le central ». `POST /api/v1/cluster` (nom obligatoire, 409 si déjà créé) renvoie un jeton API cluster par défaut ([auth-ops.md](auth-ops.md)). Un central ne rejoint jamais un autre cluster.
- Un **worker** rejoint un central : toujours approuvé par le central. Voies : **enrôlement par compte administrateur** à son setup ([ADR 0034](../adr/0034-rattachement-par-compte-administrateur.md)), **enrôlement direct** (fenêtre ouverte par un admin, 1 à 60 min) ou **demande de jumelage** avec clé hexadécimale à usage unique (10 min) approuvée par un admin après comparaison d'un **code de vérification** à 6 caractères (calculé des deux côtés depuis l'identifiant du nœud et l'empreinte SHA-256 du jeton). Option admin « accepter la première liaison » (`PUT /api/v1/nodes/first-link`, 1 à 60 min, consommée atomiquement, journalisée).
- Changement de central en **deux phases** : l'ancienne identité reste en service jusqu'à la confirmation du nouveau ; retour arrière à l'identique si refus, injoignable ou échéance (12 min). Un rattachement qui n'aboutit pas est abandonné (`switchFailure` garde la raison).
- La découverte DNS-SD (`_encoding._tcp`) annonce le central pour que les workers le trouvent ; une adresse saisie à la main reste possible.
- Quitter : `POST /api/v1/worker/leave` (agent) → `POST /api/v1/nodes/{id}/leave` ; refusé (409) si un traitement est en cours. Une identité retirée ne se réactive jamais : heartbeat d'un identifiant retiré = 410, l'agent prend une identité neuve.
- Retrait par un admin : `DELETE /api/v1/nodes/{id}` révoque le jeton, supprime les accès directs, conserve l'historique, refuse (409) un nœud qui détient un bail valide. Le nœud du central lui-même ne se retire pas (409).
- Gestion par le navigateur : uniquement via l'API du central (`/api/v1/local-node/*`, relais vers l'agent local avec le secret de conteneur). Le navigateur n'appelle jamais l'agent ; pas de CORS ni de port 8081 à publier. La route `/worker` redirige vers `/nodes`.

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
- Encodage matériel : pilotes VA-API Intel/AMD dans l'image, NVIDIA via NVIDIA Container Toolkit. Le worker de `docker-compose.central.yml` déclare `/dev/dri` et doit recevoir le GID de rendu de l'hôte. Un encodeur n'est « utilisable » que si une mire synthétique 64×64 réussit (`ToolCapabilityProbe`). `GpuDeviceSelector` : `ENCODING_GPU_DEVICE`, sinon premier `renderD*`. Un seul périphérique de rendu par nœud.

### Interface (page Nœuds)
La page **Nœuds** comporte trois sous-vues : **Ce nœud**, **Cluster** et **Groupes de nœuds**. Les vues Ce nœud et Cluster partagent `NodeOverviewCardComponent` : l'en-tête présente l'identité et l'icône de plateforme, les valeurs CPU/RAM/GPU, l'admission et la capacité de traitement ; les fonctions occupent un bloc sous l'identité, les ressources et l'admission, et le dernier signal reste sous la capacité. Les sections « État et activité » et « Matériel déclaré » sont retirées de ces cartes : leur état courant est résumé dans l'en-tête, tandis que les métriques historiques ne sont plus répétées dans la carte. Les rôles sont affichés sans commutateur (l'image les fixe) ; seule l'admission de nouveaux traitements se règle avec un commutateur. Les demandes de rattachement restent en tableau et les actions groupées appellent l'API unitaire. L'inventaire facultatif annonce aussi l'environnement d'exécution (`docker`, `podman`, `container`, `native`), sans invalider les agents antérieurs qui ne le transmettent pas. L'assistant de rattachement utilise `GuidedSetup` (stepper CDK). Détails visuels dans [ui.md](ui.md).

Dans **Ce nœud** comme dans **Cluster**, l'icône correspond à l'environnement Docker quand il est signalé, sinon au système Linux ou Windows déclaré ; les mesures matérielles sont centrées et agrandies dans leur bloc. La synthèse de capacité affiche les baux actifs du worker et les places simultanées restantes. Quand les données sont indisponibles, l'interface l'indique sans inventer de compte.

## Invariants
1. Un seul central, unique autorité et unique base ; il pilote les workers. Aucune passation, aucune réplication.
2. Un worker appartient à un seul central ; son arrivée est toujours approuvée par ce central ; secrets jamais envoyés à un autre central que celui choisi par un admin.
3. Une identité retirée n'est jamais réactivée implicitement (heartbeat, enrôlement).
4. Les routes de gestion sont fermées par défaut ([auth-ops.md](auth-ops.md)).

## Non livré / limites
- État « Silencieux » (en ligne sans progrès) absent. Contrôle de compatibilité limité à la version de protocole (pas de plage de schéma ni règle « serveurs avant workers »).
- Non vérifié en conditions réelles : GPU réel, parcours deux conteneurs avec navigateur, image Docker sous Landlock.

## Documents opérationnels
`docs/operations/release.md`, `docs/operations/backup-restore.md`, `backend/README.md`, `backend/docker-compose.central.yml`.

## Présence et métadonnées
- Un heartbeat dont nom, rôles, capacité, outils et inventaire sont identiques ne modifie que `last_seen_at`. Toute modification ou confirmation des rôles demandés emprunte la mise à jour complète, toujours authentifiée par le hash du jeton et refusée après suppression du nœud. Aucun cache de métadonnées ne remplace la base.
- Le protocole et le nonce de session ne sont réécrits que s’ils changent ; la détection des identités concurrentes, la quarantaine et les raisons d’incompatibilité sont conservées. Le contrat de heartbeat et le protocole réseau ne changent pas.
