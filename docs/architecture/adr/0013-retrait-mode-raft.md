---
id: 0013
title: Retrait du mode de consensus SQL
status: implemented
domain: replication-storage
date: 2026-10-25
decided_by: Rémi (2026-10-25)
supersedes: []
superseded_by: []
tags: [sqlite, maillage, autonomie]
---

# 0013 — Retrait du mode de consensus SQL

## Contexte
Le produit vise deux serveurs autonomes avec SQLite local et convergence éventuelle. Le mode optionnel de consensus exigeait une majorité, répliquait les transactions SQL et désactivait l'écrivain commun. Il ajoutait un second modèle incompatible avec l'autonomie recherchée.

## Décision
Retirer le coordinateur de consensus, ses sources de données, ses gardes de terme, son transport, ses commandes d'exploitation, sa dépendance et ses tests/alertes/prototype dédiés. Le serveur conserve une seule source SQLite locale, l'écrivain commun et le maillage signé existant. Ne changer ni format du journal, ni échange, ni déduplication, ni chiffrement des secrets du maillage.

L'ancien indicateur d'activation reste uniquement reconnu pour refuser explicitement un démarrage dangereux, avant ouverture de la base. Aucune conversion de projection ni suppression de données existantes n'est automatique. Une installation utilisant l'ancien mode doit être sauvegardée et sa transition préparée séparément.

## Alternatives écartées
- Conserver deux moteurs : ambiguïté des invariants et maintenance inutile pour le cluster cible.
- Utiliser une majorité pour toutes les écritures : indisponibilité dès la perte d'un des deux serveurs.
- Ignorer l'ancienne activation : risque de démarrer des projections comme propriétaires indépendants.

## Conséquences et risques
Les baux, réservations et epochs restent locaux. Aucune reprise inter-serveurs n'est ajoutée par ce retrait. La future reprise manuelle à deux nœuds exige la neutralisation explicite de l'ancien propriétaire ; un timeout ne suffit pas. Les migrations appliquées sont conservées, y compris leurs commentaires historiques. Les protections, identités et clés du maillage restent en place ; les secrets locaux conservent Data Protection. Les anciennes sessions Plex du mode retiré ne sont pas converties.

Validation : compilation stricte et suites runtime, plugins, lanceur et scripts CI. La charge de 7 000 tâches est mesurée localement ; la validation sur deux serveurs et la migration d'une ancienne installation en production restent non vérifiées.
