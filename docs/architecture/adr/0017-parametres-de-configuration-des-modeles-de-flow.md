---
id: 0017
title: Paramètres de configuration portés par les modèles de flow
status: implemented
domain: flows
date: 2026-10-25
decided_by: Rémi (2026-10-25)
supersedes: []
superseded_by: []
tags: [flows, modèles, configuration]
---

# 0017 — Paramètres de configuration portés par les modèles de flow

## Contexte
Les flows créés depuis un modèle doivent garder des valeurs cohérentes, avec quelques paramètres ajustables au démarrage. Les champs exposés doivent être choisis par le propriétaire du modèle et révisables pour les créations futures. Les contrats d’exécution des plugins restent stables.

## Décision
- Chaque modèle conserve une politique de configuration par bloc et par champ, ses valeurs par défaut et une révision optimiste.
- Le propriétaire du modèle choisit les champs exposés à sa création et peut modifier plus tard la politique, les valeurs par défaut, le nom et la description. Une modification ne touche pas aux flows déjà créés.
- La création d’un flow préremplit les valeurs du modèle et n’accepte des dérogations que pour les champs autorisés, validés selon le schéma existant. Le résultat est un nouveau brouillon indépendant à ouvrir et publier.
- Les champs liés aux chemins et fichiers locaux, aux secrets et aux artefacts ne sont pas exposables. Leurs valeurs sont retirées du brouillon créé et restent à renseigner dans l’éditeur.
- La politique et sa révision sont réconciliées entre les nœuds avec l’entité du modèle. Aucun contrat d’exécution de plugin ne change.

## Alternatives écartées
- Laisser chaque créateur sélectionner des champs au déploiement : cela permettrait des flows divergents depuis le même modèle.
- Ajouter des contrats de configuration propres aux plugins : le choix des champs appartient au modèle et les schémas de composants existants fournissent déjà les contrôles et validations.

## Conséquences et risques
Une migration additive ajoute la politique et la révision à `flow_templates`. Les nouveaux flows sont sauvegardés comme brouillons; les chemins retirés peuvent rendre certains blocs incomplets jusqu’à leur configuration dans l’éditeur. Les dérogations restent bornées par la politique du modèle et le validateur central. La compatibilité en cluster sera vérifiée par les workflows de CI; aucun test local du backend n’a été exécuté dans l’environnement de développement courant.
