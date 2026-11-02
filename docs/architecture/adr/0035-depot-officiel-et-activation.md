---
id: 0035
title: Dépôt officiel fixe et activation explicite
status: implemented
domain: plugins
date: 2026-11-01
decided_by: Rémi (2026-11-01)
supersedes: []
superseded_by: []
tags: [plugins, store, worker]
---

# 0035 — Dépôt officiel fixe et activation explicite

## Contexte
Le Store pouvait désactiver son dépôt officiel, et l’absence de préférence valait activation (ADR [0021](0021-activation-cluster-plugins-outils.md)). L’image worker présentait les mêmes actions que le central. Le dépôt par défaut est `https://github.com/remi-deher/encoding-store`.

## Décision
Le dépôt officiel est `https://github.com/remi-deher/encoding-store`. Il reste actif et ne peut être ni désactivé ni supprimé. Les autres dépôts s’ajoutent, se désactivent et se suppriment. Une adresse `github.com/propriétaire/dépôt` est lue comme `repository.json` sur `main`. Aucun paquet n’est installé par défaut. Un plugin autre que `base` et `nodes`, et chaque outil média, restent inactifs tant qu’un administrateur ne les active pas. L’image worker ne fait que lire les listes : son relais refuse les écritures du Store, des plugins et de l’activation des outils.

## Alternatives écartées
Garder l’activation par défaut : le central démarrerait avec des outils utilisables avant un choix. Autoriser la suppression du dépôt officiel : le catalogue de référence pourrait disparaître. Laisser le worker modifier le cluster par le relais de session : la gestion ne serait plus réservée au central.

## Conséquences et risques
Un cluster déjà en service qui n’a jamais enregistré d’activation voit ses plugins non essentiels et ses outils devenir inactifs au déploiement de cette version. `base` et `nodes`, une fois installés, restent actifs. Le dépôt officiel publie plugins et outils média dans le même `repository.json`. Un autre dépôt peut être ajouté.
