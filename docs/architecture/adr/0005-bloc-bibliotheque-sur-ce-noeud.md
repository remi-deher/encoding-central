---
id: 0005
title: Bloc « Bibliothèque sur ce nœud » : brancher un flow selon l'accès direct
status: implemented
domain: flows
date: 2026-10-22
decided_by: Rémi (2026-10-22)
supersedes: []
superseded_by: []
tags: [nodes, relais, accès-direct, encodage, remux]
---

# 0005 — Bloc « Bibliothèque sur ce nœud »

## Contexte
Les bibliothèques sont montées directement sur certains nœuds (MEDIA 1, 2… sur .50 ; USB 1, 2… sur .51). Rémi veut que le nœud qui a le fichier le traite, et qu'un encodage soit confié à un nœud capable seulement si le nœud du fichier ne l'a pas en accès direct. Un flow pouvait exiger l'accès direct (`nodes-require-direct-access`) mais pas se brancher selon lui.

## Décision
- Plugin `nodes` 1.4.0 : composant `nodes-library-access` (condition, sorties `yes` « Oui » et `no` « Non », sans configuration). `yes` : le worker courant est le nœud d'accès du fichier et le monte directement. `no` : il l'a reçu par transfert.
- Pas de nouveau bloc de transfert : `no` se relie à `nodes-handoff` (autre worker éligible), suivi de `nodes-require-capability`. Le nœud d'accès reste celui qui publie.
- Remux ou changement d'en-tête : `nodes-require-direct-access` en début de flow, sans branche.
- Le nœud d'accès reste celui de la première attribution, même après un relais : après un relais, un second bloc répondrait `no` sur le nœud récepteur.

## Conséquences et risques
- Compatibilité : références `nodes` 1.0.0 à 1.3.0 restent résolubles ; un agent qui annonce `nodes` 1.3.0 ne reçoit pas un flow qui contient le bloc. Mettre à jour les nœuds ensemble.
- L'éligibilité d'attribution vérifie les capacités de toutes les branches du segment : un nœud qui monte la bibliothèque mais n'a pas ce que demande l'autre branche peut être écarté. Un élagage selon l'accès réel reste à faire.
- Tests : scénario `LibraryAccessBlockBranchesOnDirectAccess` (sortie du bloc selon l'accès). Non vérifié : exécution sur deux nœuds réels.
- Mise en file automatique (28/10/2026) : quand au moins un nœud monte la bibliothèque en direct, seul un serveur dont le nœud local détient cet accès la met en file automatiquement. Avant, le serveur d'un nœud en accès par transfert créait aussi des exécutions qu'il ne pouvait pas servir (flow exigeant l'accès direct) et elles restaient en file sans fin. Sans accès direct nulle part, tout serveur met en file. Test : `AutomaticQueueingTests`.
