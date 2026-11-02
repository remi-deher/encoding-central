---
id: 0033
title: Image centrale autonome et image worker sans base
status: implemented
domain: cluster
date: 2026-11-01
decided_by: Rémi (2026-11-01)
supersedes: []
superseded_by: []
tags: [serveur, worker, image, plugins]
---

# 0033 — Image centrale autonome et image worker sans base

## Contexte
ADR [0032](0032-serveur-central-fixe.md) fige les fonctions avec une seule image. Rémi choisit deux images. Le central doit pouvoir tourner seul. Le worker ne doit pas porter la base ni devenir central. Le central installe les plugins et les outils ; les workers les reçoivent.

## Décision
`backend/Dockerfile` publie la cible `central` et la cible `worker`. La cible `central` contient l'API, l'interface et un worker local, démarrés par le lanceur : un seul conteneur est autonome. La cible `worker` contient l'agent et une page statique, sans API, sans lanceur et sans base d'autorité. `Cluster:FixedAuthority` est posé dans l'image `central` et refuse tout changement de fonction. `Node:FixedWorker`, l'acceptation automatique et l'absence de lanceur empêchent l'image worker de prendre la fonction serveur. `backend/docker-compose.central.yml` ne contient que les ports et le volume de chaque conteneur. L'adresse du central est saisie sur le worker. La confirmation par code décrite ici est remplacée par le compte administrateur ([0034](0034-rattachement-par-compte-administrateur.md)). Les plugins et les outils du worker sont ceux publiés par le central : le worker les télécharge, vérifie l'empreinte, retire seulement ce que le central ne liste plus, et ne retire rien si le central ne répond pas. L'image worker sert le même shell avec six routes : vue d'ensemble, activité, bibliothèques, nœuds, plugins et outils, paramètres. Le navigateur parle au worker, qui relaie `/api` vers le central. Les autres routes restent sur le central. La connexion et l'initialisation restent joignables pour ouvrir une session, sans entrer dans le menu.

## Alternatives écartées
Garder une seule image pour les deux rôles : l'image worker contiendrait encore l'API et la base. Transférer la base vers un worker depuis l'application : c'est une bascule d'autorité, hors de ce choix.

## Conséquences et risques
`docker-compose.yml` construit toujours la cible finale `central`. Le volume du worker ne garde que son identité de rattachement ; les paquets se reconstruisent depuis le central. Les six pages lisent et écrivent par l'API du central. Les deux images ont été construites et démarrées en local le 2026-11-01. Le menu worker n'a pas été ouvert dans un navigateur. Le maillage et la passation restent dans le code tant que `Cluster:FixedAuthority` est absent.
