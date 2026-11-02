---
id: 0025
title: Libération d'une reprise source après neutralisation
status: implemented
domain: runtime
date: 2026-10-29
supersedes: []
superseded_by: []
---

# 0025 — Libération d'une reprise source après neutralisation

## Constat

Une tentative créée depuis la source peut rester en quarantaine parce qu'elle a chevauché une exécution précédente dont le propriétaire a ensuite été neutralisé. Le checkpoint de l'ancienne exécution peut être absent ou inutilisable ; la reprise au checkpoint ne débloque donc pas la tentative source déjà prête. Les utilisateurs ne devraient pas avoir à éditer la base ni à recréer la sélection.

## Décision

La déduplication déterministe lève ce seul conflit lorsque toutes ces conditions sont réunies : le candidat est encore `queued`, révision 0, jamais démarré et sans publication ; l'exécution gagnante n'a ni réussi ni publié ; son propriétaire est neutralisé depuis au moins 135 secondes. La tentative source existante redevient alors admissible. La même projection est appliquée par tous les pairs à partir du registre de neutralisation réconcilié.

La page Activité expose cette opération aux administrateurs sur la tentative source en quarantaine. L'action exige une attestation explicite de l'arrêt du serveur propriétaire et de tous ses workers, plus un motif conservé dans le registre signé de neutralisation. Elle ne requiert pas de checkpoint et ne crée pas de tentative : elle débloque uniquement la tentative source existante. La projection réévalue les quarantaines à l'échéance même si aucun nouvel événement n'arrive.

Les conflits avec une exécution réussie ou une publication confirmée ou incertaine restent en quarantaine. Une autre tentative susceptible de publier bloque également l'action manuelle. Les autres exécutions ne sont pas modifiées. La neutralisation reste une attestation administrative explicite de l'arrêt de l'installation propriétaire et de ses workers ; l'observation de présence ne la remplace pas.

## Conséquences

Le cas récupérable converge sans checkpoint ni modification manuelle de base, puis la tentative déjà en file peut repartir depuis la source. La neutralisation conserve sa grâce et ses garanties. Les cas avec publication ou exécution commencée nécessitent toujours une résolution distincte ; cette décision ne les force pas.
