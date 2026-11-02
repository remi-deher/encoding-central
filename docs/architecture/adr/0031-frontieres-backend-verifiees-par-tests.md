---
id: 0031
title: Frontières du backend vérifiées par des tests d'architecture
status: accepted
domain: runtime
date: 2026-05-20
decided_by: Rémi (demande d'audit et de correction)
supersedes: []
superseded_by: []
tags: [architecture, tests]
---

# 0031 — Frontières du backend vérifiées par des tests d'architecture

## Contexte
L'audit a montré des endpoints couplés au stockage (`ClusterTaskEndpoints` : SQL et transactions), une méthode de 950 lignes (`RuntimeFlowRunner.RunAsync`) et aucun test garantissant les couches.

## Décision
`ArchitectureTests` impose : références de projets autorisées, pas de type de stockage dans les endpoints, `Contracts`/`Core` sans stockage, lancement de processus en liste fermée, plafond de 600 lignes, endpoints sans accès fichier direct. `RunAsync` est décomposé en `RuntimeFlowExecution` (une méthode par famille d'étape) sans changement de comportement.

## Alternatives écartées
Analyseur Roslyn dédié : plus lourd, même résultat. Découpage immédiat de tous les gros fichiers : trop risqué en un lot.

## Conséquences et risques
Les dix fichiers qui dépassaient 600 lignes sont scindés en classes partielles par responsabilité (aucun changement de comportement) ; la table de plafonds est vide et tout nouveau dépassement fait échouer le test. Les endpoints servent les fichiers via `FileResults`. Le refactor de `RuntimeFlowRunner` est validé par la suite existante, pas par un nouveau scénario de bout en bout.
