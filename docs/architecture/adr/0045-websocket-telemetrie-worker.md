---
id: 0045
title: WebSocket de télémétrie worker → central
status: accepted
domain: runtime
date: 2026-11-04
decided_by: Équipe (branche feat/worker-telemetry-ws, 2026-11-04)
supersedes: []
superseded_by: []
tags: [websocket, télémétrie, temps-reel, workers, sse]
---

# 0045 — WebSocket de télémétrie worker → central

## Contexte
Une exécution d'encodage dure des minutes. Jusqu'ici, le worker ne remontait sa progression que par les POST transactionnels (`steps/complete`, `renew`) et l'interface la récupérait par sondage (5 s / 3 s, `execution-detail-polling.ts`) ou par le flux SSE de suivi. Le flux SSE existant central → worker (`/wake`, ADR [0041](0041-sse-a-la-place-du-sondage.md)) est unidirectionnel : il ne peut pas porter de télémétrie worker → central. Le contrat des trames est fixé dans `backend/src/Encoding.Contracts/Executions/WorkerTelemetryContracts.cs`.

## Décision
- Un seul **WebSocket** `GET /api/v1/runtime/workers/{workerId}/socket` (en-tête `X-Node-Token`), trames JSON texte en camelCase avec un discriminant `type`. Worker → central : `hello`, `step`, `progress`, `log`, `pong`. Central → worker : `ready`, `wake`, `ping`. Le socket porte aussi le **signal de réveil** ; le SSE `/wake` reste en **secours** (worker d'une version antérieure, ou socket refusé par un proxy).
- Le WebSocket ne porte que de la **télémétrie « au mieux »** (best-effort) : étapes (`started` / `completed` / `failed`), progression de commande, journaux en direct. **Aucune donnée transactionnelle** : `claim`, `renew`, `steps/start`, `steps/complete`, `finish` et `source-failure` restent des POST HTTP avec ack, retry et fencing par l'époque du bail ; la persistance des journaux reste dans `renew` et `steps/complete`.
- Le central conserve la télémétrie **en mémoire** (hub `IExecutionTelemetry`, rien de durable) et la rediffuse à l'interface par SSE authentifiées par session : `/api/v1/executions/{id}/telemetry` (snapshot à l'ouverture, puis `step` / `progress` / `log` / `end`) et `/api/v1/workers/{id}/telemetry` (exécutions en cours du worker). Pas de WebSocket navigateur.
- Cadence et limites : au plus **2 trames `progress` par seconde et par commande** ; journaux regroupés **toutes les 250 ms ou 64 lignes**, avec un `seq` croissant par commande pour détecter les trous ; taille des trames plafonnée côté central.

## Alternatives écartées
- **Complétion ou télémétrie sur le SSE existant** : le SSE est unidirectionnel (central → worker ici) ; un résultat aurait besoin d'un HTTP request de toute façon, et le POST offre l'ack, le retry et l'écriture transactionnelle gardée par l'époque du bail. Sa latence (quelques ms en LAN) est négligeable devant des tâches de plusieurs minutes.
- **WebSocket navigateur pour l'interface** : les flux SSE de l'interface sont déjà authentifiés par session et servis sans tampon (`proxy_buffering off`) ; un socket navigateur ajouterait un chemin d'authentification et de proxy de plus pour aucun gain.
- **Sondage conservé** : plusieurs secondes de latence sur la progression et les journaux d'une exécution longue, et des requêtes à vide.

## Conséquences et risques
- La télémétrie est **jetable** : socket coupé = trames perdues, sans conséquence sur la vérité — baux, étapes et journaux persistés restent les POST transactionnels. Le worker rouvre le socket avec le même backoff que le SSE `/wake` (1 s → 60 s, jitter complet, voir ADR 0041) ; le central envoie `ping` toutes les 15 s, le worker répond `pong`, puis `hello` → `ready` à la reconnexion.
- Le hub est borné : les abonnés SSE lents perdent les événements les plus anciens ; une exécution terminée est oubliée (`end`).
- **Proxys** : le chemin `/api/v1/runtime/` doit autoriser l'upgrade WebSocket — nginx : en-têtes `Upgrade` / `Connection` via `map $http_upgrade` (émis par le générateur `proxy-config.ts`) ; Nginx Proxy Manager : case « Websockets Support » ; Caddy et Traefik gèrent l'upgrade nativement. Non vérifié derrière un vrai reverse proxy en production.
- Versions mixtes : un worker ancien garde le SSE `/wake`, le central accepte les deux ; sans socket ouvert, aucune télémétrie temps réel n'existe pour ce worker.
- Redémarrage du central = perte de la télémétrie en cours (les exécutions continuent ; la vérité est en base). Non vérifié en charge : débit de trames d'un worker bavard, reconnexion simultanée de plusieurs workers.
