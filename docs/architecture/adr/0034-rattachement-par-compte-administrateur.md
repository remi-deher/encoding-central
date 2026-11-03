---
id: 0034
title: Rattachement du worker par compte administrateur
status: implemented
domain: workers
date: 2026-11-01
decided_by: Rémi (2026-11-01)
supersedes: []
superseded_by: []
tags: [worker, rattachement, authentification]
---

# 0034 — Rattachement du worker par compte administrateur

## Contexte
ADR [0033](0033-images-central-et-worker.md) faisait confirmer le lien par un code à durée limitée, comparé sur la page Nœuds. Cette attente complique la mise en place : le worker et le central doivent être ouverts en même temps, et la demande expire.

## Décision
L'assistant du worker demande l'adresse du central et un compte administrateur de ce central. L'agent ouvre une session avec ce compte, appelle `POST /api/v1/workers/enroll`, puis ferme la session. Le mot de passe n'est ni journalisé ni enregistré. Seule une session de compte administrateur (`auth_type=session` et rôle admin) admet le nœud hors fenêtre d'enrôlement. Cette admission ne le déclare pas nœud local du central. Un lecteur, un jeton API ou l'identité synthétique d'une authentification désactivée ne le peuvent pas. La fenêtre d'enrôlement et la clé de jumelage restent disponibles.

## Alternatives écartées
Garder le code à comparer : deux écrans et une expiration de 10 minutes. Accepter n'importe quelle session ou un jeton API : ce n'est pas le compte qui administre le central.

## Conséquences et risques
Le mot de passe traverse le worker une fois, vers le central. Une demande de jumelage déjà publiée pour le même nœud est retirée quand l'admission réussit. Le parcours a été rejoué sur deux conteneurs réels (2026-11-02) : central initialisé, adresse du central choisie sur le worker, connexion par le relais, `POST /api/v1/local-worker/enroll` : le worker passe `online` sans approbation. Le central n'accepte que la session d'un administrateur (`auth_type=session`, en-tête CSRF) ; le worker ne transmet que la valeur du cookie de session, via un en-tête interne, et le relais n'enregistre aucun cookie. Non rejoué : navigateur piloté de bout en bout sur l'étape « Rattacher ce worker ».
