---
id: 0039
title: Un worker doit avoir la version exacte du central
status: implemented
domain: cluster
date: 2026-11-02
decided_by: Rémi (2026-11-02)
supersedes: []
superseded_by: []
tags: [central, worker, version, compatibilité]
---

# 0039 — Un worker doit avoir la version exacte du central

## Contexte
Il n'y a plus de cluster ni de maillage : un central et ses workers (le central embarque le sien). La plage de protocole (`ClusterProtocol`, 0 à 1) ne protégeait que des évolutions que personne ne versionnait.

## Décision
- Un worker ne fonctionne que s'il a **exactement** la version du central. Aucune plage, aucune règle « centraux avant workers ».
- La version est celle de l'application (`AppVersion.Current`), fixée à la construction de l'image par `APP_VERSION` (`0.0.0-dev` par défaut ; version de release dans `release.yml`, `ci-<sha>` dans `image.yml`). Les deux images d'une même release portent donc la même valeur.
- **Enrôlement** : refusé (400, champ `version`) si la version diffère ou est absente.
- **Présence** : à chaque signal, un worker de version différente ou inconnue passe `incompatible` (raison visible) et ne reçoit plus de tâche, jusqu'à sa mise à jour.
- Migration 0069 : `cluster_nodes.app_version` remplace `protocol_version`.

## Conséquences
- Après une mise à jour du central, tous les workers sont incompatibles jusqu'à leur mise à jour.
- Deux images construites sans `APP_VERSION` portent `0.0.0-dev` : la règle ne protège pas des builds locaux divergents.
