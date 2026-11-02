---
id: 0030
title: Migration des composants et plugins historiques vers le Store
status: accepted
domain: plugins
date: 2026-10-30
decided_by: Rémi (2026-10-30)
supersedes: []
superseded_by: []
tags: [store, composant, plugin, migration]
---

# 0030 — Migration des composants et plugins historiques vers le Store

Contexte : ADR [0029](0029-store-plugins-outils-hashes.md). La migration des outils binaires reste à terminer.

## Décision

Le dépôt public unique `remi-deher/encoding-store` distribue les plugins de processus et les manifestes des composants média. Aucun paquet plugin/composant n'est préinstallé par défaut. Les plugins Plex, Sonarr, Radarr et Jellyfin s'exécutent toujours dans un processus séparé sous Landlock. Les familles `video`, `audio`, `subtitles`, `mkv`, `images`, `probe` et `ai` utilisent un runtime `native` réservé à cette liste : le paquet fournit le manifeste descriptif, tandis que le code reste dans le moteur C# et passe par ses handlers existants.

Les manifestes fondamentaux `base`, `nodes` et `conditions` sont eux aussi distribués par le Store ; l’application conserve uniquement leurs handlers natifs C# et les garde-fous d’exécution. Les binaires média sont distribués en archives versionnées par plateforme, avec hash d'archive et de chaque exécutable, et sont associés aux composants par rôle d'outil et plage de versions. Les paquets Ubuntu Noble linux-x64 des quatre outils sont publiés ; l'image conserve leurs bibliothèques système mais retire les exécutables apt. Les autres plateformes restent à publier.

## Conséquences

- Les descriptions de composants et les scripts/manifests des quatre intégrations sont retirés du catalogue et de l'image de l'application ; le dépôt public conserve les sources, SDK, tests ou manifestes déplacés.
- Un flow qui référence l'une de ces familles exige son paquet présent sur le serveur qui l'édite/attribue et sur le worker qui l'exécute. L'installation et la synchronisation sont locales à chaque nœud.
- Les tests de comportement du moteur ne dépendent pas de la présence implicite d'un paquet Store ; ils chargent des fixtures déclaratives explicites. Les tests et sources Python des intégrations sont maintenus dans le dépôt Store.
- Le retrait des exécutables et des manifestes historiques est livré pour linux-x64. Les autres plateformes et les bibliothèques de runtime propres aux distributions restent à traiter.

## Limites

Le hash assure l'identité des octets distribués, pas leur provenance. La signature des catalogues/paquets, la publication des plateformes restantes et une validation sur plusieurs plateformes et matériels restent à faire.
