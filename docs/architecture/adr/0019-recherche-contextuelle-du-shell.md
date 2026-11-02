---
id: 0019
title: Recherche contextuelle dans le shell
status: implemented
domain: ui
date: 2026-10-26
decided_by: Rémi (2026-10-26)
supersedes: []
superseded_by: []
tags: [shell, recherche, navigation]
---

# 0019 — Recherche contextuelle dans le shell

## Contexte

Le shell adaptatif et les palettes Material partagées sont définis par l’ADR 0018. Il reste à intégrer la recherche et les commandes de chaque page dans une barre commune sans déplacer les règles métier vers le shell.

## Décision

Le shell global fournit une barre de recherche composite, centrée et stable dans la zone de contenu, avec une feuille de résultats directement accolée. À viewport et état de navigation identiques, sa largeur est stable entre les routes ; elle s’adapte au viewport et aux largeurs de sidebar définies par le shell. Elle réutilise `SearchFieldComponent`. Une page peut enregistrer ses résultats, jusqu’à deux actions et un filtre via `PageShellContextService`. Le shell place les résultats de la page avant les destinations globales et n’implémente pas les règles métier de recherche ou de filtrage. Les actions et le filtre apparaissent dans le même composant que la recherche, sans séparateurs ni espace vide lorsque ces commandes ne sont pas présentes.

## Alternatives écartées

- Conserver la recherche en modale : elle ne s’intègre pas aux actions de page et détache visuellement les résultats du champ.
- Déplacer les filtres et actions dans une barre distincte : cela rendrait leur emplacement dépendant de chaque page.
- Ajouter les règles métier au shell : cela couplerait la structure commune aux catalogues, aux flows et aux traitements.

## Conséquences et risques

Les fonctionnalités existantes gardent la responsabilité de leurs données et peuvent adopter le contrat du shell page par page. Tant qu’une page n’enregistre pas de contexte, seules les destinations globales apparaissent et aucun bouton contextuel n’est rendu. Les modes d’authentification et l’éditeur plein écran conservent leurs présentations dédiées.
