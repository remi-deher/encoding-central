# Publication des images Docker

## Parcours de développement et promotion

- Les changements sont poussés directement sur `dev` ; ces pushes ne déclenchent pas les workflows de validation. Quand les changements sont prêts à sortir, ouvrir une PR de `dev` vers `main`. La CI Backend, Frontend, Image et Release scripts s'exécute sur cette PR et ses mises à jour. La policy active automatiquement l’auto-merge de la PR ; GitHub ne la fusionne qu'après réussite du contrôle `promotion-ci`, qui attend les validations du commit exact de `dev` et des domaines inchangés. La fusion utilise « Merge » (un squash casserait le contrôle qui vérifie que `main` est contenue dans `dev`).
- La fusion de cette PR sur `main` déclenche le workflow de release. Celui-ci revérifie que les contrôles CI sont terminés et verts avant de publier sur Docker Hub ; ce push de `main` lance la publication, pas les workflows de validation. La PR de promotion échoue explicitement si `main` contient un commit hors fusion absent de `dev`, par exemple un commit direct (fusionner `main` dans `dev` ; ne jamais rebaser). Les fusions de promotion précédentes sont attendues sur `main` et ne bloquent pas. Un commit poussé directement sur `main` ne publie rien : le workflow de release échoue avec un message qui l'indique. Pour protéger `main`, exiger la PR et le contrôle `promotion-ci` dans les règles de branche, sans dérogation. Une PR de promotion peut être fermée ou son auto-merge désactivé si la release doit être reportée.

Pour un domaine inchangé, `wait-ci.mjs` recherche les contrôles sur les ancêtres compris entre sa dernière modification et le commit cible. Chaque candidat doit avoir exactement les mêmes fichiers dans les chemins de ce domaine (`git diff` vide, suppressions comprises). Cela permet de retrouver une validation exécutée sur la tête d’une PR après le dernier commit ayant modifié le domaine. Un contrôle présent sur le commit cible garde toujours la priorité, même en attente ou en échec ; seuls les succès autorisent la promotion. Les échecs et nouvelles tentatives restent distingués par contrôle et par commit.

La policy sérialise les événements d’une même PR et annule les exécutions remplacées. Si le commit attendu est déjà fusionné et que son contrôle `promotion-ci` a réussi, le contrôle est un succès idempotent : il ne tente pas une seconde fusion et ne signale pas une autorisation périmée. Un autre commit, une fermeture sans fusion ou un gate non réussi reste bloquant.

Le workflow `.github/workflows/release.yml` s'exécute à chaque push sur `main`. Il confirme qu'une PR `dev` vers `main` a été fusionnée et revérifie que les contrôles CI du commit (Backend, Frontend, Image et Release scripts) sont terminés et verts, puis publie sur Docker Hub. Ce workflow est la publication déclenchée par le merge, pas un lancement de la CI de validation :

- `<utilisateur>/encoding` (`backend/Dockerfile`, contexte racine) : image unique contenant le serveur, l'interface web, le worker et le lanceur qui les démarre selon le mode du nœud (voir [cluster](../architecture/domains/cluster.md)).

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

## Utiliser l'image

Sans configuration de base de données, l'image utilise SQLite dans le dossier de données (`/var/lib/encoding-node/cluster.db`). Chaque serveur garde son propre fichier ; les données se réconcilient par le [maillage de réconciliation](../architecture/domains/replication-storage.md) :

```
docker run -d --name encoding -p 8080:8080 \
  -e Node__Roles=server,worker \
  -v encoding-node:/var/lib/encoding-node \
  <utilisateur>/encoding:latest
```

SQLite est le seul fournisseur de données. Le fichier est `/var/lib/encoding-node/cluster.db` et reste propre à chaque nœud. La clé de données du cluster est générée lors de sa création et transmise chiffrée à chaque serveur approuvé ; aucune variable de secret de réconciliation n’est à configurer. Les identités privées des serveurs et la clé locale Data Protection doivent rester dans le volume `PluginSettings:KeyDirectory` (`/var/lib/encoding/plugin-keys` dans Compose). Les pairs mDNS du même cluster sont découverts automatiquement. La découverte mDNS ne traverse pas toujours les réseaux Docker/VLAN : ajoutez alors des adresses joignables avec `Cluster__Reconciliation__Peers__0`, `__1`, etc., et ouvrez le port API entre les serveurs. `Cluster__Reconciliation__AdvertisedApiBaseUrl` sert à annoncer l'adresse de pair joignable ; il est distinct de `Cluster__AdvertisedApiBaseUrl`, qui peut être une adresse interne réservée aux workers locaux. Dans Docker Compose, renseignez au besoin `CLUSTER_RECONCILIATION_ADVERTISED_API_BASE_URL` et `CLUSTER_RECONCILIATION_PEER_0`. Pour utiliser l'exemple Compose entre plusieurs hôtes, définissez aussi `CLUSTER_API_BIND_ADDRESS` sur l'adresse réseau à laquelle le port 5080 doit être publié ; il reste lié à la boucle locale par défaut. La page Nœuds → Cluster affiche l'état de découverte et les erreurs de synchronisation ; `GET /api/v1/cluster/reconciliation/status` expose le même diagnostic aux utilisateurs authentifiés. Cette version attend une base de données SQLite fraîche : réinitialisez les bases existantes avant le redémarrage, comme prévu pour cette installation.

`Node__Roles` (`server`, `worker` ou `server,worker`) donne le mode initial. Ensuite le mode se change depuis la page Nœuds (« Ce nœud et son cluster » → « Mode du nœud »), sans redémarrer le conteneur ; le choix est conservé dans le dossier de données du nœud. Un worker seul doit définir `Cluster__ApiBaseUrl` (ou être découvert sur le réseau) ; le volume de données doit être conservé pour garder l'identité du nœud et son mode.

## Gérer ce nœud depuis la page Nœuds

Sur un nœud qui héberge le serveur, tout se gère depuis la page **Nœuds → Ce nœud et son cluster** : mode, adresse d'un cluster (`http://<serveur>:8080`, utile quand la découverte DNS-SD ne traverse pas le réseau), clé de jumelage, rejoindre, quitter. Le serveur relaie ces actions vers l'agent du même conteneur (`LocalAgent__BaseUrl`, par défaut `http://127.0.0.1:8081/`) derrière la session administrateur et avec le secret local créé par le lanceur. L'agent écoute uniquement en boucle locale, exige le secret pour toutes ses routes de gestion et n'utilise plus `Origin` ni CORS : retirer les anciens ports 8081 publiés et le réglage `WorkerUi__Origins` lors de la mise à jour. La gestion par navigateur d'un nœud nécessite la fonction serveur locale ; un nœud worker seul se pilote depuis son cluster, ou via le canal interne authentifié. Hors image unique, le serveur et l'agent du même hôte doivent partager explicitement le fichier `Node__LocalEnrollmentSecretFile`.

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
pas une version commune réconciliée du cluster ; dans le mode Raft optionnel, le
routage habituel des POST MCP vers le leader reste applicable.
