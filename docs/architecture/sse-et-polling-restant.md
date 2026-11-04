# SSE et polling : état et reste à faire

Dernière mise à jour : 2026-11-03 (branche `dev`, commit `94c9b1f4` et suivants).

## Objectif

L'état du central (workers, bibliothèques, exécutions) doit se mettre à jour dans l'interface par **SSE**, sans rechargement de la page ni attente d'un intervalle. Le polling ne doit rester que là où le central ne peut pas annoncer le changement.

## Ce qui est en place

### Flux général `GET /api/v1/live`

- Code : `InterfaceLiveBroadcaster`, `InterfaceLiveEndpoints` (backend), `LiveUpdates` (frontend, `shared/services/live-updates.ts`).
- Le flux **ne porte pas les données** : il annonce qu'un sujet a changé, la page recharge ensuite ce sujet. Les annonces rapprochées sont fusionnées (anti-rebond de 300 ms côté client).
- Protégé par la session, comme les autres routes. Un `ping` toutes les 15 s ; si le flux se tait plus de 30 s, `drive()` redéclenche un rechargement lent de secours.
- Côté frontend : `live.drive(['workers', …])` émet tout de suite (chargement initial), puis à chaque changement d'un des sujets demandés.

### Sujets publiés

| Sujet | Publié par |
|---|---|
| `workers` | toute mutation réussie du groupe `/api/v1/workers` (enrôlement, approbation, drain, suppression…), sauf heartbeats et navigation de dossiers ; et `WorkerPresenceWatcher` (comparaison de la liste toutes les 2 s : worker qui s'éteint, revient, premier heartbeat) |
| `libraries` | toute mutation réussie des groupes `/api/v1/libraries` et `/api/v1/libraries/{library}` |
| `executions` | `ExecutionStreamBroadcaster.Publish`, donc chaque évolution d'exécution (progression comprise) |

L'observateur de présence existe parce que « en ligne » se calcule **à la lecture** à partir de `last_seen_at` : aucune requête de mutation ne correspond à un worker qui disparaît.

### Écrans déjà passés sur SSE

- Page **Workers** : liste et demandes en attente (`workers`) ; tâches actives globales, par worker et traitements (`executions` + `workers`). Plus aucun `timer`.
- **Aperçu du central** (`shared/services/central-overview.ts`) : `workers`, `libraries`, `executions`.

### SSE dédiés préexistants

- `/api/v1/executions/stream` et `/api/v1/executions/{id}/stream` (suivi des exécutions).
- `/api/v1/activity/stream` (activité des bibliothèques).
- `/api/v1/system/logs/stream` (journaux système).

## Ajouts de la PR #3 (branche `ao/sse-instead-polling/sse-live`)

- **Sujets** : `flows`, `plugins`, `settings` (publiés par `InterfaceLiveNotifier` après toute mutation réussie des routes correspondantes) et `execution-logs` (journaux de commandes). Workers, bibliothèques et exécutions gardent leurs sources ci-dessus.
- **Écrans passés sur SSE** : Bibliothèques (3 sondages), catalogue et éditeur de flows, accueil (exécutions), `flow-details`. `drive()` accepte `{ quietMs, refreshMs }`.
- **Workers** : `GET /api/v1/runtime/workers/{id}/wake` réveille les boucles de l'agent (claim, scans, remplacements, parcours de dossiers, transferts) ; le claim reste un POST transactionnel. Voir [runtime](domains/runtime.md) et l'[ADR 0041](adr/0041-sse-a-la-place-du-sondage.md).
- **Attentes de flow** : `ExecutionWaitService` dort jusqu'à la prochaine échéance réelle au lieu d'un passage toutes les 5 s.
- **Assistant IA** : `GET /api/v1/tools/ai/assistant/activity/{requestId}/stream` (repli sur l'interrogation).
- **Tests backend** : le diffuseur, la table route → sujet et le signal de réveil sont couverts (`InterfaceLiveTests`) ; l'observateur de présence ne l'est toujours pas.
- Les lignes de ces écrans dans les tableaux ci-dessous sont donc **faites** ; restent le suivi des exécutions (`execution-tracking`, `execution-detail-polling`), l'activité et les écrans IA/système (état externe).

## Polling qui reste

Relevé par recherche de `timer(`, `interval(` et `setInterval(` dans `frontend/src` (hors tests).

### À brancher sur le flux `live` (changements que le central connaît déjà)

| Fichier | Intervalle | Ce qui est relevé | Sujet à utiliser |
|---|---|---|---|
| `features/libraries/library-catalog-page.component.ts` (l. 245) | 5 s | traitement de la bibliothèque | `libraries`, `workers` |
| idem (l. 430) | 3 s | pipeline d'une exécution | `executions` |
| idem (l. 588) | 5 s | workflow de la bibliothèque | `libraries`, `executions` |
| `features/flows/flow-catalog-page.component.ts` (l. 561) | 5 s | liste des flows | à créer : `flows` |
| `features/flows/flow-editor-page.component.ts` (l. 385) | 5 s | validation serveur et statut de version | à créer : `flows` |
| `features/flows/editor/flow-details.component.ts` (l. 176) | 60 s | rafraîchissement en arrière-plan | à créer : `flows` |
| `features/home/home-executions.ts` (l. 34) | 10 s | aperçu des exécutions de l'accueil | `executions` |
| `features/activity/activity-page.component.ts` (l. 277) | `PAGE_REFRESH_MS` | liste d'activité (fusionnée avec des événements temps réel) | `executions` ou flux d'activité existant |
| `shared/services/execution-tracking.ts` (l. 71 et 126) | 5 s et 10 s | suivi des exécutions (le fichier ouvre aussi un `EventSource` ; le polling sert de repli quand le flux est silencieux) | `executions` ; vérifier avant de retirer le repli |
| `shared/services/execution-detail-polling.ts` (l. 50, 79, 102) | 5 s, 5 s, 3 s | événements, artefacts et pipeline d'une exécution ouverte | `/api/v1/executions/{id}/stream` |

### À brancher sur le flux, après création de sujets

Ces écrans lisent des états que le central modifie, mais **aucun sujet n'est publié** pour eux aujourd'hui.

| Fichier | Intervalle | Ce qui est relevé | Sujet à créer |
|---|---|---|---|
| `features/settings/tabs/ai-tools-tab.component.ts` | 15 s | connexions des outils IA | `ai` |
| `features/settings/tabs/ai-quota-panel.component.ts` | 60 s | quotas IA | `ai` |
| `features/settings/tabs/ai-session-history.component.ts` | 30 s | historique des sessions IA | `ai` |
| `features/ai/ai-provider-overview.component.ts` | 30 s | aperçu des fournisseurs IA | `ai` |
| `features/home/home-page.component.ts` (l. 309) | 30 s | aperçu IA, uniquement quand la vue IA est affichée | `ai` |
| `features/settings/tabs/system-tab.component.ts` | 60 s | état système | `system` |
| `features/updates/updates-page.component.ts` | 15 min | vérification de mises à jour | peut rester en polling |

### Polling qui doit probablement rester

Le changement est local à l'agent du worker ou à une opération en cours, pas un événement du central.

| Fichier | Intervalle | Pourquoi |
|---|---|---|
| `shared/services/local-worker-controller.ts` (l. 91) | 5 s | lit l'état **de l'agent** du worker (jumelage, demande en attente) : le central ne le publie pas. Il faudrait un flux côté agent |
| `features/setup/setup-wizard-page.component.ts` (l. 264) | 5 s | fiche matérielle du worker local pendant l'initialisation, avant qu'un central ne soit prêt |
| `shared/ui/library-worker-access-form.component.ts` (l. 725) | 400 ms | attente de la réponse d'une navigation de dossiers sur un worker (requête/réponse asynchrone) |
| `shared/ui/device-auth-dialog.component.ts` (l. 226) | `intervalSeconds` de l'invite | flux d'autorisation par code d'appareil : l'intervalle est imposé par le fournisseur |
| `core/auth/auth.interceptor.ts` (l. 38) | délai ponctuel | temporisation d'une nouvelle tentative, pas du polling |

## Points d'attention

- **Volume d'événements `executions`.** Chaque mise à jour d'exécution, progression comprise, publie le sujet. Le client fusionne sur 300 ms et `exhaustMap` ignore les événements pendant qu'une requête est en cours, mais la charge n'a pas été mesurée. Si besoin : n'annoncer que les changements d'état, ou regrouper côté serveur.
- **Interface relayée par un worker.** Le worker relaie `/api/v1/live` vers le central (`CentralUiProxy`, sans délai pour les flux SSE). Ce chemin n'a pas été vérifié avec une session ouverte.
- **Aucun test de bout en bout du flux.** Les tests couvrent `LiveUpdates` (faux `EventSource`) mais pas le backend (`InterfaceLiveBroadcaster`, `WorkerPresenceWatcher`) ni l'enchaînement mutation → événement → rechargement.
- **Heartbeats.** Ils ne publient rien volontairement ; c'est l'observateur de présence qui détecte le changement d'état.

## Ordre conseillé

1. Brancher la page Bibliothèques et l'accueil sur `libraries` / `executions` (sujets déjà publiés).
2. Remplacer le polling du détail d'exécution par `/api/v1/executions/{id}/stream`.
3. Créer les sujets `flows`, puis `ai` et `system`, et brancher les écrans correspondants.
4. Ajouter des tests backend sur le diffuseur et l'observateur de présence.
5. Décider si l'agent du worker doit exposer son propre flux pour retirer le polling de `local-worker-controller`.
