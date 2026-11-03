---
id: 0041
title: SSE à la place du sondage, pour l'interface et pour les workers
status: accepted
domain: runtime
date: 2026-11-03
decided_by: Rémi (2026-11-03)
supersedes: []
superseded_by: []
tags: [sse, temps-reel, workers, interface]
---

# 0041 — SSE à la place du sondage

## Décision
Le central annonce les changements par SSE (`text/event-stream`, sans WebSocket, comme le reste du temps réel) ; interface et workers n'interrogent plus à intervalle fixe.

- **Interface** : `/api/v1/live` (`InterfaceLiveBroadcaster`). Un middleware publie un sujet après toute mutation réussie (`InterfaceLiveNotifier`) : les endpoints n'ont pas à le connaître. Le flux ne porte pas les données, la page recharge le sujet.
- **Workers** : `/api/v1/runtime/workers/{id}/wake`. Le flux est un **signal de réveil** : il n'attribue rien, ne transporte rien, et le worker réclame par les POST transactionnels existants. Baux, époques, fences et idempotence sont inchangés.

## Garde-fous
- Un `claim` n'est jamais annoncé : il répond 204 sans travail, et l'annoncer réveillerait les workers en boucle.
- Le délai d'avant reste le secours : flux coupé ou muet, chaque boucle reprend son délai ; flux connecté, il devient un filet de 30 s contre un signal perdu.
- Une boucle qui termine un travail local se réveille elle-même, sinon un worker saturé n'en reprendrait pas avant le filet.
- Les attentes de flow suivent la même logique côté central : échéance réelle plutôt qu'intervalle fixe, réveil anticipé par les mêmes signaux.
- La progression d'une demande de l'assistant IA suit le même principe, par un flux propre à la demande, avec repli sur l'interrogation.
- Le sondage reste là où aucun signal n'existe : vivacité d'un worker (battements de cœur et expirations), santé, quotas et fournisseurs IA, mises à jour, assistant de configuration avant authentification.

## Conséquences
Latence d'attribution et de rafraîchissement ramenée de plusieurs secondes à quelques centaines de millisecondes, et requêtes à vide supprimées. Les proxys doivent désactiver la mise en tampon SSE (déjà exigé, voir [reverse-proxy](../../operations/reverse-proxy.md)) et ne pas couper un flux inactif avant 15 s.
