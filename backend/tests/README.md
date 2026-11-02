# Vérifications du runtime

La suite utilise les vrais dépôts, des fichiers SQLite temporaires, les endpoints HTTP du runtime et le runner worker. Elle ne dépend d'aucun serveur de base externe ; chaque scénario supprime son fichier et ses médias synthétiques à la fin.

Avec .NET 10 :

```powershell
dotnet test backend/tests/Encoding.Runtime.Tests --configuration Release
```

La suite utilise xUnit (`dotnet test`). Les scénarios de base s’exécutent toujours sur SQLite. FFmpeg, ffprobe et MediaInfo sont nécessaires aux scénarios média ; `backend/tests/Dockerfile` fournit le SDK et ces outils. La CI exécute toute la suite, exige les outils média, collecte la couverture et conserve les résultats `.trx` en artefact.

Depuis la racine du dépôt, avec Docker :

```powershell
docker build -t encoding-runtime-tests -f backend/tests/Dockerfile backend/tests
docker run --rm -v "${PWD}:/source" -w /source encoding-runtime-tests dotnet test backend/tests/Encoding.Runtime.Tests --configuration Release
```

Les scénarios média encodent une vidéo synthétique de 1 seconde en H.265 : une fois avant le relais, et une fois en transférant le plan avant son exécution. Le service source publie réellement le résultat distinct et les tests vérifient que la source reste intacte.

La suite couvre aussi les conditions structurées : groupes ET / OU, types et données absentes, limites de taille/profondeur, instantanés d'origine/courant, disponibilité du plugin et trois branches du runner avant téléchargement. Le parcours média vérifie une condition H.265 courant ET H.264 d'origine après conversion.

Le testeur de flow est couvert sans lancer de traitement : branches et reconvergence, probes/variables, résultats fictifs de transformation, conservation de l’origine, relais avec paramètres hérités, plan non exécuté, contrats HTTP et erreurs de profil. Un fichier synthétique vérifie MediaInfo et l’intégrité de la source ; saturation et annulation vérifient la libération des ressources.

La suite vérifie également les publications inchangées et concurrentes, l’absence de nouvelles sauvegardes, la concurrence des brouillons, les modifications réelles, la version exacte retournée par l’API et la conservation des versions identiques lors d’un import d’archives.

Les tests de remplacement de source (fichiers, politique, période de grâce, restauration) et de perte d’un worker couvrent les règles de l’architecture 0030. Le remplacement réel d’une source H.265 puis sa restauration sont aussi vérifiés avec FFmpeg. Les scénarios de maillage utilisent deux bases SQLite indépendantes et vérifient la convergence sans quorum.

Le maillage est aussi vérifié à trois serveurs (`ClusterMeshConvergenceTests`) : écritures concurrentes pendant une partition totale, échanges dans un ordre aléatoire à graine fixe avec doublons, rattrapage d'un serveur par un relais sans l'auteur d'origine, événements d'un type inconnu conservés et relayés, lots invalides refusés en bloc. Côté exécution, une publication demandée avant la perte du worker est adoptée par le worker de reprise sans être refaite, et une exécution répliquée vers un autre serveur y reste visible sans jamais y être attribuable (baux et réservations restent locaux).

Le test multi-processus `WorkerProcessLossTests` démarre un vrai serveur et deux vrais workers (enrôlement local, bibliothèque en accès direct, flow d'encodage H.265 d'un média synthétique), tue le worker en plein ffmpeg (SIGKILL) et vérifie qu'après l'expiration du bail l'autre worker reprend la même exécution, sans doublon, que la source est inchangée et qu'aucun fichier parasite n'apparaît dans la bibliothèque. Il est ignoré par défaut (plusieurs minutes, ffmpeg et Linux requis) : `ENCODING_TEST_MULTIPROCESS=1 dotnet test backend/tests/Encoding.Runtime.Tests --filter WorkerProcessLossTests`. La CI l'exécute dans le job `backend-multiprocess-tests`.

Deux tests utilisent de vrais conteneurs de l'image `encoding` sur un réseau Docker (`DockerClusterTests`, ignorés par défaut). Le premier démarre un serveur et deux workers (utilisateur non-root, lanceur, enrôlement par fenêtre ouverte), fait un `docker kill` du conteneur qui encode puis vérifie la reprise par l'autre conteneur et l'intégrité de la source. Le second fait rejoindre le serveur récent par un worker de la dernière image publiée (`mrcryllix/encoding:latest`) et vérifie qu'il exécute un flow : compatibilité entre versions de nœuds. Local : construire l'image (`docker build -f backend/Dockerfile -t encoding:test .`) puis `ENCODING_TEST_DOCKER=1 ENCODING_TEST_IMAGE=encoding:test ENCODING_TEST_PREVIOUS_IMAGE=<image précédente> dotnet test backend/tests/Encoding.Runtime.Tests --filter DockerClusterTests`. La CI les exécute dans le job `backend-docker-cluster-tests`.

## Frontend

`npm test` (dans `frontend/`) exécute les tests de logique pure (`node --test`) puis les tests de composants Angular (Vitest, `*.spec.ts`, via `ng test`). `npm run test:e2e` lance le parcours E2E léger Playwright (bureau et mobile, API simulée, vérifications d’accessibilité axe) ; il ne requiert ni backend ni média. En CI, les rapports et traces sont conservés en cas d’échec. Exception temporaire documentée : la règle axe `color-contrast` est désactivée dans `e2e/smoke.spec.ts` tant que les jetons de thème de la vue d’ensemble ne sont pas corrigés (12 éléments signalés).

Les attentes durables (0033) sont vérifiées sur SQLite : libération du worker et réservation conservée, reprise à l'étape suivante, délai maximal avec et sans sortie « Délai dépassé », évaluations concurrentes, événements serveur (charge, état d'un worker), sondes de plugin protégées par l'époque, runner réel et annulation.

## Plugins et déclencheur d’import

`backend/tests/Encoding.Plugins.Tests` vérifie les règles de correspondance, les webhooks, le Store, les comportements média C# et le déclencheur sur SQLite. Les manifestes Store utilisés par les scénarios runtime sont des fixtures explicites sous `backend/tests/Fixtures/StoreCatalog`; le registre de production ne les charge pas par défaut. Le test de processus Sonarr a été retiré avec le code du plugin, dont les tests de source vivent dans le dépôt Store :

```powershell
dotnet run --project backend/tests/Encoding.Plugins.Tests --configuration Release
```

Les tests des plugins Python et du SDK sont maintenus dans le dépôt public Encoding Store. Ils utilisent un faux service HTTP local et aucun service personnel.

## Charge et contention SQLite

`LoadContentionTests` tourne à chaque PR (quelques secondes) : une lecture cohérente (`Snapshot`) ne bloque jamais un écrivain, heartbeats, aperçu des exécutions et récupération partagent une grosse file sans erreur `database is locked`, et un heartbeat retardé ne fait échouer aucune exécution en file alors qu'un nœud réellement perdu le fait. Les bornes de latence sont larges : elles détectent une régression, pas une performance précise.

`LoadStressTests` (`Category=Stress`) est exclu des PR et lancé chaque nuit par `.github/workflows/stress.yml` (ou à la demande) : 20 000 exécutions, 30 nœuds, 60 s par défaut (`ENCODING_STRESS_EXECUTIONS`, `ENCODING_STRESS_NODES`, `ENCODING_STRESS_SECONDS`). En local : `dotnet test backend/tests/Encoding.Runtime.Tests --filter "Category=Stress"`.
