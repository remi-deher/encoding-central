---
id: 0020
title: Gabarit partagé de sous-navigation
status: implemented
domain: ui
date: 2026-10-26
decided_by: Rémi (2026-10-26)
supersedes: []
superseded_by: []
tags: [ui, navigation, shell]
---

# 0020 — Gabarit partagé de sous-navigation

## Contexte

Les pages avec `app-subnav` n’alignent pas uniformément leurs onglets, informations de contexte et actions. La sous-navigation doit rester centrée dans le contenu et suivre le défilement sous l’en-tête global, sans mélanger filtres et onglets de navigation.

## Décision

Toutes les pages avec `app-subnav` utilisent `SubnavTemplateComponent`. Le gabarit réserve la colonne centrale à la sous-navigation et expose des emplacements facultatifs d’information à gauche et d’actions à droite. Il masque les emplacements vides et garde la ligne collée sous l’en-tête du shell. Les règles métier des actions et des filtres restent dans les pages et leurs composants de domaine.

## Alternatives écartées

- Laisser chaque page positionner sa sous-navigation : l’alignement et le comportement au défilement divergent.
- Inclure les filtres `CategoryTabs` dans ce gabarit : ces groupes filtrent une vue et ne naviguent pas entre sections.

## Conséquences et risques

Les pages existantes migrent vers le même placement responsive ; les catalogues et paramètres conservent leurs panneaux et leurs données métier. Les onglets de la Vue d’ensemble restent centrés malgré l’identité du cluster et l’action contextuelle. Le rendu devra être vérifié visuellement sur des fenêtres étroites et lors du défilement.
