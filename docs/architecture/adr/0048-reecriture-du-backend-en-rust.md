---
id: 0048
title: Réécriture du backend en Rust — cadrage
status: accepted
domain: workers, storage, runtime, plugins, auth-ops
date: 2026-11-08
decided_by: Rémi (2026-11-08)
supersedes: []
superseded_by: []
tags: [rust, réécriture, contrats, sqlite, migration, sécurité, lots]
---

# 0048 — Réécriture du backend en Rust — cadrage

## Contexte
Rémi a décidé de réécrire en Rust l'intégralité du backend : central (API, worker intégré, lanceur), agent worker, exécution des composants natifs, outils (ticket #36). L'interface reste en Angular ; son passage éventuel à Rust sera décidé plus tard. Le backend actuel compte environ 46 500 lignes de C# (`Encoding.Api` 30 700, `Encoding.WorkerAgent` 9 000, `Encoding.Contracts` 4 900, `Encoding.Core` 1 300, `Encoding.Launcher` 200), environ 20 600 lignes de tests .NET, 44 migrations SQLite et environ 300 routes HTTP. Cet ADR **ne décide pas** de la réécriture, qui est déjà décidée. Il cadre comment la faire sans casser les invariants d'[AGENTS.md](../../../AGENTS.md) : options, recommandation et risques pour chacun des huit points demandés. **Aucun code Rust** et aucune modification d'`AGENTS.md`, de `BACKEND_RULES.md` ni de `TEST_RULES.md` avant que cet ADR soit accepté.

Mesures et chiffres : comptage de lignes sur `test` au commit `edf6ddb` (fichiers `*.cs` hors `obj/` et `bin/`) ; nombre de routes approximé par les appels `Map{Get,Post,Put,Delete,Patch}` (306 occurrences). Lorsque cet ADR cite l'[ADR 0009](0009-ecrivain-sqlite-unique-par-noeud.md), il n'en retient que l'**écrivain SQLite local unique** et sa file à priorités : le maillage multi-nœuds mentionné dans l'ADR 0009 a été retiré par l'[ADR 0036](0036-separation-stricte-central-worker.md).

## Décision

### 1. Stratégie : progressive par composant, en commençant par l'agent worker

| Option | Pour | Contre |
|---|---|---|
| A. Réécriture d'un bloc (tout le backend, une bascule) | Pas de cohabitation ; un seul langage dans la cible | Gel fonctionnel long ; aucune validation réelle avant la fin ; régressions découvertes toutes ensemble ; retour arrière = tout ou rien |
| **B. Progressive par composant** : worker d'abord, puis le central d'un seul tenant | Chaque lot est livrable et validé sur le banc NAS ; contrats déjà nets entre worker et central (baux, SSE, WebSocket, Store) ; le C# sert d'oracle | Deux langages dans le dépôt pendant la transition ; il faut prouver les contrats |
| C. Progressive par route du central (deux processus, proxy) | Bascule fine | **Incompatible avec l'écrivain unique** : deux processus écriraient le même fichier SQLite sans file commune ([ADR 0009](0009-ecrivain-sqlite-unique-par-noeud.md), [storage](../domains/storage.md)) ; état en mémoire partagé (hubs SSE et télémétrie, [ADR 0041](0041-sse-a-la-place-du-sondage.md), [0045](0045-websocket-telemetrie-worker.md)) |

**Recommandation : B.** L'ordre est le suivant : l'agent worker, puis le central en **une seule bascule**, avec son API, son worker intégré (l'agent Rust du lot 1) et son lanceur. Le central n'est jamais découpé entre deux processus.

**Compatibilité avec l'[ADR 0036](0036-separation-stricte-central-worker.md)** : rien ne change au modèle. Le central reste l'unique autorité et embarque son worker ; le worker reste sans base et est piloté par le central.

**Compatibilité avec l'[ADR 0039](0039-version-exacte-worker-central.md)** : la version est celle de l'image (`APP_VERSION`), pas celle du langage. Une image worker Rust et une image central .NET construites **depuis le même commit** portent la même version (`test-<sha7>`, `dev-<sha7>`, version de release). La cohabitation reste donc dans la règle tant que les deux images d'une version sortent du même commit.

**Retour arrière** : republier la paire d'images de la release précédente, qui est aussi la règle actuelle. Un worker .NET et un worker Rust ne coexistent jamais sous la même version : chaque version publiée choisit une seule implémentation par image. Pour la bascule du central (lot 4), le retour arrière doit en outre préserver la lecture des secrets chiffrés : voir le protocole de transition au point 3.

### 2. Contrats conservés et prouvés
Contrats figés pendant toute la transition : l'API REST (`/api/v1/**`, codes HTTP, corps JSON en camelCase, codes d'erreur comme `sqlite_write_unavailable`, `store_index_unreadable`, `worker_lost`), les en-têtes de sécurité HTTP (`Content-Security-Policy` / `frame-ancestors`, `X-Content-Type-Options: nosniff`, `X-Frame-Options`, `Referrer-Policy`), les SSE (`/api/v1/live`, `…/wake`, `/api/v1/store/events` avec ses identifiants `v2.{rev}`, télémétrie), le WebSocket de télémétrie ([0045](0045-websocket-telemetrie-worker.md)), l'enrôlement et le jumelage ([0034](0034-rattachement-par-compte-administrateur.md)), l'en-tête `X-Node-Token`, le protocole des plugins hors processus (`describe`/`execute`, `contractVersion` 1) et le format de `repository.json`.

| Option | Valeur |
|---|---|
| A. Relecture manuelle | Insuffisante : environ 300 routes et des détails de sérialisation |
| B. Comparaison du document OpenAPI | Utile (le central en génère un, `OpenApiEndpoints`) mais ne vérifie pas les comportements |
| **C. Tests de contrat exécutables contre les deux implémentations** | Prouve le comportement |

**Recommandation : C + B.**
- (1) Un **instantané OpenAPI** tiré du .NET au lot 0 ; la CI compare celui de l'implémentation Rust (routes, méthodes, schémas) et vérifie qu'aucune route enregistrée dans le routeur Rust n'est absente de l'OpenAPI (voir point 6, I4).
- (2) Une **suite de contrat HTTP en boîte noire**, indépendante du langage (requêtes et réponses attendues, codes, en-têtes de sécurité HTTP, limites de taille et de délai, refus d'autorisation `401`/`403` générés pour chaque route de l'OpenAPI, trames SSE et WebSocket). Elle est écrite au lot 0, à partir des scénarios .NET existants (`AuthScenarios`, `WorkerStoreEventsTests`, catalogue des routes anonymes…), et exécutée en CI contre le .NET puis contre le Rust.
- (3) Des **scénarios mixtes** : le central .NET avec le worker Rust (lot 1), puis le central Rust avec le worker Rust, dans le harnais multi-conteneurs (`DockerCentralTests`).
- (4) Le **frontend Angular** consomme ce contrat : ses modèles (`shared/models`) et des E2E légers servent de vérification côté client.

Point de vigilance : **sérialisation**. `System.Text.Json` écrit les dates `DateTimeOffset` avec un nombre variable de décimales (`…25.81+00:00`), les énumérations, les `null` et l'ordre des champs à sa façon. Le `serde` côté Rust doit reproduire ce qui est observable, ou le frontend doit être vérifié tolérant. Les tests de contrat doivent couvrir ces formats.

### 3. Base de données et migration des secrets

**État honnête des incidents** :

| Incident | Cause | Moteur ou code ? |
|---|---|---|
| Traducteur PostgreSQL → SQLite par expressions régulières, retiré par l'[ADR 0043](0043-sql-sqlite-natif.md) | SQL écrit au dialecte PostgreSQL et réécrit à l'exécution | **Code** (héritage du support PostgreSQL), pas une limite de SQLite |
| 503 `sqlite_write_unavailable` sur `GET /api/v1/executions` (#32) | La validation de session ouvrait une transaction d'écriture à chaque requête, avant de lire ; l'admission a dépassé 5 s pendant un long traitement | **Code**, corrigé par les PR #50 et #51. Le modèle à un seul écrivain l'a rendu visible, mais un autre moteur aurait seulement masqué l'écriture inutile |
| Attentes de verrou des requêtes de présence et d'attribution, qui ont motivé l'[ADR 0009](0009-ecrivain-sqlite-unique-par-noeud.md) | Des tâches de fond ouvraient des transactions d'écriture sans travail ; écritures concurrentes directes | **Mixte** : le moteur n'admet qu'un écrivain (propriété voulue), et le code tenait le verrou sans raison. L'écrivain unique et ses priorités traitent la partie code |
| Détentions longues de l'écrivain pendant le film de 726 Mo (#32) | **Inconnue** | **Non mesuré** : les compteurs `encoding_sqlite_writer_long_holds_total`, `…_hold_seconds_total`, `…_expired_total` et `encoding_sqlite_best_effort_abandoned_total` n'ont jamais été relevés sur un rejeu long (plan de banc #37 en cours de revue) |

Constat d'exploitation du 2026-10-21 : aucune erreur `database is locked` dans les journaux des deux hôtes ([storage](../domains/storage.md)). **Aucune donnée ne montre aujourd'hui une limite du moteur SQLite pour la charge d'un central unique.** Les incidents connus viennent du code, et le seul point ouvert n'est pas mesuré.

**Options** :

| Option | Pour | Contre |
|---|---|---|
| **A. SQLite, même fichier, même schéma** | Aucune migration de données relationnelles ; un seul conteneur ; sauvegarde par copie de fichier conforme à la reprise par restauration ; principe validé ([AGENTS.md](../../../AGENTS.md), [0043](0043-sql-sqlite-natif.md)) | Un écrivain à la fois, à servir par une file dans le code |
| B. PostgreSQL | Écrivains concurrents, verrous de ligne, outils d'exploitation | Un service de plus (conteneur, mises à jour, sauvegarde `pg_dump`, mot de passe) pour un central sans haute disponibilité ; 44 migrations et environ 300 requêtes à réécrire ; migration de données obligatoire ; déjà écarté par l'[ADR 0009](0009-ecrivain-sqlite-unique-par-noeud.md) faute de preuve ; aucun gain démontré |
| C. libSQL / Turso (fork de SQLite à écrivains concurrents) | Fichier compatible SQLite | Concurrence d'écriture encore expérimentale ; fork d'un fournisseur unique ; ne répond à aucun incident mesuré |
| D. Stockage clé-valeur embarqué (redb, sled) ou DuckDB | Embarqué | Pas de SQL relationnel transactionnel adapté (clé-valeur) ou orienté analyse (DuckDB) : réécriture complète de la persistance, sans gain |

**Recommandation : A, sans changement de moteur.** Le changement de moteur pourra être réexaminé sur **mesures** : relever les compteurs de l'écrivain sur un rejeu long (#32) et exécuter le banc de charge SQLite (#37). Critère proposé pour rouvrir la question : après correction des transactions longues identifiées, des expirations d'admission ou des détentions de plus d'1 s qui restent régulières sous charge normale.

**Bibliothèque Rust** :
- `sqlx` : asynchrone. Sa vérification hors ligne des requêtes (`sqlx prepare`) prend le relais de `SqlStatementsPrepareTests`. Un pool d'écriture à **une** connexion, plus une petite file à priorités pour la présence et les baux, reproduit `SqliteWriter`. C'est cohérent avec le skill `rust-conventions-backend`.
- `rusqlite` : synchrone, appelé depuis un thread dédié. L'écrivain unique s'y exprime naturellement, mais au prix d'un pont async à écrire et sans vérification des requêtes à la compilation.
- **Recommandation** : `sqlx`. Une maquette mesurée au lot 0 doit valider la file à priorités avant d'engager le central.

**Reprise des données** : le central Rust ouvre le `cluster.db` existant. Il garde le dossier `backend/migrations/sqlite/` et la table `schema_migrations` avec ses empreintes SHA-256 (même vérification au démarrage, numérotation qui continue à 0078). Les formats sont portables tels quels : instants en texte UTC de largeur fixe et JSON en texte ([0043](0043-sql-sqlite-natif.md)), mots de passe PBKDF2-HMAC-SHA512 à 210 000 itérations, sessions et jetons hachés en SHA-256.

**Inventaire complet des secrets ASP.NET Data Protection (C2)** :
Le code .NET utilise **7 purposes** Data Protection rattachés à l'application `Encoding.PluginSettings` (`Program.cs` l. 36-38, trousseau XML dans `PluginSettings:KeyDirectory`, ou trousseau local `{AiTools:CredentialsDirectory}/.keys` en repli dans `AiSettingsService`) :

| # | Purpose / Discriminant | Fichier source (.NET) | Emplacement de stockage | Persistance | Traitement à la bascule |
|---|---|---|---|---|---|
| 1 | `SetApplicationName("Encoding.PluginSettings")` | `Program.cs` l. 38 | Discriminant racine préfixé à tous les purposes ci-dessous ; clés XML dans `PluginSettings:KeyDirectory` | Persistant (trousseau) | Remplacé par la clé maîtresse AES-256-GCM (`0600`) ; trousseau XML purgé au lot 5 |
| 2 | `Encoding.PluginConfiguration.v1` | `PluginConfigurationRepository.cs` l. 12, `RuntimeRepository.Claim.cs` l. 394 | SQLite (`BLOB`) dans **3 colonnes** : `plugin_configurations.encrypted_values` (clé `plugin_id`), `flow_plugin_configurations.encrypted_values` (clé `flow_id, plugin_id`), `execution_work.plugin_configuration_snapshot` (clé `execution_id`) | **Persistant** (base) | Réchiffré en AES-256-GCM par le central .NET de transition |
| 3 | `Encoding.PlexSetup.v1` | `PlexSetupSession.cs` l. 16 | Jeton URL-safe renvoyé au navigateur pendant l'assistant Plex (aucun stockage serveur) | **Éphémère** (15 min) | Non migré : expire à la bascule (l'opérateur relance l'assistant Plex si ouvert) |
| 4 | `Encoding.AiAssistantAction.v1` | `AiAssistantSupportService.cs` l. 104, `AiAssistantActionsService.cs` l. 270 | Jeton `ConfirmationToken` renvoyé au client pour confirmer une action d'écriture IA (aucun stockage serveur) | **Éphémère** (5 min) | Non migré : expire à la bascule |
| 5 | `Encoding.Ai.v2` + sous-purpose dynamique `purpose = key` | `AiCentralStore.cs` l. 12 et 18 (`protector.CreateProtector(purpose)`) | SQLite (`TEXT` JSON contenant du Base64) dans `registers` pour `register_key LIKE 'ai/%'` (`settings`, `model-policy/{provider}/{sha256}`, `session/{id}`, `assistant/conversations/{owner}/{id}`, `assistant/session-owner/{id}`, `assistant/activity/{owner}/{reqId}`) | **Persistant** (base) | Réchiffré en AES-256-GCM par le central .NET de transition en liant le sous-purpose exact `key` dans l'AAD |
| 6 | `Encoding.Ai.ClaudeToken.v1` | `AiCliRuntime.cs` l. 17 et 55 | Fichier `{AiTools:CredentialsDirectory}/claude-cli/oauth-token.protected` (sous `{Worker:DataDirectory}/credentials/ai-tools/` par défaut) | **Persistant** (fichier disque) | Réchiffré sur place (écriture atomique `.tmp` en `0600`) par le central .NET de transition |
| 7 | `Encoding.Ai.ApiKey.v1` | `AiSettingsService.cs` l. 19, 195-245 | Fichier JSON `{AiTools:CredentialsDirectory}/settings.json` (champs `openAiApiKey` et `anthropicApiKey` préfixés par `"protected:"`), et lecture initiale via `IConfiguration` (`AiTools:OpenAi:ApiKey`, `AiTools:Anthropic:ApiKey`) | **Persistant** (fichier disque / config) | Réchiffré dans `settings.json` par le central .NET de transition ; si `IConfiguration` fournit une valeur `"protected:"` hors fichier, le démarrage échoue avec un message explicite demandant une valeur en clair dans la variable d'environnement ou l'enregistrement via l'API |

Aucun autre usage implicite de Data Protection n'existe dans le central (les cookies de session sont des jetons aléatoires hachés en SHA-256, et la protection CSRF repose sur l'en-tête statique `X-Encoding-Csrf`).

**Protocole de réchiffrement, format cible et retour arrière (C1)** :
- **Qui réchiffre (correction de cohérence entre lots 3 et 4)** : **seul le central .NET sait déchiffrer ASP.NET Data Protection**. Le réchiffrement ne peut donc jamais être exécuté par le binaire Rust : il est réalisé par une **dernière release .NET de transition** (livrée à la fin du lot 3, avant la bascule du lot 4).
- **Format cible AES-256-GCM** :
  - **Algorithme** : `AES-256-GCM` avec nonce de **96 bits (12 octets)** généré par CSPRNG, unique à chaque chiffrement.
  - **En-tête versionné** : préfixe binaire explicite `ENC1` (version `1`) + identifiant de clé (`key_id` sur 16 octets) + nonce (12 octets) + texte chiffré + tag d'authentification GCM (16 octets), afin de permettre une rotation ultérieure des clés sans ambiguïté de format.
  - **Données associées authentifiées (AAD)** : `AAD = "{purpose}|{record_id}"` en UTF-8 (par exemple `Encoding.PluginConfiguration.v1|plugin_configurations:{plugin_id}`, `Encoding.PluginConfiguration.v1|flow_plugin_configurations:{flow_id}:{plugin_id}`, `Encoding.PluginConfiguration.v1|execution_work:{execution_id}`, `Encoding.Ai.v2|{key}|registers:ai/{key}`, `Encoding.Ai.ClaudeToken.v1|file:claude-cli/oauth-token`, `Encoding.Ai.ApiKey.v1|settings:openAiApiKey`). L'AAD reproduit l'isolation par purpose de Data Protection et empêche d'échanger les textes chiffrés entre deux colonnes, deux lignes ou deux fichiers.
  - **Clé maîtresse** : clé de 256 bits générée par CSPRNG au premier démarrage de la release de transition, écrite dans un fichier dédié en mode **`0600`** appartenant à l'utilisateur du service (dans `PluginSettings:KeyDirectory`).
- **Zéro secret en clair sur disque** : la migration déchiffre puis réchiffre en mémoire vive (`Zeroize` / `CryptographicOperations.ZeroMemory`), valeur par valeur, à l'intérieur d'une transaction SQLite unique pour la base et par renommage atomique de fichiers `.tmp` créés en `0600` pour `settings.json` et `oauth-token.protected`. Aucun export intermédiaire, fichier temporaire en clair ni journal du clair n'est produit.
- **Préservation du retour arrière** : une release .NET antérieure à la transition ne sait pas lire `ENC1` (AES-256-GCM). Deux mécanismes complémentaires garantissent le retour arrière :
  1. La **release .NET de transition** (fin du lot 3) sait lire **les deux formats** (Data Protection historique et `ENC1` AES-256-GCM) et constitue le **point de retour arrière obligatoire** du lot 4.
  2. Tant que la bascule Rust (lot 4) n'est pas déclarée stable, les textes chiffrés Data Protection d'origine et le trousseau XML sont **conservés en archive** (table SQLite `legacy_dataprotection_backup` et copie `.dp-backup` en `0600` des deux fichiers IA), ou bien Rémi choisit de limiter le retour arrière à la seule release .NET de transition (voir la liste des décisions pour Rémi).
- **Destruction de l'ancien trousseau XML** : aujourd'hui, le trousseau Data Protection est stocké **en clair sur disque** (`PersistKeysToFileSystem` sans `ProtectKeysWith*`, `Program.cs` l. 37). Une fois la bascule Rust déclarée stable (lot 5), l'ancien trousseau XML (`key-*.xml` dans `PluginSettings:KeyDirectory` et `{AiTools:CredentialsDirectory}/.keys`) ainsi que l'archive des anciens textes chiffrés sont **explicitement détruits**. Il est rappelé dans la documentation d'exploitation (`backup-restore.md`) que toute sauvegarde prend à la fois `cluster.db`, `credentials/ai-tools/` et le dossier des clés : un vol de sauvegarde donne accès aux secrets, avant comme après la bascule.

### 4. Écrivain unique, baux, réservations, reprise, fichiers, transferts
Ces invariants sont **portés à l'identique**, sans les réinventer ([runtime](../domains/runtime.md), [0009](0009-ecrivain-sqlite-unique-par-noeud.md) pour le seul écrivain local, [0044](0044-echec-apres-perte-de-bail.md)) :
- **Transactions** : une transaction d'écriture est admise une à la fois puis ouverte en `BEGIN IMMEDIATE` ; la présence et les baux sont prioritaires ; les lectures se font sans admission (instantané ou `SELECT` simple, #32).
- **Baux** : époque et fencing, aucune réattribution silencieuse, échec `worker_lost` après expiration, reprise uniquement par une nouvelle exécution.
- **Fichiers** : réservations et verrous de disque ; sortie écrite à côté puis validée, publication atomique sans écraser la source ; transferts WebDAV avec contrôle d'empreinte.

**Options** :
- Traduction fidèle, module par module, avec les tests actuels comme spécification : **recommandé**.
- Réécriture « idiomatique » qui reconçoit ces mécanismes : écartée, car elle reviendrait à rouvrir des décisions de Rémi sans ADR.

Rust apporte des types dédiés (`Epoch`, `ExecutionId`, états d'exécution sous forme d'`enum` exhaustif), qui rendent certaines erreurs d'invariant impossibles à compiler.

**Risques** : invariants répartis dans environ 30 000 lignes, dont certains seulement documentés par des tests. Un comportement non testé se perd sans bruit. **Garde-fou** : chaque lot porte d'abord, en tests de contrat ou de comportement, les scénarios .NET du domaine (`Harness.Failover`, `WorkerProcessLossTests`, `Harness.LegacyResumptions`…), puis rejoue le banc NAS (matrice, S6, perte de worker).

### 5. Plugins hors processus, Landlock, seccomp, outils média, capacités
- **Plugins tiers** (Python, Node) : **inchangés**. Le protocole stdin/stdout `contractVersion` 1 est un contrat ; les SDK et le dépôt `encoding-store` ne bougent pas. Les plugins officiels « natifs » (`base`, `video`, `audio`, `subtitles`, `mkv`…) ne sont que des manifestes : leur **exécution** est codée dans le worker (`MediaActions`, environ 9 000 lignes avec l'agent) et migre avec le lot worker.
- **Bac à sable Landlock et seccomp (I1)** : Landlock et seccomp ([0042](0042-reseau-plugins-capacite-et-seccomp.md), [plugins](../domains/plugins.md)) s'écrivent avec les crates `landlock` (mainteneur du noyau) et `seccompiler` (rust-vmm/Firecracker), dans un **binaire relais séparé, mono-thread et sans runtime `tokio`** (`sandbox-relay`, équivalent de `--isolated-process`), qui applique le confinement juste avant `execve` :
  - **Refus strict de tout repli non confiné** : la crate `landlock` utilise `CompatLevel::BestEffort` par défaut, ce qui dégrade silencieusement la protection sur un noyau ancien et viole [AGENTS.md](../../../AGENTS.md). Le relais impose **`CompatLevel::HardRequirement`** sur l'ensemble des règles, déclare une version d'ABI minimale explicite et vérifie que `restrict_self()` retourne **`RulesetStatus::FullyEnforced`** (`RestrictionStatus::FullyEnforced`) ; tout autre statut provoque l'arrêt immédiat avec un code de sortie dédié avant `execve`.
  - **Seccomp** : le relais active `PR_SET_NO_NEW_PRIVS` (`prctl`) avant de charger le filtre BPF, applique une **liste blanche d'appels système par architecture** (`x86_64`, et `aarch64` si l'image cible cette architecture) avec **action par défaut de refus** (`Errno(EPERM)` / `KillProcess`), puis invoque `execve`.
  - **Test d'intégration obligatoire** : un test vérifie que le relais échoue fermement et n'exécute jamais le binaire cible lorsque Landlock ou seccomp est indisponible ou partiellement appliqué.
- **Politique `unsafe` (I2)** :
  - `#![forbid(unsafe_code)]` est déclaré à la racine des crates `encoding-central`, `encoding-worker`, `encoding-contracts`, `encoding-core` et `encoding-launcher`.
  - L'usage de `unsafe` est **strictement cantonné** au binaire `sandbox-relay` (pour les appels `prctl` / `execve` bas niveau si nécessaires) et, le cas échéant, à un module FFI isolé, avec `#![deny(clippy::undocumented_unsafe_blocks, missing_safety_doc)]` et **relecture obligatoire par l'Expert Sécurité** de chaque bloc `unsafe`.
- **Outils média** : FFmpeg, ffprobe et MKVToolNix restent des processus externes, lancés par `tokio::process` avec délais et capture bornée. La détection des capacités (encodeurs vérifiés, accélération matérielle) est portée telle quelle. Les corrections récentes doivent être reprises avec leurs tests : #24 et #28 (dispositions), #25 (`+genpts`), #26 (`worker_lost`).
- **Sécurité propre au lot 1 — worker Rust et serveur WebDAV (I7)** :
  - Les identifiants administrateur saisis lors de l'enrôlement du worker ne sont utilisés qu'en mémoire pour l'échange d'enrôlement et ne sont **jamais écrits sur disque ni journalisés**.
  - Le jeton de nœud obtenu (`X-Node-Token`) est enregistré sur disque avec les permissions **`0600`**.
  - L'interface web locale du worker conserve exactement les mêmes règles d'authentification et d'exposition qu'aujourd'hui.
  - Le **serveur WebDAV des transferts** est la surface réseau la plus exposée du lot 1 : avant la bascule de l'image worker, il fait l'objet d'une **relecture sécurité dédiée** et d'une suite de tests d'intrusion couvrant le confinement canonique des chemins dans le dossier de transfert (refus de `..`, des octets nuls et de tout lien symbolique résolvant hors de la racine), les plafonds de taille et d'en-têtes, et la vérification d'empreinte SHA-256.
- **Risque** : `MediaActions` concentre beaucoup de règles fines, prouvées par le banc. La parité se démontre en rejouant la matrice NAS sur le worker Rust avant de basculer l'image.

### 6. Authentification, sessions, jetons, secrets, routes fermées et limites DoS
Le schéma est porté **à l'identique du code** ([auth-ops](../domains/auth-ops.md)) : cookie opaque `encoding_session` (`HttpOnly`, `SameSite=Strict`, `Secure` selon le schéma ou le proxy déclaré) haché en SHA-256, délais d'inactivité et absolu, en-tête CSRF `X-Encoding-Csrf`, jetons `enc_pat_` avec portées, jetons de worker, limitation des routes anonymes, rôles `admin` et `viewer`.

**Routes fermées par défaut et vérifiabilité dans `axum` (I4)** :
- Option 1 (**recommandée**) : l'autorisation est une couche `tower` appliquée à **tout** le routeur (y compris `fallback`, sous-routeurs `nest`, fichiers statiques et réponses `405 Method Not Allowed`), qui refuse par défaut.
- Option 2 (écartée) : des vérifications dans chaque gestionnaire, car l'oubli ouvre la route.
- **Registre de routes vérifiable** : contrairement à ASP.NET Core (`EndpointDataSource`, utilisé par `AuthScenarios` l. 529), `axum::Router` n'offre aucune introspection de ses routes après construction. Pour conserver la preuve automatique du « fermé par défaut », aucun appel direct à `axum::Router::route` n'est autorisé dans les modules métier : toutes les routes passent par un **constructeur centralisé unique** qui exige un marqueur de politique typé obligatoire (`AccessPolicy::Admin`, `Viewer`, `NodeCredentials`, `Anonymous`, etc.) et alimente en même temps un **registre en mémoire énumérable**.
- **Double contrôle en CI** :
  1. Un test compare le registre en mémoire à la liste blanche explicite des routes anonymes et à l'instantané OpenAPI, afin de **détecter toute route non documentée**.
  2. La suite de contrat génère automatiquement, pour chaque opération de l'OpenAPI, les **tests de refus** : `401` sans identifiants hors liste anonyme, `403` pour un `viewer` sur une route de mutation, `403` pour un jeton de worker hors des routes `AllowNodeCredentials*`, `403` sans en-tête `X-Encoding-Csrf` sur une mutation authentifiée par cookie de session, et refus hors portée pour un jeton `enc_pat_`.

**Garanties de sécurité hors schéma OpenAPI à porter et tester (I5)** :
Les protections suivantes ne se voient pas dans le schéma OpenAPI et font l'objet de tests dédiés dans la suite de contrat :
- **Verrouillage de compte** après 5 échecs de connexion, avec délai doublant et réponses d'erreur indiscernables (temps et message).
- **Comparaison en temps constant** (`subtle::ConstantTimeEq`) pour `Auth:SetupToken`, le jeton Bearer de `/metrics`, les jetons de worker et les empreintes de jetons.
- **Limitation de débit (rate-limiting) par adresse IP** sur les routes anonymes (`/api/v1/auth/login`, `/api/v1/auth/setup`, enrôlement, jumelage) et plafond de demandes de jumelage en attente.
- **Confiance dans `X-Forwarded-For` / `X-Forwarded-Proto`** strictement limitée aux proxies et réseaux déclarés (`KnownProxies` / `KnownNetworks`) ; ignorés sinon pour empêcher le contournement du rate-limiting par falsification d'en-tête.
- **Refus au démarrage** de `Auth:Enabled=false` dès que l'environnement n'est pas `Development`.
- **Hygiène des journaux et des erreurs SQL** : aucune chaîne de requête (`query string`) ni aucun en-tête sensible dans les journaux applicatifs ; **aucune requête SQL brute ni affichage direct de `sqlx::Error`** dans les réponses HTTP ou les journaux persistés (`sqlx::Error` inclut le texte SQL dans son implémentation `Display` : les erreurs de base doivent être interceptées et traduites en codes métier stables sans exposer la requête).
- **Secrets en mémoire** : utilisation obligatoire de `secrecy::SecretString` / `SecretVec` (avec `zeroize` à la libération) pour tout secret en mémoire (mots de passe, jetons, clés API, clé maîtresse AES-GCM), et interdiction par lint/revue de `#[derive(Debug, Serialize)]` sur toute structure contenant un secret.

**Limites de déni de service `hyper` / `axum` (I6)** :
Contrairement à Kestrel qui applique des bornes par défaut, `hyper` et `axum` exigent de configurer explicitement et de vérifier dans la suite de contrat :
- `DefaultBodyLimit` et `RequestBodyLimitLayer` explicites par catégorie de route, y compris sur les routes de flux (`BodyDataStream` / multipart) qui contournent `DefaultBodyLimit` ;
- la taille et le nombre maximaux d'en-têtes HTTP, le délai de lecture des en-têtes (`header_read_timeout`) et un `TimeoutLayer` global par requête ;
- un plafond de connexions simultanées et un nombre maximal de flux SSE (`/api/v1/live`, `/api/v1/store/events`, journaux) et WebSocket de télémétrie par client/worker.

**Sessions, mots de passe et écart documentaire (N1, N3)** :
- **N1 — Continuité des sessions et mots de passe à la bascule** : le format PBKDF2-HMAC-SHA512 (210 000 itérations, sel et hash) et le hachage SHA-256 des sessions et jetons `enc_pat_` sont repris tels quels, validés au lot 0 par un **vecteur de test croisé .NET ↔ Rust** (un hachage produit par .NET est vérifié par Rust et inversement). Une option d'exploitation (`Auth:RevokeSessionsOnStartup=true`) est prévue pour permettre à Rémi de révoquer toutes les sessions existantes lors de la bascule s'il le souhaite (tranché : décision 4, sessions conservées par défaut).
- **N3 — Alignement de `auth-ops.md` sur le code pour `/metrics` et `/health/details`** : la fiche [auth-ops](../domains/auth-ops.md) (l. 38) indique par erreur que `/metrics` et `/health/details` sont sans authentification par défaut, alors que `OperationsEndpoints.cs` (l. 58-72) exige une session sur `/health/details` et exige sur `/metrics` soit une session, soit le jeton Bearer `Operations:Metrics:Token`. Le portage Rust suit **le code** (routes fermées par défaut), et la fiche `auth-ops.md` sera corrigée.

### 7. CI, chaîne d'approvisionnement, images, flux test → dev → main, tests
- **Flux** ([0047](0047-cycle-test-dev-main-et-images-par-branche.md)) : inchangé (`test` : images seulement ; `dev` : CI légère ; `main` : CI complète). Ajouts proposés :
  - **Chaîne d'approvisionnement (I3)** :
    - `Cargo.lock` est versionné et toutes les commandes CI et Docker compilent avec **`--locked`** ;
    - la version du compilateur et des outils est épinglée dans **`rust-toolchain.toml`**, suivie par Dependabot/Renovate ;
    - **`cargo deny check advisories licenses bans sources`** est exécuté et **bloquant** dans `dev-ci` comme dans `promotion-ci` : base RustSec à jour, liste blanche de licences, **`crates.io` comme unique source autorisée**, et **interdiction de toute dépendance `git` non épinglée ou non décidée** ;
    - le choix des crates sensibles (`landlock`, `seccompiler`, cryptographie, serveur WebDAV, MCP) fait l'objet d'une décision écrite au lot 0.
  - en CI légère sur `dev` : `cargo fmt --check`, `cargo clippy --locked -- -D warnings -D clippy::undocumented_unsafe_blocks -D missing_safety_doc`, `cargo build --locked`, `cargo deny check advisories licenses bans sources` et le test d'architecture vérifiant `#![forbid(unsafe_code)]` et les frontières entre crates ([ADR 0031](0031-frontieres-backend-verifiees-par-tests.md)) ;
  - en CI complète sur `main` (`promotion-ci`) : `cargo test --locked`, `cargo deny`, la suite de contrat (incluant les refus OpenAPI, les en-têtes de sécurité et les limites DoS) contre l'implémentation livrée, et les scénarios multi-conteneurs.
- **Images** : construction en plusieurs étapes (`cargo build --release --locked`, puis image d'exécution Ubuntu inchangée avec FFmpeg, MKVToolNix et pilotes GPU) ; `APP_VERSION` injectée à la compilation. L'image n'embarque plus le runtime ASP.NET. Le gain de taille est attendu mais **non mesuré**.
- **Règles de test locales** : `TEST_RULES.md` interdit les tests .NET locaux. Pour Rust, **aucun `cargo test` en local (CI uniquement)** : comme pour .NET aujourd'hui, seuls `cargo fmt --check`, `cargo clippy --locked` et `cargo check --locked` (avec `nice -n 19`) sont autorisés en local ; tous les tests (`cargo test --locked`, suite de contrat, multi-conteneurs) sont strictement réservés à la CI GitHub Actions.
- **Pendant la transition**, les deux chaînes coexistent dans la CI : `dotnet` reste l'oracle tant qu'un composant n'est pas basculé.

### 8. Lots ordonnés, estimation, critères de fin

| Lot | Contenu | Reste en .NET | Estimation grossière (équivalent temps plein) |
|---|---|---|---|
| 0. Socle | Acceptation de cet ADR et des décisions ouvertes. Instantané OpenAPI et suite de contrat en boîte noire (avec refus `401`/`403`, en-têtes de sécurité et limites DoS), verte contre le .NET. Espace de travail Cargo (`contracts`, `core`, `worker`, `central`, `launcher`, `sandbox-relay`), `rust-toolchain.toml`, `Cargo.lock`, `cargo deny`, `#![forbid(unsafe_code)]`, CI Rust. Maquette de l'écrivain `sqlx` à priorités. Spécification et vecteurs de test du format `ENC1` (AES-256-GCM + AAD) et de PBKDF2 | Tout | 2 à 3 semaines |
| 1. Agent worker | Enrôlement (identifiants en mémoire uniquement, jeton `0600`), présence, SSE et WebSocket, réclamation, renouvellement, étapes et fin, synchronisation du Store, transferts WebDAV (avec relecture sécurité et tests de traversée), relais `sandbox-relay` mono-thread (`HardRequirement` + `FullyEnforced` + seccomp), `MediaActions` (~9 000 lignes avec l'agent) et composants natifs, interface web du worker | Le central entier (API, worker intégré, lanceur) | 6 à 10 semaines |
| 2. Central, cœur | Stockage (écrivain, migrations sur la base existante, masquage `sqlx::Error`), authentification et registre de routes typé, limites DoS, workers et enrôlement, exécutions, baux, réservations, reprise, publication | Le central .NET reste en production (code Rust développé à côté, non livré) | 8 à 12 semaines |
| 3. Central, reste + release .NET de transition des secrets | Flows (validation, versions, simulation), bibliothèques et surveillance, Store, ressources, IA et MCP, opérations (santé, métriques, journaux, rétention), lanceur en Rust ; **et publication d'une dernière release du central .NET** qui réchiffre en mémoire les 5 emplacements persistants Data Protection vers `ENC1` (AES-256-GCM + AAD) tout en sachant lire les deux formats pour servir de point de retour arrière | Le central .NET de transition est livré en fin de lot 3 pour effectuer le réchiffrement | 6 à 10 semaines |
| 4. Bascule du central | Une release : image central Rust (API, worker intégré Rust, lanceur), base existante ouverte telle quelle, secrets `ENC1` lus directement | Plus rien en production ; la release .NET de transition de fin du lot 3 sert de cible de retour arrière | 1 à 2 semaines |
| 5. Retrait et purge | Après stabilité confirmée : destruction explicite de l'ancien trousseau XML Data Protection en clair et des textes chiffrés d'archive ; suppression du code .NET ; mise à jour d'`AGENTS.md`, `BACKEND_RULES.md`, `TEST_RULES.md` et des fiches (dont `auth-ops.md`) | — | 1 semaine |

Total indicatif : **6 à 9 mois en équivalent temps plein**, avec une incertitude d'au moins ×1,5. La vitesse réelle des agents sur du Rust de cette taille n'est pas connue. Les lots 2 et 3 peuvent avancer en parallèle, mais le central Rust n'est livré qu'en une seule bascule au lot 4 (point 1), après le passage par la release .NET de réchiffrement de fin du lot 3.

**Critères pour déclarer un lot terminé** (tous requis) :
1. La suite de contrat est verte contre l'implémentation Rust du lot (y compris les tests de refus OpenAPI, les en-têtes de sécurité HTTP et les limites DoS), sans dérogation non décidée.
2. Les tests de comportement du domaine portés depuis le .NET sont verts en CI, ainsi que `cargo deny`.
3. Les scénarios multi-conteneurs sont verts (mixtes au lot 1, tout Rust au lot 4, avec test de bascule et de retour arrière vers la release .NET de transition).
4. Le banc NAS est rejoué par DevOps : matrice, S6, perte de worker et film long, sans régression ; compteurs de l'écrivain relevés à partir du lot 2.
5. La relecture QA est faite, ainsi que la relecture sécurité obligatoire pour tout ce qui touche à l'authentification, au registre de routes, au serveur WebDAV, au bac à sable (`sandbox-relay` / `unsafe`) ou à la migration des secrets.
6. Les fiches de domaine sont à jour ; aucun « non vérifié » nouveau sans mention dans [open-questions](../open-questions.md).

## Alternatives écartées
- **Bascule d'un bloc** : point 1, option A.
- **Découpage du central par routes** : point 1, option C, incompatible avec l'écrivain unique.
- **Changement de moteur de base dans la foulée** : point 3 ; aucun incident mesuré ne l'impose, et le combiner à la réécriture mélangerait deux sources de régression.
- **Réécrire les plugins Python et Node en Rust** : leur protocole est un contrat stable et ils tournent hors processus ; aucun gain.
- **Réimplémenter Data Protection en Rust** : point 3, fragile et non documenté comme contrat stable.
- **Utiliser `CompatLevel::BestEffort` pour Landlock ou appliquer Landlock/seccomp dans un hook `pre_exec` du runtime `tokio`** : point 5, interdit par `AGENTS.md` (aucun repli non confiné) et dangereux dans un processus multithread.

## Conséquences et risques
- **Deux langages** pendant 6 à 9 mois : coût de CI et de revue, risque de dérive entre les deux implémentations. Ce risque est contenu par la suite de contrat commune et par la règle « une seule implémentation par image et par version ».
- **Gel fonctionnel partiel** : toute évolution du central pendant les lots 2 et 3 doit être faite dans les deux implémentations, ou différée (tranché : décision 6, gel des nouvelles fonctionnalités backend pendant les lots 2 à 4, seuls les correctifs sont autorisés et reportés dans les deux).
- **Données et secrets** : la bascule réchiffre les 5 emplacements persistants de secrets Data Protection via la release .NET de fin de lot 3 (les 2 jetons éphémères `PlexSetup` et `AiAssistantAction` expirant simplement). Sauvegarde complète obligatoire avant (incluant `cluster.db`, `credentials/ai-tools/` et `PluginSettings:KeyDirectory`), test de restauration, puis destruction de l'ancien trousseau XML en clair au lot 5.
- **Ce qui n'est pas su**, et ne doit pas être présenté comme acquis :
  - la charge réelle de l'écrivain SQLite (compteurs jamais relevés, banc #37 à exécuter) ;
  - le gain de performance, de mémoire et de taille d'image de Rust pour cette application ;
  - la vitesse de réécriture ;
  - la fidélité de sérialisation JSON nécessaire au frontend ;
  - la maturité des bibliothèques Rust pour le serveur MCP et le serveur WebDAV des transferts ;
  - la présence éventuelle de secrets IA préfixés `"protected:"` injectés par variable d'environnement ou fichier externe via `IConfiguration` sur les environnements déployés ;
  - le comportement de Landlock (`HardRequirement`) et seccomp en production et la version d'ABI Landlock des hôtes, déjà non vérifiés aujourd'hui ([open-questions](../open-questions.md), point 3).
- **Décisions tranchées par Rémi (2026-11-08) avant de démarrer le lot 0** :
  1. **Stratégie** : Progressive par composant (Option B : worker au lot 1, puis central d'un seul tenant au lot 4).
  2. **Base de données** : Maintien de SQLite (écrivain unique, WAL) avec `sqlx`, sous réserve des résultats du banc de charge #37.
  3. **Secrets Data Protection et retour arrière** : La release .NET de fin de lot 3 réchiffre vers `ENC1` (`AES-256-GCM` + AAD) et conserve en archive (`legacy_dataprotection_backup` et `.dp-backup` en `0600`) les anciens textes chiffrés et le trousseau XML jusqu'au lot 5 (destruction au lot 5).
  4. **Sessions à la bascule** : Conserver les sessions actives par défaut (avec vecteur de test croisé PBKDF2/SHA-256 et option `Auth:RevokeSessionsOnStartup`).
  5. **Tests locaux pour Rust** : **Aucun `cargo test` en local (CI uniquement)** — comme pour .NET aujourd'hui, seuls `cargo fmt --check`, `cargo clippy --locked` et `cargo check --locked` (avec `nice -n 19`) sont autorisés en local ; tous les tests (`cargo test --locked`, suite de contrat, multi-conteneurs) sont strictement réservés à la CI GitHub Actions.
  6. **Gel fonctionnel (lots 2 à 4)** : Gel des nouvelles fonctionnalités backend pendant les lots 2 à 4 (seuls les correctifs sont autorisés et reportés dans les deux implémentations).
  7. **Emplacement** : `backend-rs/` à côté de `backend/` (retiré au lot 5).
