# Publication des images Docker

## Parcours de développement et promotion

- Les changements sont poussés directement sur `dev` ; ces pushes ne déclenchent pas les workflows de validation. Quand les changements sont prêts à sortir, ouvrir une PR de `dev` vers `main`. La CI Backend, Frontend, Image et Release scripts s'exécute sur cette PR et ses mises à jour. La policy active automatiquement l’auto-merge de la PR ; GitHub ne la fusionne qu'après réussite du contrôle `promotion-ci`, qui attend les validations du commit exact de `dev` et des domaines inchangés. La fusion utilise « Merge » (un squash casserait le contrôle qui vérifie que `main` est contenue dans `dev`).
- La fusion de cette PR sur `main` déclenche le workflow de release. Celui-ci revérifie que les contrôles CI sont terminés et verts avant de publier sur Docker Hub ; ce push de `main` lance la publication, pas les workflows de validation. La PR de promotion échoue explicitement si `main` contient un commit hors fusion absent de `dev`, par exemple un commit direct (fusionner `main` dans `dev` ; ne jamais rebaser). Les fusions de promotion précédentes sont attendues sur `main` et ne bloquent pas. Un commit poussé directement sur `main` ne publie rien : le workflow de release échoue avec un message qui l'indique. Pour protéger `main`, exiger la PR et le contrôle `promotion-ci` dans les règles de branche, sans dérogation. Une PR de promotion peut être fermée ou son auto-merge désactivé si la release doit être reportée.

Pour un domaine inchangé, `wait-ci.mjs` recherche les contrôles sur les ancêtres compris entre sa dernière modification et le commit cible. Chaque candidat doit avoir exactement les mêmes fichiers dans les chemins de ce domaine (`git diff` vide, suppressions comprises). Cela permet de retrouver une validation exécutée sur la tête d’une PR après le dernier commit ayant modifié le domaine. Un contrôle présent sur le commit cible garde toujours la priorité, même en attente ou en échec ; seuls les succès autorisent la promotion. Les échecs et nouvelles tentatives restent distingués par contrôle et par commit.

La policy sérialise les événements d’une même PR et annule les exécutions remplacées. Si le commit attendu est déjà fusionné et que son contrôle `promotion-ci` a réussi, le contrôle est un succès idempotent : il ne tente pas une seconde fusion et ne signale pas une autorisation périmée. Un autre commit, une fermeture sans fusion ou un gate non réussi reste bloquant.

Le workflow `.github/workflows/release.yml` s'exécute à chaque push sur `main`. Il confirme qu'une PR `dev` vers `main` a été fusionnée et revérifie que les contrôles CI du commit (Backend, Frontend, Image et Release scripts) sont terminés et verts, puis publie sur Docker Hub. Ce workflow est la publication déclenchée par le merge, pas un lancement de la CI de validation :

- `<utilisateur>/encoding-central` et `<utilisateur>/encoding-worker` (`backend/Dockerfile`, cibles `central` et `worker`, contexte racine) : le central embarque l'API, l'interface, la base, un worker intégré et le lanceur ; le worker n'embarque que l'agent et son interface (voir [workers](../architecture/domains/workers.md)). Les deux images d'une même release portent la même version (`APP_VERSION`) : un worker n'est accepté que s'il a la version du central.

L'image reçoit les tags `X.Y.Z` et `latest`, et le commit reçoit le tag git `vX.Y.Z`.

## Secrets à créer

Dans GitHub : Settings > Secrets and variables > Actions > New repository secret.

- `DOCKERHUB_USERNAME` : nom d'utilisateur Docker Hub ;
- `DOCKERHUB_TOKEN` : jeton d'accès Docker Hub (Account settings > Personal access tokens, droits Read & Write).

## Numéro de version

Calculé par `.github/scripts/next-version.sh` à partir du dernier tag `vX.Y.Z` (départ 0.0.0) :

- patch par défaut ;
- minor : libellé de PR `release:minor` ou commit `feat:` / `minor:` ;
- major : libellé de PR `release:major`, commit `type!:` ou `major:`.

## Utiliser les images

La release publie **deux images** ([ADR 0036](../architecture/adr/0036-separation-stricte-central-worker.md)) : `<utilisateur>/encoding-central` (API, interface, base SQLite, worker intégré, lanceur) et `<utilisateur>/encoding-worker` (agent et interface du worker, sans base). Chacune porte la version `X.Y.Z` et `latest`.

```
docker run -d --name encoding-central -p 8080:8080 \
  -v encoding-node:/var/lib/encoding-node \
  <utilisateur>/encoding-central:latest

docker run -d --name encoding-worker -p 8081:8081 \
  -v encoding-worker:/var/lib/encoding-node \
  <utilisateur>/encoding-worker:latest
```

Le central fonctionne seul ; sa base est `/var/lib/encoding-node/cluster.db`. Il n'y a ni réplication ni maillage entre serveurs : la reprise après sinistre repose sur la sauvegarde du central. Le worker ne démarre jamais seul : son assistant `/setup` demande l'adresse du central et un compte administrateur de celui-ci ([ADR 0034](../architecture/adr/0034-rattachement-par-compte-administrateur.md)). Les volumes de données doivent être conservés pour garder l'identité des workers et les clés (`PluginSettings:KeyDirectory`).

Sur le central, la page **Workers** gère le worker intégré (adresse du central, départ) ; l'agent du même conteneur écoute en boucle locale (`LocalAgent__BaseUrl`, `http://127.0.0.1:8081/`) et exige le secret local créé par le lanceur. Le worker distant se pilote depuis le central.

## Images de test

Le workflow `.github/workflows/image-test.yml` publie **deux images de test** à chaque push vers `dev` (fusion de PR) :

- `<utilisateur>/encoding-central:test` et `<utilisateur>/encoding-worker:test` (étiquette flottante, toujours la dernière version testée) ;
- `<utilisateur>/encoding-central:test-<sha7>` et `<utilisateur>/encoding-worker:test-<sha7>` (étiquette immuable, 7 premiers caractères du commit) ;
- `APP_VERSION` vaut `test-<sha7>` pour les deux images du même commit.

Ces images sont **construites sans exécuter aucun test ni CI lourde** (docker/setup-buildx-action, docker/login-action, docker/build-push-action uniquement), pour une publication rapide. **NE PAS UTILISER EN PRODUCTION.**

Règle : **test = sans tests, ne pas utiliser pour une release**.

Le workflow utilise les mêmes secrets Docker Hub (`DOCKERHUB_USERNAME`, `DOCKERHUB_TOKEN`) que `release.yml`, avec des caches GHA distincts (`encoding-central-test` et `encoding-worker-test`). Il échoue proprement avec un message clair si les secrets sont manquants.

Déclenchement : push vers `dev` ou `workflow_dispatch`. Concurrency : un seul run à la fois par branche (`image-test-${{ github.ref }}`), avec `cancel-in-progress: false` pour éviter de publier un commit ancien après un récent.

## Notes de version et recherche de mises à jour

La release génère `backend/src/Encoding.Api/release-manifest.json` avant le build Docker.
Ce fichier est copié dans la sortie publiée de l’API : il contient la version `X.Y.Z`, le
commit, le dépôt GitHub et les notes de chaque tag stable accessible depuis le commit
publié. Les notes sont les titres des commits hors commits de fusion, regroupés entre
deux tags ; elles ne sont pas une synthèse éditoriale. Les anciennes versions restent
consultables sans réseau. Une construction locale utilise `0.0.0-dev` sans vérification distante.

Après publication de l’image et du tag, la CI crée la release GitHub `vX.Y.Z`, avec les
notes courantes et le manifeste en pièce jointe. Ce manifeste est aussi la source de
la recherche de mises à jour :
`https://github.com/<owner>/<repo>/releases/latest/download/release-manifest.json`.
Le dépôt et ses pièces jointes doivent être lisibles publiquement pour cette recherche ;
aucun jeton GitHub n’est embarqué dans l’image. Un dépôt privé, un accès réseau bloqué,
une release manquante ou un manifeste invalide donnent un état `unavailable`, jamais
un faux état « à jour ». Les requêtes sont bornées à 10 secondes et 2 Mio, avec cache
local de 10 minutes (y compris les erreurs). Le bouton de vérification relit ce cache.

La section **Mise à jour** (`/updates`, session viewer ou admin) affiche la version
installée du serveur local, la disponibilité d’une version stable plus récente et
les différentes notes. L’installation reste une mise à jour de l’image Docker par
l’opérateur ; cette section ne modifie pas les conteneurs ni les médias.

Contrats accessibles avec session ou jeton API Bearer :

- `GET /api/v1/system/version` → `{ "version": "X.Y.Z", "commit": "<sha>" }` ; lecture locale sans appel réseau.
- `GET /api/v1/system/updates` → `installedVersion`, `latestVersion`, `updateAvailable`, `status`, `checkedAt`, `releases`.
- Outils MCP `get_version` et `get_updates` (arguments `{}`), en lecture seule sans portée d’écriture.

`updateAvailable` vaut `null` quand aucune comparaison fiable n’est possible.
`status` vaut `development`, `unconfigured`, `unavailable`, `current` ou `available`.
Ces données décrivent l’installation du processus serveur qui exécute la requête,
pas une version commune : chaque worker doit avoir la même version que le central
([ADR 0039](../architecture/adr/0039-version-exacte-worker-central.md)).
