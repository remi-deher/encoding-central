# Reprise du travail — état au 2026-11-02

Note de passage pour reprendre sur une autre machine. Elle résume l'état après la fusion de la PR #1 (`dev` vers `main`), ce qui reste à faire et les pièges d'environnement. Les décisions durables sont dans les ADR ; les écarts connus dans [open-questions.md](architecture/open-questions.md).

## Où en est-on

Fait et dans `main` (PR #1, 16 contrôles verts sur 16) :
- Séparation stricte central / worker ([ADR 0036](architecture/adr/0036-separation-stricte-central-worker.md)) : deux images (`central`, `worker`), worker sans base, aucune passation, maillage multi-serveur retiré, migrations 0066 et 0067.
- Rattachement du worker par compte administrateur du central ([ADR 0034](architecture/adr/0034-rattachement-par-compte-administrateur.md)), vérifié sur conteneurs réels. Route `GET /api/v1/worker/hardware` (lecture seule, ouverte tant que le worker n'est pas rattaché).
- Correctif de sécurité : le relais du worker ne garde plus de cookie de session partagé (un visiteur anonyme obtenait la session administrateur après une connexion via le worker).
- Setup du central et du worker : accueil non compté, trois étapes, matériel détecté à l'accueil seulement ; page de connexion sans en-tête ni halo.
- CI : `image.yml`, `release.yml` et `backend.yml` construisent `--target central` et `--target worker` ; `DockerClusterTests` et `WorkerProcessLossTests` réécrits et verts.

Non vérifié :
- **La release.** Aucun lancement du workflow `Release` n'a été vu après la fusion. Les trois derniers (08:40 à 08:49 UTC) ont échoué avant la PR ; leurs journaux n'ont pas été lus. Hypothèse non vérifiée : l'auto-merge fait par le jeton GitHub ne déclenche pas les workflows sur `push`. À vérifier dans l'onglet Actions ; au besoin, lancer `Release` à la main (`workflow_dispatch`). Cela publie `encoding-central` et `encoding-worker` sur Docker Hub (nouveaux noms, ils remplacent `encoding`).
- La migration 0067 n'a été testée que sur bases neuves. **Sauvegarder `cluster.db` avant de mettre à jour une installation existante.**
- Navigateur piloté de bout en bout sur l'étape « Rattacher ce worker » ; thème sombre et mobile des étapes 2 et 3 du setup.
- NuGet : contrôle des vulnérabilités non fait.

## Reste à faire, par ordre conseillé

1. **Release** : vérifier ou lancer le workflow, contrôler que les deux images sont publiées, puis activer le scénario de compatibilité (`ENCODING_TEST_PREVIOUS_WORKER_IMAGE`, image worker de la dernière version publiée) dans `backend.yml`.
2. **Pages du worker** (prochain chantier). Le build worker (`ng build` configuration `worker`) remplace seulement `app.routes.ts` par `app.routes.worker.ts` ; les composants sont déjà communs, mais les routes sont recopiées (titres, navigation, descriptions) : risque de dérive. Proposition validée dans son principe, à confirmer page par page :
   - une seule table de routes, chaque route déclare ses profils (`central`, `worker`) et une description propre au worker au besoin ; une constante de build `APP_PROFILE` (remplacée par `fileReplacements`) filtre la table pour que les pages centrales disparaissent du bundle worker ; supprimer `app.routes.worker.ts` ;
   - un service `AppProfile` expose des capacités (`canManageLibraries`, `canEditNodes`…) ; les pages partagées masquent actions et sections avec `@if`, sans template parallèle ; au-delà de 2 ou 3 `@if (profile…)` dans une page, la découper en composants communs plutôt que créer une variante worker ;
   - les données font la différence : le worker relaie `/api/*` vers le central avec la session de l'utilisateur ; l'Activité d'un worker est un filtre par défaut sur ce nœud ;
   - un test d'architecture interdit d'importer du code propre au worker depuis `features/`.

   Contenu proposé par page (**décision métier de Rémi à confirmer** ; question ouverte : actions autorisées sur le worker, par exemple annuler une tâche ou suspendre l'admission) :

   | Page | Sur le worker |
   |---|---|
   | Vue d'ensemble | Ce nœud : charge, tâches en cours, matériel |
   | Activité | Exécutions de ce nœud, lecture seule |
   | Nœuds | Ce nœud en détail, cluster en lecture seule, « quitter le central » |
   | Plugins & Outils | Ce que ce nœud a installé et sait exécuter |
   | Paramètres | Adresse du central et nom du nœud seulement |
3. **Worker et SSE** : l'interface du worker doit consommer le SSE du central ; décision en attente (voir open-questions, point 10).
4. **Résidus** (open-questions, point 10) : `task_ownership` / `task_server_identity` et le prédicat `TaskOwnershipRepository.ActiveSql` ; route `GET /api/v1/executions/{id}/ownership` inutilisée par l'interface.
5. **Nettoyage de `open-questions.md`** : les points 2, 4, 5, 7 et 9 décrivent encore le maillage ou la reprise entre serveurs, retirés par l'ADR 0036 ; à fermer ou réécrire. Même remarque pour les ADR antérieurs (0032, 0033 sont remplacés par 0036).
6. **Hygiène** : `backend/migrations/0036_library_cluster_tombstones.sql` (racine, non chargé), `backend/README.md` à réécrire, instructions du projet qui citent encore PostgreSQL.
7. **Non vérifié en conditions réelles** (liste complète dans open-questions) : Landlock en production, GPU réel, Plex/Jellyfin/*ARR réels, NAS/NFS.

## Reprendre l'environnement

Piles de test (Docker) :
```
cd backend
CENTRAL_API_PORT=5082 docker compose -f docker-compose.central.yml up -d --build
CENTRAL_API_PORT=5082 docker compose -f docker-compose.central.yml down -v   # remise à zéro
```
- Central : http://localhost:5082 (port interne 8080). Worker : http://localhost:5081 (port interne 8081).
- Dans l'assistant du worker, l'adresse du central est `http://server:8080` (réseau Docker du compose). L'IP LAN de l'hôte ne répond pas depuis le conteneur quand `ufw` est actif (`INPUT DROP`) ; entre deux machines distinctes, utiliser l'adresse LAN du central et son port publié.
- `down -v` supprime seulement les volumes nommés du projet ; les médias de test (`/home/remi/encoding-tests/*`, montés en bind) ne doivent jamais être supprimés. Adapter ces chemins sur une autre machine (`docker-compose.central.yml`).
- Le central est initialisé par l'interface (`/setup`) ; ne pas laisser de mot de passe de test dans le dépôt.

Vérifications avant de pousser (la CI ne tourne que sur la PR `dev` vers `main`) :
- Frontend (`frontend/`) : `npm run lint`, `npm run format:flows`, `npm run format:settings`, `npm test` (vitest ; **vérifie les gabarits Angular**, contrairement à `tsc`), `npm run build`, `node --test tests/*.test.mjs`. E2E : `PLAYWRIGHT_CHROMIUM_EXECUTABLE=/usr/bin/chromium npx playwright test`. Les contrôles de format ont fait échouer la CI plusieurs fois : les lancer tous, pas seulement Prettier sur les fichiers modifiés.
- Backend (`backend/`) : la CI compile avec `-warnaserror` **et** le `.editorconfig` racine (sans lui, environ 367 fausses erreurs d'analyse). Tests : `dotnet test tests/Encoding.Runtime.Tests -c Release --filter "Category!=Stress&FullyQualifiedName!~WorkerProcessLossTests&FullyQualifiedName!~DockerCluster"`.
- Tests lourds : `ENCODING_TEST_MULTIPROCESS=1` pour `WorkerProcessLossTests` (ffmpeg requis, environ 3,5 min) ; `ENCODING_TEST_DOCKER=1`, `ENCODING_TEST_CENTRAL_IMAGE`, `ENCODING_TEST_WORKER_IMAGE` pour `DockerClusterTests` (images avec ffmpeg : `.github/docker/Dockerfile.cluster-test` avec `BASE_IMAGE`).
- Sur la machine d'origine, les dossiers `obj/` du dépôt appartiennent à `root` : le backend y était compilé et testé sur une copie synchronisée dans `/tmp`. Sans objet si le dépôt est sain sur l'autre machine.

Règles du dépôt à garder en tête : changements poussés directement sur `dev`, CI sur la PR de promotion `dev` vers `main` ; nouvelle route fermée par défaut ; migrations additives à numéro unique ; aucun secret dans les journaux ; décision structurante = ADR court, changement de comportement = fiche de domaine mise à jour (voir [AGENTS.md](../AGENTS.md)).
