# Banc local avec deux projets Docker Compose

Ce banc lance un central et un worker externe dans deux projets Compose distincts,
sur le même hôte Docker. Le central garde son worker intégré et sa base SQLite ;
le worker externe ne porte aucune base. Les deux conteneurs communiquent par le
réseau externe `encoding-network`, avec l'alias DNS `encoding-central:8080`.
Ce réseau bridge ne relie pas deux machines physiques.

Ces fichiers sont réservés au test local. Les ports publiés écoutent uniquement
sur `127.0.0.1`. Le script initialise le premier administrateur avec le compte
**`admin` / `adminpassword123`** par défaut. Ce mot de passe générique satisfait la
longueur minimale de 12 caractères ; ne jamais le réutiliser en production.
L'authentification reste activée. Aucun identifiant administrateur n'est transmis
par les variables d'environnement des conteneurs.

## Fichiers et prérequis

| Fichier | Usage |
| --- | --- |
| `docker-compose.central.yml` | API, interface, SQLite et worker intégré |
| `docker-compose.worker.yml` | Worker externe CPU et son interface |
| `docker-compose.worker-gpu.yml` | Surcharge facultative du worker CPU pour Intel/AMD |
| `launch-docker.sh` | Construction séquentielle, lancement, initialisation et enrôlement |

Prérequis : Bash, Git, Docker avec Compose v2 ou supérieur, `curl`, `jq` et `nice`.
Les premières constructions téléchargent les dépendances et peuvent être longues.
Les images s'exécutent avec l'utilisateur non privilégié défini par le Dockerfile.

## Démarrer et contrôler

Depuis la racine du dépôt, avec les modifications suivies commitées :

```bash
./backend/launch-docker.sh start
./backend/launch-docker.sh status
```

Le script :

1. Crée le réseau s'il est absent.
2. Construit les cibles `central` puis `worker` avec `nice -n 19`, sans parallélisme,
   uniquement si leur image locale `sha-<SHA complet>` est absente. Les deux cibles
   reçoivent le même argument de construction `APP_VERSION=0.0.0-dev+<SHA court>`.
   La version est compilée ; une variable au démarrage ne la change pas.
3. Lance le central et attend `/health/ready`.
4. Crée le premier administrateur si nécessaire, sinon ouvre une session avec
   le compte fourni. Un compte existant n'est ni remplacé ni réinitialisé.
5. Lance le worker, ajoute l'adresse du central et l'enrôle avec la session
   administrateur via `/api/v1/local-worker/centrals` et `/enroll` de son relais.
   Un worker déjà enrôlé auprès de ce central est conservé ; un rattachement à
   un autre central provoque un échec sans départ automatique.
6. Vérifie l'enrôlement des deux workers, puis leur présence et leur état `online`
   dans l'API authentifiée du central. Un refus HTTP ou un délai dépassé fait
   échouer le script. `status` affiche seulement l'état des conteneurs.

Interfaces : central `http://127.0.0.1:5080`, worker `http://127.0.0.1:5081`.
La page `/workers` du central doit afficher son worker intégré et le worker externe.
Les cookies et corps HTTP privés restent dans un dossier temporaire de droits
restreints ; la session est fermée et ce dossier supprimé à la sortie. Ne pas
exécuter le script sous traçage shell ni publier des sorties de `compose config`
qui pourraient contenir un jeton de setup personnalisé.

## Paramétrer le banc

Le script lit `backend/.env.test` s'il existe, ou le fichier explicitement désigné
par `COMPOSE_ENV_FILE`. Il le passe à Compose sans l'exécuter comme un script shell.
Exemple de contenu, sans secret :

```dotenv
CENTRAL_API_PORT=5080
WORKER_UI_PORT=5081
```

`AUTH_SETUP_TOKEN` est facultatif pour protéger la première initialisation ;
le script reprend sa valeur résolue dans Compose pour l'appel de setup. Ne pas
versionner un fichier qui contient des secrets. Les ports de l'hôte n'affectent
pas la liaison interne, qui reste `http://encoding-central:8080`.

Pour un compte existant, fournir `ADMIN_USERNAME` et `ADMIN_PASSWORD` dans
l'environnement du script (pas dans `.env.test`). Le script ne les affiche pas.
Il conserve les volumes et les identités aux relancements. Les deux images
locales sont construites une seule fois par SHA et réutilisées ; ce mécanisme de
banc n'est pas une procédure de publication ou de promotion d'image.

Le compose central ne définit pas `Central__ApiBaseUrl`. Le lanceur force son
worker intégré à joindre l'API sur `127.0.0.1`, conformément à la PR #33.
`Central__AdvertisedApiBaseUrl` annonce seulement l'adresse du central aux workers
externes. Ne pas désactiver `Worker__WorkerImage` dans le compose worker : l'image
crée le secret local nécessaire à son relais authentifié.

## GPU Intel/AMD facultatif

Le compose CPU ne nécessite pas `/dev/dri`. Sous Linux, récupérer le GID du
périphérique de rendu et activer la surcharge :

```bash
export RENDER_GID="$(stat -c '%g' /dev/dri/renderD128)"
./backend/launch-docker.sh start --gpu
```

`RENDER_GID` peut aussi être défini dans `.env.test`. Le fichier GPU complète le
compose worker ; il n'ajoute pas de troisième conteneur ni de nouvelle identité.
`ENCODING_GPU_DEVICE` sélectionne un autre périphérique si nécessaire. Cette
surcharge concerne Intel/AMD ; NVIDIA exige le NVIDIA Container Toolkit et une
configuration adaptée. L'accès au périphérique ne prouve pas la disponibilité
d'un encodeur : vérifier les sondes dans l'interface après installation des outils.

## Données, arrêt et dépannage

Les chemins personnels `/home/remi/encoding-tests/*` sont remplacés par des volumes
Docker nommés initialement vides. Seul `encoding-test-shared-media` est partagé
entre central et worker ; les volumes privés et SQLite restent séparés. Alimenter
ces volumes uniquement avec des médias synthétiques de test. Les anciens dossiers
de médias de l'hôte ne sont pas montés. Les volumes de données préexistants de même
nom sont réutilisés : un ancien compte peut donc nécessiter ses identifiants.

```bash
./backend/launch-docker.sh stop
./backend/launch-docker.sh restart
# Avec le même mode GPU si activé :
./backend/launch-docker.sh stop --gpu
```

L'arrêt conserve tous les volumes et le réseau partagé. Le script ne supprime
aucune donnée et ne relance pas automatiquement après une erreur. En cas d'échec,
les conteneurs restent disponibles pour le diagnostic (`docker compose -p
encoding-central -f backend/docker-compose.central.yml logs --tail 100`, et le
projet `encoding-worker` avec son fichier pour l'agent). Ne pas publier de secrets
présents dans les journaux. Vérifier les ports occupés, le réseau, les identifiants
et les versions compilées. Le banc ne préinstalle pas les paquets du Store.

La validation syntaxique et les simulations du script ne prouvent pas un parcours
réel à deux conteneurs, un encodage ou le fonctionnement du GPU. Les tests .NET et
scénarios multi-workers complets sont réservés à la CI (`TEST_RULES.md`).

## Références

- [Central et workers](../docs/architecture/domains/workers.md)
- [Authentification et exploitation](../docs/architecture/domains/auth-ops.md)
- [Surveillance](../docs/operations/monitoring.md)
- [Sauvegarde et restauration](../docs/operations/backup-restore.md)
