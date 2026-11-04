# Reprise du travail — état au 2026-11-03

Note de passage pour reprendre sur une autre machine. Elle résume l'état de `dev` après la fusion de la PR #1 (`dev` vers `main`) et le chantier de vocabulaire, ce qui reste à faire et les pièges d'environnement. Les décisions durables sont dans les ADR ; les écarts connus dans [open-questions.md](architecture/open-questions.md).

## Où en est-on

Fait dans `main` (PR #1) : séparation stricte central / worker ([ADR 0036](architecture/adr/0036-separation-stricte-central-worker.md)), deux images, rattachement par compte administrateur ([ADR 0034](architecture/adr/0034-rattachement-par-compte-administrateur.md)), correctif du cookie de session du relais, setup en trois étapes, CI qui construit les deux images.

Fait sur `dev`, **pas encore promu vers `main`** (la CI de promotion n'a jamais exécuté ce lot) :
- **Version exacte** ([ADR 0039](architecture/adr/0039-version-exacte-worker-central.md)) : un worker n'est accepté que s'il a la version du central (`AppVersion`, `APP_VERSION` à la construction des images).
- **Vocabulaire central / worker** ([ADR 0040](architecture/adr/0040-vocabulaire-central-worker.md)) : plus de « nœud » ni de « cluster » dans le produit (interface, routes, configuration, schéma SQLite, JSON, outils MCP, projet `Encoding.WorkerAgent`, types C# et TypeScript).
- **Résidus du maillage retirés** : propriété des tâches, mode de cession `server`, clés d'identité et de données du central, coordinateur de session IA, panneaux de réplication, registre renommé `registers`.
- **Fonctions « cluster » retirées** : changement de central en deux phases, état « autonome », création du central.
- Migrations **0068 à 0074** (voir [storage.md](architecture/domains/storage.md)). Les anciennes données IA chiffrées sont supprimées par la 0070 ; un fichier d'identité de worker écrit avant le renommage est relu tel quel.
- Documentation vivante réécrite ([workers.md](architecture/domains/workers.md), [storage.md](architecture/domains/storage.md), règles, README, exploitation). Les ADR, incidents et revues de conception datés restent dans l'ancien vocabulaire.

Non vérifié :
- **La CI de promotion** et **les E2E Playwright** sur ce lot (adaptés mais non lancés), **la release** (aucun lancement vu après la fusion de la PR #1 ; au besoin lancer `Release` à la main, `workflow_dispatch`).
- Les migrations 0068 à 0074 n'ont été essayées que sur une instance de test et des bases neuves : **sauvegarder `cluster.db` avant de mettre à jour une installation existante**, et renommer les variables d'environnement (`Node__*` devient `Worker__*`, `Cluster__*` devient `Central__*`).
- Une vraie conversation IA (le chiffrement des points de reprise a changé), un encodage réel (les outils média étaient absents des conteneurs de test), le navigateur de bout en bout.
- NuGet : contrôle des vulnérabilités non fait.

## Reste à faire, par ordre conseillé

1. **Promotion** : ouvrir la PR `dev` vers `main`, laisser tourner `promotion-ci`, vérifier la release et publier les deux images, puis activer le scénario de compatibilité (`ENCODING_TEST_PREVIOUS_WORKER_IMAGE`) dans `backend.yml`.
2. **Pages du worker** (prochain chantier produit). Le build worker (`ng build` configuration `worker`) remplace seulement `app.routes.ts` par `app.routes.worker.ts` ; les composants sont communs, mais les routes sont recopiées (risque de dérive). Proposition à confirmer page par page : une seule table de routes avec profils (`central`, `worker`) et une constante de build `APP_PROFILE` ; un service `AppProfile` expose des capacités et les pages partagées masquent actions et sections avec `@if` ; l'Activité d'un worker est un filtre par défaut sur ce worker ; un test d'architecture interdit d'importer du code propre au worker depuis `features/`. Question ouverte (**décision de Rémi**) : les actions autorisées sur le worker, par exemple annuler une tâche ou suspendre l'admission.

   | Page | Sur le worker |
   |---|---|
   | Vue d'ensemble | Ce worker : charge, tâches en cours, matériel |
   | Activité | Exécutions de ce worker, lecture seule |
   | Workers | Ce worker en détail, central en lecture seule, « quitter le central » |
   | Plugins & Outils | Ce que ce worker a installé et sait exécuter |
   | Paramètres | Adresse du central et nom du worker seulement |
3. **Worker et SSE** : l'interface du worker doit consommer le SSE du central (voir open-questions, point 10).
4. **Reliquats de vocabulaire** : classes CSS `node-*` des écrans Workers, fichier `node-identity.json` et dossier `encoding-node` de l'image, dossier d'archive `backend/tests/cluster-3-nodes`, noms de tests et commentaires. La clé `targetNodeId` des blocs `nodes-*` et les identifiants du plugin `nodes` sont définis par le dépôt `encoding-store`, à mettre à jour de son côté (et à y retirer l'option du mode « serveur »).
5. **Hygiène** : instructions du projet (hors dépôt) qui citent encore PostgreSQL ; `open-questions.md` à relire.
6. **Non vérifié en conditions réelles** (liste complète dans open-questions) : Landlock en production, GPU réel, Plex/Jellyfin/*ARR réels, NAS/NFS.

## Reprendre l'environnement

Piles de test (Docker), à exécuter en CI ou sur un environnement distant dédié, jamais sur le poste local :
```
cd backend
CENTRAL_API_PORT=5082 docker compose -f docker-compose.central.yml up -d --build
CENTRAL_API_PORT=5082 docker compose -f docker-compose.central.yml down -v   # remise à zéro
```
- Central : http://localhost:5082 (port interne 8080). Worker : http://localhost:5081 (port interne 8081).
- Dans l'assistant du worker, l'adresse du central est `http://server:8080` (réseau Docker du compose). L'IP LAN de l'hôte ne répond pas depuis le conteneur quand `ufw` est actif (`INPUT DROP`) ; entre deux machines distinctes, utiliser l'adresse LAN du central et son port publié.
- `down -v` supprime seulement les volumes nommés du projet ; les médias de test (`/home/remi/encoding-tests/*`, montés en bind) ne doivent jamais être supprimés. Adapter ces chemins sur une autre machine (`docker-compose.central.yml`).
- Le central est initialisé par l'interface (`/setup`) ; ne pas laisser de mot de passe de test dans le dépôt.

Vérifications : suivre strictement le profil local de TEST_RULES.md, section 8. En local, seuls les contrôles statiques légers ciblés sont autorisés, un à la fois, avec `timeout 15s nice -n 19`. Aucune compilation, aucun typecheck, aucun test unitaire ou scénario.
- Frontend (`frontend/`) : lint TypeScript (`npm run lint`, qui lance `tsc`), tests, compilation Angular et E2E sont réservés aux runners GitHub Actions. Le format peut être vérifié localement sur les seuls fichiers modifiés.
- Backend (`backend/`) : la CI compile avec `-warnaserror` et le `.editorconfig` racine. Aucun test .NET ne doit être exécuté localement, même ciblé ou dans Docker. Compilation, restauration .NET et format .NET sont également réservés à la CI.
- Tests média (Runtime et Plugins) : exécutés en CI. `LoadStressTests` : workflow `stress.yml` en déclenchement manuel seulement.
- Tests lourds (**CI uniquement, jamais en local** — TEST_RULES.md, section 8) : `ENCODING_TEST_MULTIPROCESS=1` pour `WorkerProcessLossTests` (ffmpeg requis, environ 3,5 min) ; `ENCODING_TEST_DOCKER=1`, `ENCODING_TEST_CENTRAL_IMAGE`, `ENCODING_TEST_WORKER_IMAGE` pour `DockerCentralTests` (images avec ffmpeg : `.github/docker/Dockerfile.cluster-test` avec `BASE_IMAGE`).
- Ne pas contourner le profil local en compilant ou testant une copie dans `/tmp`.

Règles du dépôt à garder en tête : changements poussés directement sur `dev`, CI sur la PR de promotion `dev` vers `main` ; nouvelle route fermée par défaut ; migrations additives à numéro unique ; aucun secret dans les journaux ; décision structurante = ADR court, changement de comportement = fiche de domaine mise à jour (voir [AGENTS.md](../AGENTS.md)).
