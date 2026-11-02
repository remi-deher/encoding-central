---
domain: auth-ops
updated: 2026-10-24
legacy_adr: [0002, 0009, 0029, 0031, 0044, 0060-api, 0061-mcp, 0065, 0073]
code:
  - backend/src/Encoding.Api/Auth/
  - backend/src/Encoding.Api/Mcp/
  - backend/src/Encoding.Api/Logging/
  - backend/src/Encoding.Api/Infrastructure/Operations/
  - backend/src/Encoding.Core/Logging/
  - frontend/src/app/features/auth/ , features/settings/
tags: [authentification, rôles, session, csrf, jeton, mcp, openapi, santé, métriques, logs, rétention, supervision]
---

# Authentification, API d'automatisation et exploitation

## Authentification et autorisation
- Deux rôles : `admin` (tout) et `viewer` (lecture seule). Comptes dans SQLite, réconciliés ([replication-storage.md](replication-storage.md)) ; le dernier admin actif ne peut être ni rétrogradé, ni désactivé, ni supprimé.
- **L'authentification est obligatoire par défaut** (`Auth:Enabled` vaut `true`, décision de Rémi du 2026-10-21) : au premier démarrage l'interface impose la création du premier admin (`POST /api/v1/auth/setup`, possible tant qu'aucun compte n'existe sur ce nœud). `Auth__Enabled=false` reste possible pour le développement : `SessionAuthenticationHandler` donne alors le rôle `admin` à tout appelant (MCP compris) et le serveur le signale par un avertissement au démarrage. Une instance existante sans compte revient donc sur l'assistant de création d'admin à la mise à jour ; fixer `Auth__Enabled=false` pour garder l'ancien comportement.
- Session : cookie opaque `encoding_session` (HttpOnly, SameSite=Strict, Secure selon `Auth__CookieSecure`/HTTPS), SHA-256 stocké dans `user_sessions` (**local**, non réconcilié : on se connecte sur chaque serveur), expiration inactivité 480 min / absolue 168 h, rôle relu à chaque requête. Mots de passe PBKDF2-HMAC-SHA512 210 000 itérations, 12 à 256 caractères, verrouillage 1 min doublant jusqu'à 15 min après 5 échecs, réponses indiscernables.
- **Politique unique de repli** (toute route, y compris futures) : session ou jeton obligatoire ; GET/HEAD tout rôle ; autres méthodes admin avec en-tête `X-Encoding-Csrf: 1` (exempté pour un jeton Bearer). Exceptions explicites : `.AllowViewer()` (POST sans effet d'état : validation/simulation, import MediaInfo, export, déconnexion, mot de passe propre), `.AllowAnonymous()` (santé, identité du cluster, `auth/status|setup|setup/node|login` — `setup/node` renvoie uniquement la fiche non sensible du nœud local tant qu'aucun admin n'existe, puis `404`, protocole des nœuds : `enroll`, `enrollment-requests`, heartbeat, `runtime/nodes/{id}/**`, `leave`, fichiers de l'interface, webhooks d'intégration qui s'authentifient eux-mêmes), `.AllowNodeCredentials()` (`GET /executions/{id}` avec `X-Node-Token`). `/api/v1/users/**` : admin même en lecture. **Une nouvelle route est fermée par défaut** ; l'ouvrir exige une décision et la mise à jour du test qui énumère les routes anonymes.
- Jumelage : approbation = admin ; enrôlement direct = fenêtre ouverte par un admin ; demandes en attente plafonnées (20, puis 429) ; routes anonymes limitées par adresse (20/min) ; derrière un proxy, réglage Paramètres > Réseau (proxies de confiance par nœud, `network-settings.json`) ; en-têtes `X-Forwarded-*` jamais crus d’un appelant non déclaré.
- Limites : pas de SSO/OIDC ni de 2FA ; le rôle lecture est appliqué par le serveur mais l'UI affiche encore des boutons d'écriture.

## Jetons API et MCP
- Jetons `enc_pat_<64 hex>` (SHA-256 seul stocké, préfixe visible), rôle admin|viewer (tout autre rôle est refusé à la création), expiration optionnelle, révocation, **portées d'écriture** (`scopes`, voir plus bas) ; table `api_tokens` **réconciliée** (n'importe quel serveur authentifie un jeton créé ailleurs). Un jeton cluster admin par défaut est émis à la création du cluster. `Authorization: Bearer` est examiné avant le cookie. `GET/POST /api/v1/tokens`, `DELETE /api/v1/tokens/{id}`. La création par l'interface revalide le mot de passe du compte avant de présenter le secret une seule fois. Les clés créées avant cette évolution ne sont pas récupérables : seul leur hash est conservé. `POST /api/v1/auth/reauthenticate` revalide le mot de passe de la session courante ; l'interface l'exige avant d'afficher/copier l'adresse MCP. Schéma OpenAPI 3.1 avec `bearerAuth`.
- Serveur **MCP** embarqué (JSON-RPC 2.0, protocole 2024-11-05) : SSE `GET /api/v1/mcp/sse` + `POST /api/v1/mcp/message`, HTTP direct `POST /api/v1/mcp`, stdio `dotnet Encoding.Api.dll mcp` (`ENCODING_API_URL`, `ENCODING_API_TOKEN`). Outils : flows (schéma, CRUD, clone, delete), bibliothèques, nœuds, exécutions, modèles, packs, config de plugins, logs système, introspection des outils média et validation de manifestes. Le catalogue d'outils fait foi dans `Mcp/McpService.cs` et `BusinessOperationCatalog.cs` (ne pas citer un nombre). Droits = ceux du jeton.
- **MCP et `/api/v1/business-operations/execute` : lecture par défaut.** Un jeton API, même admin, ne lit que tant qu'il ne porte pas la portée d'écriture du domaine : `mcp:write` (tout), `libraries:write`, `nodes:write`, `flows:write` (flows, scripts, profils, modèles, packs), `executions:control` (cancel/retry/resume), `plugins:write`. Un jeton viewer ne peut pas en porter. La table `api_tokens.scopes` (migration 0043) est réconciliée ; les jetons existants n'ont aucune portée, donc le jeton cluster par défaut ne peut plus écrire via le MCP tant qu'un jeton scopé n'est pas créé. Les routes REST hors MCP gardent le contrôle par rôle. Les sessions de l'interface ne sont pas concernées. La portée et la nature « jeton » traversent le maillage en mode pairs (`PeerForwardedUser`), sinon un jeton scopé serait promu administrateur par le transfert vers le leader.
- **Audit des écritures MCP** : chaque `tools/call` d'écriture (réussi, en échec ou refusé pour portée manquante) est journalisé au niveau Warning avec l'acteur, l'outil et les *noms* des arguments, jamais leurs valeurs. Il passe donc par la persistance locale bornée des journaux système (5 000 lignes / 7 jours, non réconciliée) : c'est une trace d'exploitation, pas un journal d'audit infalsifiable.
- Suppression de flow via MCP/IA : sans `detachLibraries` (donc refusée si affecté).

## Versions et mises à jour
- Manifeste de release local embarqué par la CI, sans stockage en base ni réconciliation. `GET /api/v1/system/version` expose version/commit sans réseau ; `GET /api/v1/system/updates` expose les notes et la disponibilité d’une nouvelle version. Les deux routes gardent l’authentification par défaut (session ou Bearer, viewer/admin).
- MCP `get_version` et `get_updates` : mêmes données, opérations de lecture. Le processus exécutant la requête fait autorité pour sa propre version ; le routage MCP du mode Raft reste inchangé.
- Recherche du manifeste de la dernière release GitHub publique : timeout 10 s, taille 2 Mio, cache local 10 min. Réseau indisponible/manifeste invalide : état inconnu (`updateAvailable: null`), notes embarquées conservées. Pas d’installation automatique. Détails et schéma : [release](../../operations/release.md).

## Santé, métriques, journaux
- `/health/live` (sans dépendance), `/health/ready` (base locale ; critère de retrait d'un répartiteur), `/health/details` (base, migrations, workers en ligne, baux expirés > 2 min, file de sauvegardes, écriture et espace libre des volumes). `/metrics` Prometheus par serveur (état de cluster lu localement, peut diverger pendant la convergence ; `Operations:Metrics:Token` ou `Enabled=false`). **`/metrics` et `/health/details` sont sans authentification utilisateur par défaut : limiter au réseau d'exploitation.** Métriques de processus et de rétention locales, remises à zéro au redémarrage ; l'agent worker n'expose pas ses propres métriques.
- Journaux applicatifs : console JSON, `X-Correlation-Id` par requête, jamais la chaîne de requête ni les en-têtes.
- **Journaux système et nœuds** (distincts des journaux de commandes de pipeline) : tampon circulaire de 2 000 entrées par processus (`SystemLogRingBuffer`), masquage des secrets (`SystemLogSanitizer`), bruit de routine filtré, persistance SQLite **locale non réconciliée** des niveaux ≥ Warning (`system_logs`, 5 000 lignes / 7 jours). `GET /api/v1/system/logs[/stream|/categories]` (admin, SSE avec heartbeat 15 s, relais vers un nœud distant par `nodeId`), outil MCP `get_system_logs`, console dans l'interface.
- **Rétention des artefacts** : activée par défaut (`Operations:ArtifactRetention`, `Days`/`FailedDays` = 0, passe toutes les 10 min) : aucun artefact n'est conservé, une exécution échouée est purgée et sa reprise repart de zéro (journaux et métadonnées restent en base) ; épargne les exécutions actives, les publications non confirmées et les médias réservés ; `DryRun` ; `.encoding-results/` jamais touché.
- **Dossiers de travail des nœuds** (`executions/` et `transfers/` du dossier de données) : `WorkspaceCleanupService` les supprime une fois l'exécution terminée (sans délai pour réussie/annulée/échouée, 72 h si l'exécution est inconnue du serveur), jamais pour une exécution en file, en cours ou en annulation. Réglable par `Node:Workspace:*`. Sur un nœud sans fonction serveur, elle balaie aussi `runtime-artifacts/` (sur un nœud serveur, `ArtifactRetention` en reste seul responsable).
- Procédures : `docs/operations/monitoring.md`, `backup-restore.md`, `release.md`.

## Invariants
1. Une route nouvelle est fermée par défaut.
2. Aucun secret (jeton, mot de passe, clé de plugin) dans les journaux, erreurs, exports, ni réponses de configuration.
3. Les sessions ne sont jamais copiées entre serveurs ; les jetons API le sont.
4. Observabilité sans pollution : ne pas écrire de bruit de routine dans les journaux persistés.

## Non livré / limites
- SSO/OIDC, 2FA, métriques de l'agent, authentification de `/metrics` et `/health/details`, rotation automatique des jetons.
