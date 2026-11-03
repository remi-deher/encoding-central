---
id: 0040
title: Vocabulaire central / worker, fin des notions de cluster et de nœud
status: accepted
domain: cluster
date: 2026-11-02
decided_by: Rémi (2026-11-02)
supersedes: []
superseded_by: []
tags: [vocabulaire, api, renommage]
---

# 0040 — Vocabulaire central / worker

## Décision
Il n'y a plus de cluster ni de nœud : un **central** et des **workers**. Le renommage se fait par couches vérifiables.

## État
- **Fait** : textes visibles (interface, messages d'erreur, descriptions MCP) ; routes de l'API (`/api/v1/workers`, `/worker-groups`, `/local-worker`, `/runtime/workers`, `/central`, `/libraries/{id}/worker-access`, `/auth/setup/worker`, `/tools/ai/central`, `/local-worker/centrals`) ; pages de l'interface (`/workers`, `/worker-groups`).
- **Fait (noms internes)** : clés de configuration et variables d'environnement (`Worker__*` / `Worker:*` à la place de `Node__*`, `WorkerPresence`, `Central__*` / `Central:*` à la place de `Cluster__*`, `ConnectionStrings:Central`) ; 92 types C# (`Node*` → `Worker*`, `Cluster*` → `Central*`, `AiCluster*` → `AiCentral*`) et les fichiers correspondants. Le type local `WorkerEnrollmentRequest` de l'agent devient `JoinCentralRequest`. Le fichier de base garde le nom `cluster.db`.
- **Fait (fonctions)** : un worker n'a qu'un central (plus de changement en deux phases) ; plus d'état « autonome » ni de création du central ; mode de cession `server` retiré du moteur. Sessions IA : plus de coordinateur, de reprise ni d'adoption. Plus de clés d'identité ni de clé de données propres au central, plus de panneaux de réplication (migration 0070 : les anciennes données IA chiffrées sont supprimées).
- **Fait (schéma, projet, contrats)** : tables et colonnes SQLite (migration 0072 : `workers`, `central_identity`, `worker_*`, `library_worker_*`, `worker_id`, `source_worker_id`, `worker_token_hash`…) ; `execution_steps.node_id` et `execution_waits.node_id` (blocs de flow) deviennent `flow_node_id` ; projet `Encoding.WorkerAgent`, espaces de noms et dossiers `Workers/` ; propriétés des contrats JSON (`workerId`, `worker`, `workers`, `executorWorkerId`, `workerToken`, `centralId`, `centralName`, `workerLimits`, `configuredWorkerCount`…) côté C# et TypeScript ; noms des outils MCP (`list_workers`, `get_worker_identity`, `save_worker_processing`…) ; clés DNS-SD `centralId`/`centralName`.
- **Pas fait** : variables locales et paramètres internes qui s'appellent encore `node`/`nodeId`/`cluster` (sans effet visible), types et fichiers TypeScript nommés `Node*`/`cluster*`/`local-node*`, `NodeRole`, `ApiTokenReplicationNode` (supprimé), noms d'index SQLite (`ix_cluster_nodes_…`), tag OpenAPI « Cluster », identifiants de composants du plugin `nodes` (externe), clé de configuration `targetNodeId` des blocs `nodes-*` (externe, dépôt `encoding-store`).
- **À ne pas renommer** : « node » au sens de bloc d'un graphe de flow (`FlowNodeDefinition`, `FlowNodeId`), identifiant de plugin `nodes` et ses composants `nodes-*`.

## Conséquences
Les clients de l'API (jetons, scripts, MCP) doivent utiliser les nouvelles routes, et les déploiements existants doivent renommer leurs variables d'environnement (`Node__Name` → `Worker__Name`, etc.). Les anciennes routes et variables ne sont ni redirigées ni lues.
