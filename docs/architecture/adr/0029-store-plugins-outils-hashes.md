---
id: 0029
title: Store commun pour plugins et outils, identifiés par hash
status: accepted
domain: plugins
date: 2026-10-30
decided_by: Rémi (2026-10-30)
supersedes: []
superseded_by: []
tags: [store, plugin, outil, hash]
---

# 0029 — Store commun pour plugins et outils, identifiés par hash

## Contexte
Le Store sait distribuer des plugins, mais les outils média sont encore résolus depuis l'image ou le `PATH`. Les plugins doivent pouvoir être livrés indépendamment des versions du moteur. Une même fonction d'outil ne doit pas recevoir plusieurs binaires implicites selon le plugin qui l'utilise.

## Décision
Le catalogue public utilise un dépôt statique unique `repository.json` pour publier plugins et paquets d'outils. Un plugin déclare les paquets d'outils requis, leur version minimale, leur éventuelle version maximale exclusive et leurs capacités nécessaires. Les outils sont partagés par identifiant de paquet et rôle ; une seule source fournit un rôle sur une plateforme à la fois. Le manifeste de paquet donne une empreinte SHA-256 par exécutable, l'index donne l'empreinte SHA-256 de l'archive, et serveur comme worker contrôlent ces empreintes avant usage. Un administrateur peut choisir un autre paquet fournisseur après retrait du fournisseur actuel. Les dépôts restent locaux à chaque serveur. La première version du Store n'embarque aucun paquet par défaut.

## Alternatives écartées
- Un outil copié dans chaque archive plugin : duplication et versions divergentes du même rôle.
- Un chemin d'exécutable choisi par le plugin : contourne le résolveur et le contrôle partagé.

## Conséquences et risques
Les manifestes et l'API du Store évoluent ; les paquets d'outils sont téléchargés du serveur vers les workers avec le jeton du nœud. L'index de dépôt ne remplace pas une signature : la confiance repose encore sur le dépôt configuré et ses empreintes. L'état du Store reste local et doit être installé sur chaque serveur. Cette décision pose le socle des paquets gérés ; l'extraction de tous les composants intégrés et des outils de l'image est une migration distincte. La validation en cluster réel et sous Landlock n'est pas effectuée ici.
