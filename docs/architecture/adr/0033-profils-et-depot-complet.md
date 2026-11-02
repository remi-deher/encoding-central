---
id: 0033
title: Profils de sous-titres et dépôt complet
status: implemented
domain: plugins
date: 2026-11-02
decided_by: Rémi (2026-11-02)
supersedes: []
superseded_by: []
tags: [sous-titre, ressource, depot, flow]
---

# 0033 — Profils de sous-titres et dépôt complet

## Contexte
Le moteur écrit quatre styles ASS en dur (référence 640×360, mise à l’échelle sur la hauteur affichée). Les blocs convertir et styliser ne savent choisir que ce jeu, « aucun », ou un instantané de police. L’ADR 0032 laisse les types autres que `font` fermés. L’index `repository.json` 1.x ne publie que `plugins[]` et `tools[]` : un dépôt ne peut pas embarquer le flow, les polices, un profil ou un fichier de sous-titres.

## Décision
1. Un profil de sous-titres est une ressource `subtitle-profile`. Son unique fichier est un JSON (`formatVersion` 1) : canevas de référence, infos de script (titre, WrapStyle, bordure à l’échelle, matrice) et la liste des styles ASS V4+ (police, taille, couleurs, graisse, italique, souligné, barré, échelles, espacement, angle, style de bordure, contour, ombre, alignement, marges, encodage). Les mesures sont celles du canevas de référence. Le moteur les met à l’échelle du cadre affiché (hauteur pour le corps, le contour, l’ombre et la marge verticale ; largeur pour les marges horizontales) et n’a plus de styles en propre.
2. Les blocs convertir, styliser et assembler citent ce profil par `identifiant:version`. Vide : ne pas restyler. Les valeurs `crunchyroll` et `custom` ne s’appliquent plus : l’exécution échoue avec un message explicite tant qu’aucune ressource n’est citée.
3. Une ressource `subtitle` est un fichier texte `.srt`, `.ass`, `.ssa` ou `.vtt`. Le bloc d’ajout de piste peut la citer à la place d’un fichier produit par le flow. Un seul des deux.
4. `repository.json` 1.x gagne `resources[]` et `flows[]`, optionnels, validés entrée par entrée comme les paquets. Une ressource y a un identifiant UUID stable, un type, des versions entières immuables, une URL et un SHA-256. Un flow y a le même identifiant que la définition JSON publiée. L’installation crée la ressource du catalogue ou importe le flow ; une version déjà présente avec la même empreinte est un succès, une empreinte différente est refusée.

## Alternatives écartées
- Garder le profil proportionnel comme préréglage du moteur : les styles resteraient écrits dans l’application.
- Publier le profil comme champ du bloc : un dépôt ne pourrait pas le réutiliser, et le document ne serait pas versionné à part.
- Nouveau schéma majeur de dépôt : les index 1.x déjà publiés resteraient illisibles, alors que les tableaux ajoutés sont optionnels.

## Conséquences et risques
- Un flow publié qui cite encore `crunchyroll` ou `custom` part en erreur de bloc jusqu’à ce qu’il épingle une ressource. Les flows de test peuvent être refaits.
- Les octets restent locaux au serveur ; la fiche de ressource est réconciliée. Deux serveurs qui installent la même entrée obtiennent le même identifiant.
- Le paquet `subtitles` 1.5.0 déclare le champ. Tant qu’il n’est pas installé, l’éditeur ne propose pas le sélecteur ; le moteur de cette version sait déjà lire le champ.
- Non vérifié : installation d’un dépôt public réel, ni cluster à deux serveurs déjà peuplé.
