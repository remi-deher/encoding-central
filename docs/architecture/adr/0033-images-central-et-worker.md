---
id: 0033
title: Image centrale autonome et image worker sans base
status: implemented
domain: cluster
date: 2026-11-02
decided_by: Rémi (2026-11-02)
supersedes: []
superseded_by: []
tags: [serveur, worker, image, plugins]
---

# 0033 — Image centrale autonome et image worker sans base

## Contexte
ADR [0032](0032-serveur-central-fixe.md) fige les fonctions avec une seule image. Rémi choisit deux images. Le central doit pouvoir tourner seul. Le worker ne doit pas porter la base ni devenir central. Le central installe les plugins et les outils ; les workers les reçoivent.

## Décision
`backend/Dockerfile` publie les cibles `central` et `worker`. La cible `central` contient l'API, l'interface et un worker local, démarrés par le lanceur : un seul conteneur est autonome. La cible `worker` contient seulement l'agent et le shell Angular, sans API, lanceur, CLI IA ni base SQLite d'autorité. `Cluster:FixedAuthority` est posé dans l'image `central`. `Node:FixedWorker` force le rôle worker, supprime les routes et réceptions de promotion, et l'absence de lanceur empêche l'image worker de devenir central. `backend/docker-compose.central.yml` construit explicitement les deux cibles et chaque conteneur garde son volume.

Au premier accès, le worker demande l'adresse du central ; il la mémorise, puis présente la connexion avec un compte administrateur de ce central. Après authentification, le navigateur reste sur l'origine du worker et son API est relayée vers le central. Le shell et ses composants restent identiques, mais la navigation worker se limite à la vue d'ensemble, l'activité, les nœuds, plugins et outils, et les paramètres. Les plugins et les outils installés sur le worker viennent du central : les paquets sont vérifiés, ne sont retirés que si le central le demande et ne sont jamais retirés quand il est indisponible.

## Alternatives écartées
Garder une seule image pour les deux rôles : l'image worker contiendrait encore l'API et la base. Transférer la base vers un worker depuis l'application : c'est une bascule d'autorité, hors de ce choix.

## Conséquences et risques
`docker-compose.central.yml` est le seul Compose livré. Le volume du worker ne garde que son identité de rattachement et les paquets reçus. Les cinq pages lisent et écrivent par l'API du central, sans rendre les routes de flows, bibliothèques, IA ou mise à jour accessibles depuis le worker. La validation des deux images et du parcours dans un navigateur reste à exécuter.
