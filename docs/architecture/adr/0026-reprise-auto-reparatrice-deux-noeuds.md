# ADR 0026 — Reprise auto-réparatrice à deux nœuds

- **Statut** : Accepté
- **Date** : 2026-10-30
- **Domaines** : `cluster`, `replication-storage`, `runtime`
- **Complète** : ADR 0014, ADR 0015, ADR 0016, ADR 0023, ADR 0025

## Contexte

Dans un cluster à deux nœuds, plusieurs garde-fous initialement conçus pour bloquer toute progression jusqu'à une action manuelle d'administrateur entraînaient des blocages complets lors de pannes, redémarrages ou divergences transitoires :

1. **Projections d'entités en diagnostic** : une divergence de JSON dans `flow_draft_backups` (`immutable_snapshot_conflict`) ou un conflit de nom d'utilisateur (`username_conflict`) laissait une ligne dans `cluster_sync_pending_projections`, bloquant indéfiniment `PrepareClaimsAsync`, `RecoverAsync`, la sonde `ready` et la barrière d'écriture source (`ClusterSourceWriteBarrier`).
2. **Redémarrage à froid lorsque le second nœud est hors ligne** : `PrepareClaimsAsync` refusait de passer `task_server_identity.ready` à `1` tant que le pair distant n'avait pas confirmé un rattrapage frais, empêchant le nœud redémarré de traiter sa propre file locale.
3. **Neutralisation, reprise et réadmission uniquement manuelles** : en cas de perte prolongée d'un des deux nœuds, les tâches possédées par le pair arrêté, les quarantaines concurrentes et les remplacements de source restaient bloqués jusqu'à des appels manuels successifs (`neutralize`, attente de 135 s, `recover` ou `force-source-retry`, puis `readmit` au retour du pair). De plus, une tâche en file sur le pair arrêté sans checkpoint capturé ne pouvait pas être reprise par `RecoverAsync`.
4. **Échecs définitifs sur indisponibilité temporaire d'un worker ou d'un serveur** : `FailUnavailableTargetsAsync` et `FailUnavailableSourcesAsync` mettaient en échec les relais, reprises et exécutions en attente lorsque la cible ou la source était momentanément hors ligne, et `HandoffsAsync` mettait l'exécution en échec définitif après trois tentatives de relais inter-serveurs infructueuses.
5. **Sondage mono-serveur des agents workers (`node-identity.json`) et perte de `LocalNodeRegistry.NodeId` au redémarrage** : lorsqu'un nœud rejoint un second serveur, son `node-identity.json` pointe uniquement vers le serveur d'accueil ; les deux workers n'interrogeaient alors plus qu'un seul serveur (`/claim`, `/transfers`, `/heartbeat`, `/library-scans/claim`), laissant les exécutions et relais (`handoff`) créés dans la table locale `execution_work` de l'autre serveur indéfiniment en file. De plus, `LocalNodeRegistry.NodeId` restait nul après redémarrage d'un conteneur déjà enrôlé, et l'admission racine du serveur fondateur (`node_id IS NULL`) empêchait la détection d'arrêt du nœud fondateur. Enfin, une exécution en `cancelling` mise en quarantaine conservait sa réservation locale `media_reservations`.

## Décision

### 1. Projections non bloquantes et convergence déterministe des sauvegardes de brouillon

- `flow_draft_backups` converge selon l'ordre déterministe LWW du journal (`CompareVersion`) en préservant le statut local (`status`, `last_checked_at`, `last_error`) et l'empreinte JSON locale lorsque la définition est sémantiquement identique, sans produire `immutable_snapshot_conflict`.
- Les lignes de `cluster_sync_pending_projections` ayant déjà fait l'objet d'un diagnostic enregistré dans `cluster_projection_diagnostics` ne bloquent plus `TaskOwnershipRepository.PrepareClaimsAsync`, `ReadinessBlockersAsync`, `ClusterTaskCoordinator.RecoverAsync`, la sonde `ready` de `ClusterTaskCoordinationEndpoints`, ni `ClusterSourceWriteBarrier.CheckAsync`.

### 2. Autonomie au redémarrage lorsque le pair est hors ligne

- Lorsqu'un serveur redémarre (`ready = 0`), une fois ses files locales (`task_ownership_inbox`, `task_coordination_inbox`, projections applicables) vidées, un pair serveur dont le nœud `cluster_nodes` associé est hors ligne (`last_seen_at <= clock_timestamp() - interval '45 seconds'`, y compris lorsque `cluster_peer_admissions.node_id` est nul pour le serveur fondateur) ne bloque plus le passage à `ready = 1`.

### 3. Auto-neutralisation, auto-reprise et auto-réadmission à deux serveurs

- Dans `ClusterTaskMaintenance.ObserveAsync` (`HealTwoServerClusterAsync`), sur un cluster à deux serveurs :
  - **Auto-réadmission** : tout pair neutralisé par le serveur local qui a repris contact et confirmé son rattrapage (`HasFreshCatchUp`) est automatiquement réadmis via `ReadmitAsync` (avec capture du vecteur local avant `barrier.VerifyAsync` pour éviter toute course avec les écritures concurrentes de présence).
  - **Auto-neutralisation** : si l'unique autre serveur admis a son nœud `cluster_nodes` hors ligne depuis plus de 90 s, n'a aucun échange authentifié récent (`!HasFreshAuthenticated(45s)`), et bloque des tâches actives/en file, des quarantaines ou des écritures source en attente, le serveur survivant journalise automatiquement sa neutralisation.
  - **Auto-reprise** : après le délai de grâce de 135 s suivant la neutralisation, le serveur survivant déclenche automatiquement `RecoverAsync` pour les tâches actives du pair neutralisé (au checkpoint si le cache vérifié est présent, ou depuis la source à la révision 0 si aucun checkpoint n'avait été émis et qu'aucune étape non idempotente ni publication n'a eu lieu), et la projection `DeduplicateAsync` lève automatiquement les quarantaines éligibles tout en libérant les réservations locales `media_reservations` et `disk_locks` des perdants. Les exécutions en `cancelling` dont le bail est expiré sont clôturées en `cancelled` même si leur propriété est en quarantaine.
- Lorsque le propriétaire distant d'une exécution est neutralisé et injoignable, `ExecutionRepository` (`CancelAsync`, `RetryAsync`, `ResumeAsync`) autorise l'opération localement sur le survivant.

### 4. Attente auto-réparatrice des relais, reprises et sources

- `RuntimeRecoveryService.HasEligibleWorkerAsync` évalue l'éligibilité des workers candidats sans les exclure sur une simple expiration de `last_seen_at` : un relais, une reprise ou une relance vers un worker temporairement hors ligne attend son retour au lieu de mettre l'exécution en échec, et n'échoue que si aucun worker non supprimé du cluster ne possède les capacités, la capacité (`max_concurrent_tasks > 0`) ou l'accès requis.
- `RuntimeRepository.UnavailableSourcesSql` ne met en échec une exécution que si aucun accès direct activé sur un nœud non supprimé n'existe pour la bibliothèque, sans échouer sur un simple retard de heartbeat du nœud source.
- `ClusterTaskMaintenance.HandoffsAsync` n'initie `MoveAsync` que lorsque le serveur cible a confirmé son rattrapage (`HasFreshCatchUp`), sans faire échouer définitivement l'exécution après trois essais.

### 5. Sondage multi-serveurs des workers et réenregistrement automatique du nœud local

- `PUT /api/v1/nodes/{nodeId}/heartbeat` réenregistre `LocalNodeRegistry` lorsque le secret local du conteneur est présenté (avec repli SQL dans `ClusterTaskCoordinator` et `LibraryWorkflowRepository` tant que le premier heartbeat n'a pas eu lieu) et retourne dans `NodeResponse.ClusterServerUrls` la liste des URLs des serveurs du cluster.
- `WorkerIdentityStore` conserve `KnownServerUrls` et expose `GetCandidateServerUrls` (serveur local en boucle locale d'abord, puis `ApiBaseUrl`, puis les autres serveurs connus du cluster).
- `NodeHeartbeatService`, `RuntimeWorkerService`, `RuntimeTransferService`, `LibraryDiscoveryService`, `SourceReplacementService`, `NodeDirectoryBrowserService`, `ResourcePrefetchWorker`, `WorkspaceCleanupService` et `PluginSyncService` interrogent l'ensemble des serveurs candidats du cluster et lient chaque bail/transfert au serveur propriétaire qui l'a attribué.

## Garanties d'intégrité préservées

- Aucune étape à effet externe incertain (`!idempotent`) interrompue n'est rejouée automatiquement.
- Aucune publication confirmée ou incertaine n'est écrasée ni dupliquée.
- Les baux et réservations restent strictement locaux au serveur propriétaire ; toute reprise incrémente l'époque et exclut l'ancien propriétaire.
