---
id: 0018
title: Shell adaptatif et palettes Material partagées
status: implemented
domain: ui
date: 2026-10-26
decided_by: Rémi (2026-10-26)
supersedes: []
superseded_by: []
tags: [angular, material, shell, theme, accessibilite]
---

# 0018 — Shell adaptatif et palettes Material partagées

## Contexte

L’audit du 26 octobre relève des surcharges des internes de MatSidenav, deux jeux de couleurs indépendants, des titres invisibles et des divergences dans les cartes de plugins. Le propriétaire demande l’application du shell proposé et plusieurs thèmes de couleur.

## Décision

Le shell standard est un scaffold CSS avec une navigation persistante de 248 px, repliable en rail de 88 px, et une navigation basse. Les deux largeurs sont des règles du shell, inchangées entre les routes et non redéfinies par les gabarits métier. Le rail s’adapte aux écrans de 761 à 1 100 px. Les interactions superposées continuent à utiliser Material/CDK ; le graphe et l’accès public gardent leurs cadres spécialisés. Les gabarits partagés affichent les titres et les actions de page.

Forêt, Océan, Iris et Rose exposent les rôles système Material. Les alias applicatifs et contrôles Material consomment les mêmes couleurs. Les composants ne sont thémés qu’une fois : seules les variables système changent par palette/mode. La couleur est indépendante de la luminosité et persiste localement sans appel serveur.

L’assistant est porté par un composant dédié ; les vues de conversation et d’édition sont différées. La géométrie des tiroirs est centralisée ; les verrous modaux sont suivis par propriétaire et la fermeture protégée précède la fermeture effective.

## Alternatives écartées

- Réécriture générale des pages : conserver les gabarits et comportements testés limite les régressions.
- Dupliquer chaque thème de composant pour chaque palette : les variables système évitent de multiplier le CSS.
- Forcer MatSidenav pour une navigation persistante : le cadre CSS ne dépend pas de sa structure interne.

## Conséquences et risques

Aucun contrat backend, stockage partagé ou protocole de cluster ne change. Les choix d’apparence restent propres au navigateur. Les modifications des primitives nécessitent une vérification de navigation, accessibilité, ouverture/fermeture des tiroirs et saisie. Les grandes pages métier restent à découper progressivement selon leurs responsabilités ; cet ADR ne prétend pas achever cette refonte.

La validation locale et ses limites sont consignées dans `docs/design/audit-ui-2026-10-26/implementation.md`. Safari, Firefox et la recette avec backend réel restent hors vérifications locales.
