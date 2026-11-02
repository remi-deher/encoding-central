---
id: 0032
title: Ressources de flow à plat, une version par fichier
status: implemented
domain: flows
date: 2026-11-02
decided_by: Rémi (2026-11-02)
supersedes: []
superseded_by: []
tags: [ressource, police, catalogue, version]
---

# 0032 — Ressources de flow à plat, une version par fichier

## Contexte
L'ADR 0006 range plusieurs fichiers dans une seule ressource (« Polices ASS » contient Arial, Times et Trebuchet). Une nouvelle version remplace le paquet entier. Un flow qui n'a besoin que d'une police embarque les autres, et la même police ne se réutilise pas d'un paquet à l'autre sans la dupliquer.

## Décision
1. Une ressource du catalogue est un fichier. Sa version immuable porte ce fichier (nom, taille, SHA-256). Une nouvelle version est un nouveau contenu du même fichier.
2. L'envoi de plusieurs fichiers crée une ressource par fichier. Le nom par défaut est le nom du fichier.
3. Un bloc qui consomme des polices référence une ou plusieurs ressources, chacune épinglée `identifiant:version`, séparées par des virgules. Il ne suit jamais « la dernière ».
4. Les paquets déjà publiés restent en base : les versions de flow sont immuables et continuent de les citer. La migration 0064 en extrait une ressource par fichier, avec un identifiant déterministe. Le catalogue n'affiche plus les paquets.

## Alternatives écartées
- Réécrire les versions de flow publiées pour remplacer le paquet par la liste : elles sont immuables, et le résultat média resterait le même.
- Garder le paquet comme seule forme et n'aplatir que l'affichage : une police ne pourrait pas être versionnée ni réutilisée seule.

## Conséquences et risques
- Le plafond de 100 Mio par paquet n'a plus d'objet. Le plafond de 20 Mio par fichier reste.
- Deux nœuds qui appliquent 0064 produisent les mêmes identifiants. Un nœud pas encore migré sert encore le paquet ; le worker qui cite le paquet télécharge toujours tous ses fichiers.
- Les types autres que `font` restent fermés tant qu'aucun bloc ne les consomme.
- Non vérifié sur un cluster à deux serveurs déjà peuplé : seulement le schéma, l'API et la migration sur une base de test.
