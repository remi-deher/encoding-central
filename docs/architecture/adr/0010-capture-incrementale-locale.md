---
id: 0010
title: Capture incrémentale locale par clés modifiées
status: implemented
domain: replication-storage
date: 2026-10-23
decided_by: Rémi, demande de mise en place des optimisations le 2026-10-23
supersedes: []
superseded_by: []
tags: [sqlite, capture, triggers, maillage, cohérence]
---

# 0010 — Capture incrémentale locale par clés modifiées

## Contexte
La capture relit et calcule les empreintes de nombreuses tables inchangées à chaque passage. Les curseurs temporels de certaines tables réduisent ce travail mais ne couvrent pas toutes les mutations ni les suppressions. L’ADR 0009 et les lots de capture bornés limitent la concurrence d’écriture ; il reste à réduire le travail lu et préparé. Rémi a demandé la mise en place des cinq optimisations, dont cette capture incrémentale, le 2026-10-23.

## Décision
- Des triggers SQLite versionnés marquent, dans la transaction métier, les clés insérées, modifiées ou supprimées et une révision locale par table. Deux tables **locales**, `cluster_sync_capture_tables` et `cluster_sync_dirty_entities`, sont créées par la migration additive 0049. Ce suivi n’est jamais envoyé aux pairs ni inclus dans les images Raft.
- La capture lit révisions, clés et lignes dans un Snapshot cohérent. Une table initiale ou invalidée est relue complètement ; ensuite seules les clés modifiées sont consultées. Les suppressions sont comparées à leur état connu même si aucune ligne source ne reste. Les dates métier ne sont plus une frontière d’exhaustivité.
- Les lots, événements, empreintes et séquences conservent leurs transactions. Les marqueurs de capture ne sont acquittés qu’après traitement complet, jusqu’à la révision observée ; une mutation concurrente plus récente reste en attente. Les candidats sont toujours revérifiés avant émission.
- Les clés composites sont normalisées avec le sérialiseur des états existants. Les reçus utilisent toujours le chemin média portable. Les changements d’inventaire invalident leur checkpoint local et les dépendances concernées. Un changement de cluster force une nouvelle capture initiale.
- Le catalogue des tables et les triggers doivent rester alignés, vérifiés par test. Toute nouvelle table réconciliée exige sa politique de capture et une nouvelle migration. Le format du journal, les origines, la déduplication et le protocole réseau sont inchangés.

## Alternatives écartées
- Cache mémoire des tables inchangées : perd les invalidations au redémarrage et ne suit pas tous les écrivains.
- Horodatages seuls : ne prouvent pas la détection des suppressions ni des écritures tardives ou antidatées.
- Nouveau flux de réplication de triggers SQL : inutile ; les événements métier existants restent l’unique format échangé.

## Conséquences et risques
Le suivi ajoute des écritures locales et des triggers à entretenir. Les clés restent en attente tant que la capture n’aboutit pas ; un grand retard ou une première capture peut encore consommer des ressources importantes. Les contraintes uniques, les dépendances et les conflits sont toujours gérés par les mécanismes existants. Le mode Raft conserve son coordinateur et exclut ces tables locales ; cette décision ne lui ajoute aucune garantie. Les tests synthétiques vérifient les lignes préparées, l’interruption, la concurrence et la convergence ; aucune mesure de charge ni validation entre images de versions différentes en production n’est revendiquée.
