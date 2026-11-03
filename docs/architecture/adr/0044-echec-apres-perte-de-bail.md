---
id: 0044
title: Échec après perte de bail sans réattribution automatique
status: implemented
domain: runtime
date: 2026-11-03
decided_by: Rémi (2026-11-03)
supersedes: [0004, 0026]
tags: [bail, worker, relance, intégrité]
---

# 0044 — Échec après perte de bail

## Contexte
La reprise automatique au checkpoint, jusqu'à deux fois sous le même identifiant,
contredisait l'interdiction de réattribution silencieuse dans AGENTS.md.

## Décision
Après expiration du bail d'un worker, la tentative échoue avec `worker_perdu`.
Les réservations et verrous sont libérés ; la source et les artefacts restent conservés.
L'ancien worker ne peut plus écrire ni publier. Une annulation en cours devient annulée.
Une reprise exige une nouvelle exécution depuis l'entrée, demandée par l'opérateur
ou autorisée par la règle bornée `nodes-retry-on-worker-loss` du flow ; les restrictions
sur les effets non idempotents restent applicables. Les relais et attentes explicites restent inchangés.

## Alternatives écartées
La reprise automatique au checkpoint masque le changement de worker et conserve une tentative interrompue active.

## Conséquences et risques
La colonne historique `resume_count` reste inutilisée ; aucune migration appliquée n'est modifiée.
La détection d'expiration conserve sa boucle d'une seconde : son passage à un minuteur
événementiel dépasse cette correction. Les scénarios multi-processus et les écritures
média réelles restent à vérifier en CI.
