---
id: 0048
title: Réécriture du backend en Rust — cadrage
status: proposed
domain: workers, storage, runtime, plugins, auth-ops
date: 2026-11-08
decided_by: à décider (principe de la réécriture décidé par Rémi le 2026-11-08 ; le cadrage ci-dessous attend sa décision)
supersedes: []
superseded_by: []
tags: [rust, réécriture, contrats, sqlite, migration, lots]
---

# 0048 — Réécriture du backend en Rust — cadrage

## Contexte
Rémi a décidé de réécrire en Rust l'intégralité du backend : central (API, worker intégré, lanceur), agent worker, exécution des composants natifs, outils (ticket #36). L'interface reste en Angular ; son passage éventuel à Rust sera décidé plus tard. Le backend actuel compte environ 47 000 lignes de C# (`Encoding.Api` 30 700, `Encoding.WorkerAgent` 9 500, `Encoding.Contracts` 4 900, `Encoding.Core` 1 400, `Encoding.Launcher` 200), environ 20 600 lignes de tests .NET, 44 migrations SQLite et environ 300 routes HTTP. Cet ADR **ne décide pas** de la réécriture, qui est déjà décidée. Il cadre comment la faire sans casser les invariants d'[AGENTS.md](../../../AGENTS.md) : options, recommandation et risques pour chacun des huit points demandés. **Aucun code Rust** et aucune modification d'`AGENTS.md`, de `BACKEND_RULES.md` ni de `TEST_RULES.md` avant que cet ADR soit accepté.

Mesures et chiffres : comptage de lignes sur `test` au commit `edf6ddb` (fichiers `*.cs` hors `obj/` et `bin/`) ; nombre de routes approximé par les appels `Map{Get,Post,Put,Delete,Patch}`.

## Décision proposée (à valider point par point)

### 1. Stratégie : progressive par composant, en commençant par l'agent worker

| Option | Pour | Contre |
|---|---|---|
| A. Réécriture d'un bloc (tout le backend, une bascule) | Pas de cohabitation ; un seul langage dans la cible | Gel fonctionnel long ; aucune validation réelle avant la fin ; régressions découvertes toutes ensemble ; retour arrière = tout ou rien |
| **B. Progressive par composant** : worker d'abord, puis le central d'un seul tenant | Chaque lot est livrable et validé sur le banc NAS ; contrats déjà nets entre worker et central (baux, SSE, WebSocket, Store) ; le C# sert d'oracle | Deux langages dans le dépôt pendant la transition ; il faut prouver les contrats |
| C. Progressive par route du central (deux processus, proxy) | Bascule fine | **Incompatible avec l'écrivain unique** : deux processus écriraient le même fichier SQLite sans file commune ([ADR 0009](0009-ecrivain-sqlite-unique-par-noeud.md), [storage](../domains/storage.md)) ; état en mémoire partagé (hubs SSE et télémétrie, [ADR 0041](0041-sse-a-la-place-du-sondage.md), [0045](0045-websocket-telemetrie-worker.md)) |

**Recommandation : B.** L'ordre est le suivant : l'agent worker, puis le central en **une seule bascule**, avec son API, son worker intégré (l'agent Rust du lot 1) et son lanceur. Le central n'est jamais découpé entre deux processus.

**Compatibilité avec l'[ADR 0036](0036-separation-stricte-central-worker.md)** : rien ne change au modèle. Le central reste l'unique autorité et embarque son worker ; le worker reste sans base et est piloté par le central.

**Compatibilité avec l'[ADR 0039](0039-version-exacte-worker-central.md)** : la version est celle de l'image (`APP_VERSION`), pas celle du langage. Une image worker Rust et une image central .NET construites **depuis le même commit** portent la même version (`test-<sha7>`, `dev-<sha7>`, version de release). La cohabitation reste donc dans la règle tant que les deux images d'une version sortent du même commit.

**Retour arrière** : republier la paire d'images de la release précédente, qui est aussi la règle actuelle. Un worker .NET et un worker Rust ne coexistent jamais sous la même version : chaque version publiée choisit une seule implémentation par image.

### 2. Contrats conservés et prouvés
Contrats figés pendant toute la transition : l'API REST (`/api/v1/**`, codes HTTP, corps JSON en camelCase, codes d'erreur comme `sqlite_write_unavailable`, `store_index_unreadable`, `worker_lost`), les SSE (`/api/v1/live`, `…/wake`, `/api/v1/store/events` avec ses identifiants `v2.{rev}`, télémétrie), le WebSocket de télémétrie ([0045](0045-websocket-telemetrie-worker.md)), l'enrôlement et le jumelage ([0034](0034-rattachement-par-compte-administrateur.md)), l'en-tête `X-Node-Token`, le protocole des plugins hors processus (`describe`/`execute`, `contractVersion` 1) et le format de `repository.json`.

| Option | Valeur |
|---|---|
| A. Relecture manuelle | Insuffisante : environ 300 routes et des détails de sérialisation |
| B. Comparaison du document OpenAPI | Utile (le central en génère un, `OpenApiEndpoints`) mais ne vérifie pas les comportements |
| **C. Tests de contrat exécutables contre les deux implémentations** | Prouve le comportement |

**Recommandation : C + B.**
- (1) Un **instantané OpenAPI** tiré du .NET au lot 0 ; la CI compare celui de l'implémentation Rust (routes, méthodes, schémas).
- (2) Une **suite de contrat HTTP en boîte noire**, indépendante du langage (requêtes et réponses attendues, codes, en-têtes, trames SSE et WebSocket). Elle est écrite au lot 0, à partir des scénarios .NET existants (`AuthScenarios`, `WorkerStoreEventsTests`, catalogue des routes anonymes…), et exécutée en CI contre le .NET puis contre le Rust.
- (3) Des **scénarios mixtes** : le central .NET avec le worker Rust (lot 1), puis le central Rust avec le worker Rust, dans le harnais multi-conteneurs (`DockerCentralTests`).
- (4) Le **frontend Angular** consomme ce contrat : ses modèles (`shared/models`) et des E2E légers servent de vérification côté client.

Point de vigilance : **sérialisation**. `System.Text.Json` écrit les dates `DateTimeOffset` avec un nombre variable de décimales (`…25.81+00:00`), les énumérations, les `null` et l'ordre des champs à sa façon. Le `serde` côté Rust doit reproduire ce qui est observable, ou le frontend doit être vérifié tolérant. Les tests de contrat doivent couvrir ces formats.

### 3. Base de données

**État honnête des incidents** :

| Incident | Cause | Moteur ou code ? |
|---|---|---|
| Traducteur PostgreSQL → SQLite par expressions régulières, retiré par l'[ADR 0043](0043-sql-sqlite-natif.md) | SQL écrit au dialecte PostgreSQL et réécrit à l'exécution | **Code** (héritage du support PostgreSQL), pas une limite de SQLite |
| 503 `sqlite_write_unavailable` sur `GET /api/v1/executions` (#32) | La validation de session ouvrait une transaction d'écriture à chaque requête, avant de lire ; l'admission a dépassé 5 s pendant un long traitement | **Code**, corrigé par les PR #50 et #51. Le modèle à un seul écrivain l'a rendu visible, mais un autre moteur aurait seulement masqué l'écriture inutile |
| Attentes de verrou des requêtes de présence et d'attribution, qui ont motivé l'[ADR 0009](0009-ecrivain-sqlite-unique-par-noeud.md) | Des tâches de fond ouvraient des transactions d'écriture sans travail ; écritures concurrentes directes | **Mixte** : le moteur n'admet qu'un écrivain (propriété voulue), et le code tenait le verrou sans raison. L'écrivain unique et ses priorités traitent la partie code |
| Détentions longues de l'écrivain pendant le film de 726 Mo (#32) | **Inconnue** | **Non mesuré** : les compteurs `encoding_sqlite_writer_long_holds_total`, `…_hold_seconds_total`, `…_expired_total` et `encoding_sqlite_best_effort_abandoned_total` n'ont jamais été relevés sur un rejeu long |

Constat d'exploitation du 2026-10-21 : aucune erreur `database is locked` dans les journaux des deux hôtes ([storage](../domains/storage.md)). **Aucune donnée ne montre aujourd'hui une limite du moteur SQLite pour la charge d'un central unique.** Les incidents connus viennent du code, et le seul point ouvert n'est pas mesuré.

**Options** :

| Option | Pour | Contre |
|---|---|---|
| **A. SQLite, même fichier, même schéma** | Aucune migration de données ; un seul conteneur ; sauvegarde par copie de fichier conforme à la reprise par restauration ; principe validé ([AGENTS.md](../../../AGENTS.md), [0043](0043-sql-sqlite-natif.md)) | Un écrivain à la fois, à servir par une file dans le code |
| B. PostgreSQL | Écrivains concurrents, verrous de ligne, outils d'exploitation | Un service de plus (conteneur, mises à jour, sauvegarde `pg_dump`, mot de passe) pour un central sans haute disponibilité ; 44 migrations et environ 300 requêtes à réécrire ; migration de données obligatoire ; déjà écarté par l'[ADR 0009](0009-ecrivain-sqlite-unique-par-noeud.md) faute de preuve ; aucun gain démontré |
| C. libSQL / Turso (fork de SQLite à écrivains concurrents) | Fichier compatible SQLite | Concurrence d'écriture encore expérimentale ; fork d'un fournisseur unique ; ne répond à aucun incident mesuré |
| D. Stockage clé-valeur embarqué (redb, sled) ou DuckDB | Embarqué | Pas de SQL relationnel transactionnel adapté (clé-valeur) ou orienté analyse (DuckDB) : réécriture complète de la persistance, sans gain |

**Recommandation : A, sans changement de moteur.** Le changement de moteur pourra être réexaminé sur **mesures** : relever les compteurs de l'écrivain sur un rejeu long (demandé dans #32). Critère proposé pour rouvrir la question : après correction des transactions longues identifiées, des expirations d'admission ou des détentions de plus d'1 s qui restent régulières sous charge normale.

**Bibliothèque Rust** :
- `sqlx` : asynchrone. Sa vérification hors ligne des requêtes (`sqlx prepare`) prend le relais de `SqlStatementsPrepareTests`. Un pool d'écriture à **une** connexion, plus une petite file à priorités pour la présence et les baux, reproduit `SqliteWriter`. C'est cohérent avec le skill `rust-conventions-backend`.
- `rusqlite` : synchrone, appelé depuis un thread dédié. L'écrivain unique s'y exprime naturellement, mais au prix d'un pont async à écrire et sans vérification des requêtes à la compilation.
- **Recommandation** : `sqlx`. Une maquette mesurée au lot 0 doit valider la file à priorités avant d'engager le central.

**Reprise des données** : le central Rust ouvre le `cluster.db` existant. Il garde le dossier `backend/migrations/sqlite/` et la table `schema_migrations` avec ses empreintes SHA-256 (même vérification au démarrage, numérotation qui continue à 0078). Les formats sont portables tels quels : instants en texte UTC de largeur fixe et JSON en texte ([0043](0043-sql-sqlite-natif.md)), mots de passe PBKDF2-HMAC-SHA512 à 210 000 itérations, sessions et jetons hachés en SHA-256.

**Point bloquant** : les secrets chiffrés par **ASP.NET Data Protection** ne sont pas lisibles par une implémentation Rust telle quelle. Sont concernés la configuration des plugins (`Encoding.PluginConfiguration.v1`), les clés et jetons IA (`Encoding.Ai.*`), la session d'installation Plex et les actions de l'assistant ; leur trousseau de clés est dans `PluginSettings:KeyDirectory`.
- Option 1, **recommandée** : une dernière version .NET réchiffre ces valeurs dans un format documenté (AES-256-GCM, clé dans le dossier de clés). C'est une migration de données, faite une fois, testée et sauvegardée avant.
- Option 2 : réimplémenter le format Data Protection en Rust. Fragile, non documenté comme contrat stable.
- Option 3 : demander de ressaisir les secrets. Perte de configuration pour l'utilisateur.

### 4. Écrivain unique, baux, réservations, reprise, fichiers, transferts
Ces invariants sont **portés à l'identique**, sans les réinventer ([runtime](../domains/runtime.md), [0009](0009-ecrivain-sqlite-unique-par-noeud.md), [0044](0044-echec-apres-perte-de-bail.md)) :
- **Transactions** : une transaction d'écriture est admise une à la fois puis ouverte en `BEGIN IMMEDIATE` ; la présence et les baux sont prioritaires ; les lectures se font sans admission (instantané ou `SELECT` simple, #32).
- **Baux** : époque et fencing, aucune réattribution silencieuse, échec `worker_lost` après expiration, reprise uniquement par une nouvelle exécution.
- **Fichiers** : réservations et verrous de disque ; sortie écrite à côté puis validée, publication atomique sans écraser la source ; transferts WebDAV avec contrôle d'empreinte.

**Options** :
- Traduction fidèle, module par module, avec les tests actuels comme spécification : **recommandé**.
- Réécriture « idiomatique » qui reconçoit ces mécanismes : écartée, car elle reviendrait à rouvrir des décisions de Rémi sans ADR.

Rust apporte des types dédiés (`Epoch`, `ExecutionId`, états d'exécution sous forme d'`enum` exhaustif), qui rendent certaines erreurs d'invariant impossibles à compiler.

**Risques** : invariants répartis dans environ 30 000 lignes, dont certains seulement documentés par des tests. Un comportement non testé se perd sans bruit. **Garde-fou** : chaque lot porte d'abord, en tests de contrat ou de comportement, les scénarios .NET du domaine (`Harness.Failover`, `WorkerProcessLossTests`, `Harness.LegacyResumptions`…), puis rejoue le banc NAS (matrice, S6, perte de worker).

### 5. Plugins hors processus, Landlock, outils média, capacités
- **Plugins tiers** (Python, Node) : **inchangés**. Le protocole stdin/stdout `contractVersion` 1 est un contrat ; les SDK et le dépôt `encoding-store` ne bougent pas. Les plugins officiels « natifs » (`base`, `video`, `audio`, `subtitles`, `mkv`…) ne sont que des manifestes : leur **exécution** est codée dans le worker (`MediaActions`, environ 9 500 lignes avec l'agent) et migre avec le lot worker.
- **Bac à sable** : Landlock et seccomp ([0042](0042-reseau-plugins-capacite-et-seccomp.md), [plugins](../domains/plugins.md)) s'écrivent avec des bibliothèques Rust maintenues : `landlock`, du mainteneur du noyau, et `seccompiler`, de rust-vmm/Firecracker. **Recommandation** : garder un **binaire relais séparé** (équivalent de `--isolated-process`, qui applique le confinement puis fait `execve`) plutôt qu'une fermeture `pre_exec` dans un processus tokio multithread, où seules des opérations sûres pour les signaux sont permises. Le principe « bac à sable indisponible, jamais dégradé » reste.
- **Outils média** : FFmpeg, ffprobe et MKVToolNix restent des processus externes, lancés par `tokio::process` avec délais et capture bornée. La détection des capacités (encodeurs vérifiés, accélération matérielle) est portée telle quelle. Les corrections récentes doivent être reprises avec leurs tests : #24 et #28 (dispositions), #25 (`+genpts`), #26 (`worker_lost`).
- **Risque** : `MediaActions` concentre beaucoup de règles fines, prouvées par le banc. La parité se démontre en rejouant la matrice NAS sur le worker Rust avant de basculer l'image.

### 6. Authentification, sessions, jetons, secrets, routes fermées
Le schéma est porté **à l'identique** ([auth-ops](../domains/auth-ops.md)) : cookie opaque `encoding_session` (HttpOnly, SameSite=Strict) haché en SHA-256, délais d'inactivité et absolu, en-tête CSRF `X-Encoding-Csrf`, jetons `enc_pat_` avec portées, jetons de worker, limitation des routes anonymes, rôles `admin` et `viewer`.

**Routes fermées par défaut** :
- Option 1 : l'autorisation est une couche `tower` appliquée à **tout** le routeur, qui refuse par défaut. Une route ne s'ouvre que par un marqueur explicite (anonyme, lecteur, jeton de worker, rapport du worker…), équivalent de `ManagementAccessHandler`. **Recommandé.**
- Option 2 : des vérifications dans chaque gestionnaire. **Écartée**, car l'oubli ouvre la route.

Le test qui énumère les routes anonymes est porté tel quel. Secrets : voir le point 3, Data Protection. Aucune journalisation de secret (`tracing` avec champs filtrés). **Relecture sécurité obligatoire** pour chaque lot qui touche à l'authentification.

### 7. CI, images, flux test → dev → main, tests
- **Flux** ([0047](0047-cycle-test-dev-main-et-images-par-branche.md)) : inchangé (`test` : images seulement ; `dev` : CI légère ; `main` : CI complète). Ajouts proposés :
  - en CI légère sur `dev` : `cargo fmt --check`, `cargo clippy -D warnings`, `cargo build --locked` et des contrôles de frontières entre crates (équivalent de l'[ADR 0031](0031-frontieres-backend-verifiees-par-tests.md) : `cargo deny`, dépendances autorisées entre crates de l'espace de travail) ;
  - en CI complète sur `main` : `cargo test`, la suite de contrat contre l'implémentation livrée, et les scénarios multi-conteneurs.
- **Images** : construction en plusieurs étapes (compilation Rust, puis image d'exécution Ubuntu inchangée avec FFmpeg, MKVToolNix et pilotes GPU) ; `APP_VERSION` injectée à la compilation. L'image n'embarque plus le runtime ASP.NET. Le gain de taille est attendu mais **non mesuré**.
- **Règles de test locales** : `TEST_RULES.md` interdit les tests .NET locaux. Pour Rust, la règle est à décider avec l'ADR : proposition = mêmes limites (en local : `fmt`, `clippy`, `check` et tests ciblés légers avec `nice -n 19` ; suites lourdes en CI), comme le dit déjà le skill `rust-conventions-backend`.
- **Pendant la transition**, les deux chaînes coexistent dans la CI : `dotnet` reste l'oracle tant qu'un composant n'est pas basculé.

### 8. Lots ordonnés, estimation, critères de fin

| Lot | Contenu | Reste en .NET | Estimation grossière (équivalent temps plein) |
|---|---|---|---|
| 0. Socle | Acceptation de cet ADR et des décisions ouvertes. Instantané OpenAPI et suite de contrat en boîte noire, verte contre le .NET. Espace de travail Cargo (`contracts`, `core`, `worker`, `central`, `launcher`, `sandbox-relay`), règles de lint, CI Rust. Maquette de l'écrivain `sqlx` à priorités. Format de réchiffrement des secrets | Tout | 2 à 3 semaines |
| 1. Agent worker | Enrôlement, présence, SSE et WebSocket, réclamation, renouvellement, étapes et fin, synchronisation du Store, transferts, relais de bac à sable, `MediaActions` et composants natifs, interface web du worker | Le central entier (API, worker intégré, lanceur) | 6 à 10 semaines |
| 2. Central, cœur | Stockage (écrivain, migrations sur la base existante), authentification, workers et enrôlement, exécutions, baux, réservations, reprise, publication | — (développé à côté, non livré) | 8 à 12 semaines |
| 3. Central, reste | Flows (validation, versions, simulation), bibliothèques et surveillance, Store, ressources, IA et MCP, opérations (santé, métriques, journaux, rétention), lanceur, réchiffrement des secrets | — | 6 à 10 semaines |
| 4. Bascule du central | Une release : image central Rust (API, worker intégré Rust, lanceur), base existante ouverte telle quelle, secrets réchiffrés | Plus rien en production ; le code .NET reste une release pour le retour arrière | 1 à 2 semaines |
| 5. Retrait | Suppression du .NET ; mise à jour d'`AGENTS.md`, `BACKEND_RULES.md`, `TEST_RULES.md` et des fiches | — | 1 semaine |

Total indicatif : **6 à 9 mois en équivalent temps plein**, avec une incertitude d'au moins ×1,5. La vitesse réelle des agents sur du Rust de cette taille n'est pas connue. Les lots 2 et 3 peuvent avancer en parallèle, mais ne sont livrés qu'ensemble (point 1).

**Critères pour déclarer un lot terminé** (tous requis) :
1. La suite de contrat est verte contre l'implémentation Rust du lot, sans dérogation non décidée.
2. Les tests de comportement du domaine portés depuis le .NET sont verts en CI.
3. Les scénarios multi-conteneurs sont verts (mixtes au lot 1, tout Rust au lot 4).
4. Le banc NAS est rejoué par DevOps : matrice, S6, perte de worker et film long, sans régression ; compteurs de l'écrivain relevés à partir du lot 2.
5. La relecture QA est faite, ainsi que la relecture sécurité pour tout ce qui touche à l'authentification, au bac à sable ou aux secrets.
6. Les fiches de domaine sont à jour ; aucun « non vérifié » nouveau sans mention dans [open-questions](../open-questions.md).

## Alternatives écartées
- **Bascule d'un bloc** : point 1, option A.
- **Découpage du central par routes** : point 1, option C, incompatible avec l'écrivain unique.
- **Changement de moteur de base dans la foulée** : point 3 ; aucun incident mesuré ne l'impose, et le combiner à la réécriture mélangerait deux sources de régression.
- **Réécrire les plugins Python et Node en Rust** : leur protocole est un contrat stable et ils tournent hors processus ; aucun gain.
- **Réimplémenter Data Protection en Rust** : point 3, option 2.

## Conséquences et risques
- **Deux langages** pendant 6 à 9 mois : coût de CI et de revue, risque de dérive entre les deux implémentations. Ce risque est contenu par la suite de contrat commune et par la règle « une seule implémentation par image et par version ».
- **Gel fonctionnel partiel** : toute évolution du central pendant les lots 2 et 3 doit être faite dans les deux implémentations, ou différée. Une règle est à décider (proposition : gel des nouvelles fonctionnalités backend pendant les lots 2 à 4, correctifs autorisés et reportés dans les deux).
- **Données** : la bascule touche aux secrets chiffrés (Data Protection). C'est le seul changement de données prévu : sauvegarde obligatoire avant, et test de restauration.
- **Ce qui n'est pas su**, et ne doit pas être présenté comme acquis :
  - la charge réelle de l'écrivain SQLite (compteurs jamais relevés) ;
  - le gain de performance, de mémoire et de taille d'image de Rust pour cette application ;
  - la vitesse de réécriture ;
  - la fidélité de sérialisation JSON nécessaire au frontend ;
  - la maturité des bibliothèques Rust pour le serveur MCP et le serveur WebDAV des transferts ;
  - le comportement de Landlock et seccomp en production, déjà non vérifié aujourd'hui ([open-questions](../open-questions.md), point 3).
- **Décisions à prendre par Rémi** avant de démarrer le lot 0 :
  - la stratégie (point 1) ;
  - le maintien de SQLite et la bibliothèque (point 3) ;
  - le traitement des secrets Data Protection ;
  - la règle de tests locaux pour Rust ;
  - la règle de gel fonctionnel pendant les lots 2 à 4 ;
  - l'emplacement du code Rust dans le dépôt (proposition : `backend-rs/` à côté de `backend/`, retiré au lot 5).
