---
domain: workers
updated: 2026-11-07
legacy_adr: [0001, 0002, 0017, 0034, 0036, 0037, 0038, 0039, 0040, 0041, 0044, 0045, 0046, 0047, 0048, 0050, 0051, 0052, 0053, 0066]
code:
  - backend/src/Encoding.Api/Workers/
  - backend/src/Encoding.Core/Workers/
  - backend/src/Encoding.WorkerAgent/Services/
  - backend/src/Encoding.Launcher/
  - backend/src/Encoding.Contracts/Workers/
  - frontend/src/app/features/workers/
tags: [worker, central, rattachement, jumelage, version, deux-images, groupes, matériel, gpu]
---

# Central et workers

Il n'y a qu'un **central**, unique autorité et unique base de données, et des **workers** qui traitent les médias. Le central embarque son propre worker. Il n'existe ni cluster maillé, ni nœud autonome, ni passation de la fonction serveur ([ADR 0036](../adr/0036-separation-stricte-central-worker.md), [ADR 0040](../adr/0040-vocabulaire-central-worker.md)). Le stockage est décrit dans [storage.md](storage.md).

## Vocabulaire (décidé par Rémi, 2026-11-02)

- **Central** : l'installation qui porte l'API, l'interface, la base et l'autorité sur les tâches. Il existe dès l'installation et a un nom que l'administrateur peut changer (`PUT /api/v1/central`). Il ne rejoint jamais un autre central.
- **Worker** : toute installation qui traite des médias. Un worker n'a qu'un central. Le rôle est fixé par l'image, jamais changé à chaud.
- **Groupe de workers** : classement sans effet sur la gestion, utilisé par les règles de placement et les relais.
- Les anciens termes « nœud » et « cluster » n'existent plus dans le produit ; ils subsistent seulement dans les archives datées (ADR, incidents, revues de conception).

## État courant

### Deux images, un central
- Le Dockerfile publie `central` et `worker`. `central` embarque `Encoding.Api` (8080), l'interface, la base SQLite, `Encoding.WorkerAgent` (worker intégré, 8081, boucle locale) et `Encoding.Launcher`, qui démarre et surveille les deux processus sans jamais changer de mode.
- `worker` n'embarque que l'agent et son interface Angular (8081) : ni API, ni lanceur, ni base. Il n'est jamais autonome : tant qu'il n'a pas de central, son interface ne propose que le choix du central. Son interface est alimentée par l'API du central (relayée par `CentralUiProxy`) et son flux SSE.
- Le worker intégré du central est enrôlé d'office par un secret de conteneur (`local-enrollment.secret`, présenté seulement à un serveur en boucle locale). Le lanceur impose à l'agent intégré `Central__ApiBaseUrl=http://127.0.0.1:<port de l'API>` **quelle que soit** la valeur de `Central:ApiBaseUrl` fournie à l'image central (l'enrôlement local exige cette adresse loopback exacte) ; une autre adresse est ignorée avec un avertissement au démarrage (`[launcher] Central:ApiBaseUrl est ignoré…`). Pour joindre un autre central, utiliser un worker distinct.
- Un worker doit avoir **exactement la version du central** ([ADR 0039](../adr/0039-version-exacte-worker-central.md)) : la version est fixée à la construction de l'image (`APP_VERSION`).

### Installation, rattachement, départ
- Le central est initialisé par l'assistant `/setup` : compte administrateur, nom du worker du central et nom du central (`POST /api/v1/auth/setup`). Il n'y a pas d'étape « créer un central ».
- Un **worker** rejoint un central par l'une de ces voies, toujours approuvée par le central : **compte administrateur** à son setup ([ADR 0034](../adr/0034-rattachement-par-compte-administrateur.md)) ; **enrôlement direct** (fenêtre ouverte par un administrateur, 1 à 60 min) ; **demande de jumelage** avec clé hexadécimale à usage unique (10 min) approuvée après comparaison d'un code de vérification à 6 caractères, calculé des deux côtés depuis l'identifiant du worker et l'empreinte SHA-256 du jeton. Option administrateur « accepter la première liaison » (`PUT /api/v1/workers/first-link`, 1 à 60 min, consommée atomiquement et journalisée).
- L'enrôlement est refusé (400, champ `version`) si la version du worker diffère de celle du central.
- Le worker trouve son central par la découverte DNS-SD (`_encoding._tcp`) ou par une adresse saisie ; `Central__ApiBaseUrl` fixe l'adresse par configuration. `POST /api/v1/worker/centrals` n'est ouvert sans secret local que tant que le worker n'a pas de central.
- Pour changer de central, un worker le quitte puis rejoint l'autre avec une identité neuve ; il n'y a pas de changement en deux phases. Un rattachement qui n'aboutit pas (refus, central injoignable, clé expirée) est abandonné et `enrollmentFailure` garde la raison.
- Quitter : `POST /api/v1/worker/leave` (agent) puis `POST /api/v1/workers/{id}/leave` ; refusé (409) si un traitement est en cours. Une identité retirée ne se réactive jamais : le heartbeat d'un identifiant retiré reçoit 410 et l'agent prend une identité neuve.
- Retrait par un administrateur : `DELETE /api/v1/workers/{id}` révoque le jeton, supprime les accès directs, conserve l'historique et refuse (409) un worker qui détient un bail valide. Le worker du central lui-même ne se retire pas (409).
- Le navigateur n'appelle jamais l'agent : tout passe par l'API du central (`/api/v1/local-worker/*`, relais vers l'agent local avec le secret de conteneur). Pas de CORS ni de port 8081 à publier. La route `/worker` redirige vers `/workers`.

### États et présence
- État de cycle de vie dérivé, le plus restrictif gagne : `Quarantined` > `Incompatible` > `Draining` > `Offline` > `Online`. `workers.accepts_work` est faux pour les trois premiers : aucun bail n'est accordé (`RuntimeRepository.ClaimAsync`, `LibraryWorkflowRepository.ClaimScanAsync`).
- Vidange : `PUT /api/v1/workers/{id}/draining`. Incompatible : version différente de celle du central ; se lève après mise à jour du worker. Quarantaine : nonce de session alterné a→b→a en moins de 10 min (identité dupliquée) ; levée par `POST /api/v1/workers/{id}/quarantine/clear`.
- Présence : `lastSeenAt` est horodaté par le central ; hors ligne après `WorkerPresence:OfflineAfterSeconds` (30). Le heartbeat est protégé par `X-Node-Token` (nom d'en-tête conservé).
- Un heartbeat dont nom, rôles, capacité, outils et inventaire sont identiques ne modifie que `last_seen_at`. Toute autre modification emprunte la mise à jour complète, authentifiée par le hash du jeton et refusée après suppression du worker. La version et le nonce de session ne sont réécrits que s'ils changent.

### Groupes, étiquettes, activité
- Groupes manuels ou automatiques (critères : rôles, OS, architecture, mémoire, CPU, outils, concurrence). Étiquettes effectives = union des groupes, puis étiquettes du worker (prioritaires). Résolution **à l'attribution**, jamais figée à la publication d'un flow. Supprimer un groupe ciblé par un flow publié ou brouillon : 409.
- `GET /api/v1/workers/{id}/activity` et `GET /api/v1/workers/activity` : baux actifs, totaux, dernière activité.

### Matériel
- Premier démarrage : la fiche anonyme, disponible seulement avant création de l'administrateur, expose les adresses IPv4/IPv6 assignées à toutes les interfaces avec leur nom (sans troncature arbitraire, dédoublonnées par interface/adresse). En bridge, ces interfaces appartiennent au conteneur ; l'adresse d'accès de l'hôte provient d'une URL annoncée configurée ou, à défaut, de l'origine utilisée par le navigateur. Sans métadonnées de déploiement, aucune détection de l'IP LAN de l'hôte Docker n'est revendiquée.
- Inventaire déclaré par l'agent (`HardwareInventory` : OS, architecture, CPU, mémoire, GPU via `/proc` et `/sys/class/drm`, noms commerciaux via `hwdata`). Aucune charge CPU/GPU n'existe : ne pas en inventer.
- Encodage matériel : pilotes VA-API Intel/AMD dans l'image, NVIDIA via NVIDIA Container Toolkit. Le worker de `docker-compose.central.yml` déclare `/dev/dri` et doit recevoir le GID de rendu de l'hôte. Un encodeur n'est « utilisable » que si une mire synthétique 64×64 réussit (`ToolCapabilityProbe`). `GpuDeviceSelector` : `ENCODING_GPU_DEVICE`, sinon premier `renderD*`. Un seul périphérique de rendu par worker.

### Interface (page Workers)
La page **Workers** (`/workers`) comporte trois sous-vues : **Ce worker**, **Central** et **Groupes de workers**. Les vues Ce worker et Central partagent `WorkerOverviewCardComponent` : l'en-tête présente l'identité et l'icône de plateforme, les valeurs CPU/RAM/GPU, l'admission et la capacité de traitement ; les fonctions occupent un bloc sous l'identité, les ressources et l'admission, et le dernier signal reste sous la capacité. Les sections « État et activité » et « Matériel déclaré » sont retirées de ces cartes : leur état courant est résumé dans l'en-tête, tandis que les métriques historiques ne sont plus répétées dans la carte. Les rôles sont affichés sans commutateur (l'image les fixe) ; seule l'admission de nouveaux traitements se règle avec un commutateur. Les demandes de rattachement restent en tableau et les actions groupées appellent l'API unitaire. L'inventaire facultatif annonce aussi l'environnement d'exécution (`docker`, `podman`, `container`, `native`), sans invalider les agents antérieurs qui ne le transmettent pas. L'assistant de rattachement utilise `GuidedSetup` (stepper CDK). Détails visuels dans [ui.md](ui.md).

Dans **Ce worker** comme dans **Central**, l'icône correspond à l'environnement Docker quand il est signalé, sinon au système Linux ou Windows déclaré ; les mesures matérielles sont centrées et agrandies dans leur bloc. La synthèse de capacité affiche les baux actifs du worker et les places simultanées restantes. Quand les données sont indisponibles, l'interface l'indique sans inventer de compte.

## Invariants
1. Un seul central, unique autorité et unique base ; il pilote les workers. Aucune passation, aucune réplication.
2. Un worker appartient à un seul central ; son arrivée est toujours approuvée par ce central ; les secrets ne sont jamais envoyés à un autre central que celui choisi par un administrateur.
3. Un worker n'est accepté que s'il a la version exacte du central.
4. Une identité retirée n'est jamais réactivée implicitement (heartbeat, enrôlement).
5. Les routes de gestion sont fermées par défaut ([auth-ops.md](auth-ops.md)).

## Non livré / limites
- État « Silencieux » (en ligne sans progrès) absent. Deux builds sans `APP_VERSION` portent tous deux `0.0.0-dev` : la règle de version ne les distingue pas.
- Plugins et outils du Store : aujourd'hui un worker n'a que ce qu'un administrateur lui a attribué (état par worker, vide par défaut, voir [plugins.md](plugins.md)). **Décidé, non livré** ([ADR 0046](../adr/0046-etat-store-des-workers-suit-le-central.md), `proposed`) : chaque worker, y compris le worker intégré, suivra l'état Store du central (état complet envoyé par SSE à l'enrôlement et à chaque connexion, puis changements par SSE ; flux refusé à un worker incompatible). Tant qu'il n'est pas **conforme** (paquets du central appliqués, `ffmpeg`/`ffprobe` passant la sonde, plugins répondant à leur poignée de main), il ne reçoit aucune attribution : nouvel état `StoreNotCompliant`, entre `Incompatible` et `Draining`, avec les sous-états `syncing`, `non_compliant`, `failed` et leur motif. Les baux en cours, ainsi que la finalisation ou le retour arrière d'un remplacement de source, vont à leur terme ; rien n'est réattribué.
- Non vérifié en conditions réelles : GPU réel, parcours deux conteneurs avec navigateur, image Docker sous Landlock.

## Documents opérationnels
`docs/operations/release.md`, `docs/operations/backup-restore.md`, `backend/README.md`, `backend/docker-compose.central.yml`.

### Continuité du lien au central
- Le heartbeat ne transmet plus `MasterUrl` et ne peut pas réécrire le central d'enrôlement. Les adresses découvertes restent des candidats d'enrôlement ; le jeton existant est envoyé uniquement à `ApiBaseUrl`.
- Reconnexion SSE : plafond exponentiel de 1 à 60 s avec jitter complet, réinitialisé après 30 s stables ; `retry:` est pris en compte dans cette borne. Un silence de 45 s ferme le flux. Le modèle SSE et filet de 30 s reste inchangé.
- Le central ne garde qu'un flux de réveil par worker : une nouvelle connexion ferme l'ancienne. Jeton et existence sont revérifiés toutes les 15 s, même pendant des réveils fréquents.
- Heartbeat : jitter ±10 %. Découverte et auto-enrôlement passent à cinq minutes une fois le worker enrôlé.
