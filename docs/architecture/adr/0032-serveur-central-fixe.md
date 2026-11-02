---
id: 0032
title: Serveur central fixe et workers qui ne deviennent pas serveurs
status: superseded
domain: cluster
date: 2026-11-01
decided_by: Rémi (2026-11-01)
supersedes: []
superseded_by: [0036]
tags: [serveur, worker, cluster]
---

# 0032 — Serveur central fixe et workers qui ne deviennent pas serveurs

## Contexte
Le déploiement historique laisse chaque nœud devenir serveur, worker, ou les deux, puis transférer la maîtrise. Rémi veut un serveur unique et des workers, tout en gardant les pages Cluster et Nœuds pour voir comment ce fonctionnement s'y insère.

## Décision
`Cluster:FixedAuthority` sur le serveur refuse la passation de maîtrise et tout changement qui donne ou retire la fonction serveur. Le signal de présence n'attache plus d'ordre de passation. `Node:FixedWorker` sur un worker force le mode worker dans le lanceur, ignore une demande de fonction serveur et refuse la promotion d'urgence. Le fichier `backend/docker-compose.central.yml` lance ces deux rôles. L'ADR [0033](0033-images-central-et-worker.md) les publie dans deux images. Sans ces réglages, le comportement précédent reste en place. L'ajout d'un worker passe par l'enrôlement déjà présent sur la page Nœuds.

## Alternatives écartées
Retirer les pages Cluster et Nœuds : elles servent encore à voir les membres, le matériel et l'enrôlement. Réécrire un second produit à côté : la copie conforme du projet est la base retenue.

## Conséquences et risques
Un cluster déjà déployé sans `Cluster:FixedAuthority` ne change pas. Le maillage, les groupes et la reprise à plusieurs serveurs restent dans le code. Le worker du fichier Compose n'apparaît qu'après ouverture de l'enrôlement. Le verrou de mode est couvert par les tests du dépôt de mode et du changement de rôles SQLite. L'image Docker de ce fichier Compose n'a pas été construite dans cette étape.
