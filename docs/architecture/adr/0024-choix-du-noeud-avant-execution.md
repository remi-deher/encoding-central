---
id: 0024
title: Choix du nœud avant exécution et minimisation des transferts
status: implemented
domain: runtime
date: 2026-10-29
supersedes: []
superseded_by: []
---

# 0024 — Placement avant exécution

## Décision

Le planificateur de placement applique les priorités suivantes aux workers annoncés et compatibles avec la bibliothèque :

1. Un worker à accès direct qui peut exécuter tous les chemins retenus prend le flow entier (aucun transfert du média).
2. À défaut, un worker capable d'exécuter le flow entier reçoit le média par le mécanisme de transfert existant.
3. À défaut, les nœuds du graphe sont attribués à des workers compatibles. Le planificateur minimise exactement le nombre de transitions entre workers sur les arcs retenus. Chaque transition devient une passation au checkpoint durable correspondant.
4. Si aucun placement ou découpage n'est possible, l'exécution échoue avec le code `placement_impossible` et les capacités absentes dans le plan structuré.

Les contraintes des flows restent prioritaires : `nodes-run-on-node` contraint son étape, `nodes-handoff` conserve sa destination, `nodes-require-capability` filtre les capacités et encodeurs vérifiés, `nodes-require-direct-access` maintient l'accès direct jusqu'au prochain relais explicite, et `nodes-library-access` conserve ses sorties métier. Une destination imposée ne reçoit aucun repli vers un autre worker.

## Branches conditionnelles

Les conditions natives évaluables à partir de `before_metadata` sont réduites au chemin déterminé par les médias observés. Les branches dépendant de l'identité du worker ou d'une valeur qui ne peut pas être connue au plan restent toutes dans le graphe conservateur. Un seul worker est choisi pour chaque nœud ; l'optimisation minimise la somme des transitions sur les arcs de ces branches possibles. Au runtime, seule la branche sélectionnée s'exécute. Si l'espace de recherche dépasse 500 000 affectations, le plan retourne l'erreur structurée `placement_search_limit` au lieu de prétendre qu'un optimum a été trouvé.

## Persistance et reprise

Le calcul initial est une lecture seule lors de l'admission ; il ne modifie pas `execution_work` dans le chemin de réclamation. Après le claim et sous son bail, le worker demande la persistance du plan calculé ; cette écriture séparée ne fait pas partie de la transaction de réclamation. Quand le worker rapporte les métadonnées sources pour la première fois, le serveur recalcule une seule fois avec les branches alors déterminées et persiste le plan final dans la colonne locale `execution_work.placement_plan` (migration additive 0062). Les checkpoints et reprises de la même exécution utilisent les affectations persistées. Une nouvelle tentative depuis l'entrée recalcule son plan pour le nouveau cycle et les capacités disponibles. Aucun recalcul périodique ne change silencieusement les décisions.

Les tâches dont le plan est impossible restent soumises au délai d'admission existant avant l'échec précoce. Le claim n'écrit pas un plan ni un échec de placement. Les baux, époques, réservation du média, validation des artefacts, empreintes, quarantaines et gardes de publication gardent leurs contrôles existants.

## Validation et limites

Le planificateur et le routage des checkpoints sont couverts par les tests runtime ciblés, y compris accès direct, worker complet par transfert, conditions déterminées, découpage vers un worker spécialiste et refus quand aucun worker n'est compatible. Le code ne promet pas une validation sur TrueNAS ou avec des accélérateurs physiques ; celle-ci reste distincte des tests synthétiques.
