---
id: 0011
title: Contrôles média selon les effets des opérations
status: implemented
domain: runtime
date: 2026-10-23
decided_by: Rémi (2026-10-23)
supersedes: []
superseded_by: []
tags: [runtime, plugins, intégrité, performance]
---

# 0011 — Contrôles média selon les effets des opérations

## Contexte
En production, un épisode conforme de 618 Mo prend 176 secondes sans encodage ni publication. Ses deux scans ffprobe prennent 0,24 seconde ; la préparation et les SHA-256 après chaque bloc dominent. Rémi autorise des contrôles légers pour les inspections et le maintien des empreintes aux frontières de transfert et de publication.

## Décision
- Détecter les modifications courantes par taille et dernière modification UTC, jamais par date de création. Ce relevé n'est pas une identité de contenu.
- Un worker à accès direct annonçant `runtime/direct-read-v1` peut exécuter une liste fermée d'entrées, conditions et opérations natives sans copie ni empreinte. Le checkpoint porte un `directSource` (taille/date), avec `currentSha256` vide explicitement, jamais une fausse empreinte calculée sur ces métadonnées.
- Avant une opération qui exige un espace de travail, une attente ou un relais, demander la préparation de source avec le bail courant. La préparation d'un plan vide et les règles audio en mode plan peuvent d'abord inspecter les métadonnées ; dès qu'un plan trouve du travail, préparer la source avant son checkpoint. Un plan vide peut être sauté sans copie. Le nœud d'accès contrôle le relevé initial puis prépare un artefact durable vérifié par SHA-256. Les checkpoints suivants reviennent au contrat d'artefacts normal.
- Le moteur ne recalcule plus le SHA-256 après chaque bloc. Un candidat explicite ou un changement taille/date déclenche validation légère, empreinte et stockage du résultat. Avant publication, vérifier l'empreinte du fichier de travail exact puis appliquer le décodage prévu par l'ADR 0007. Les transferts et remplacements conservent leurs contrôles complets.
- Le descripteur plugin peut préciser `mediaEffect`: `readOnly` (aucun candidat), `producesOutput` (nouveau candidat, entrée préservée), `modifiesCurrent` (fichier courant toujours considéré comme candidat, même si sa taille/date est conservée). Le moteur décide des contrôles, le plugin n'en désactive aucun. Les deux premières déclarations sont vérifiées par taille/date ; Landlock reste la frontière de sécurité.
- Un plugin `readOnly`, `requiresMedia: false`, sans production de fichiers, peut recevoir les métadonnées sans fichier courant ni accès au montage. Les autres plugins et les scripts existants restent dans une copie de travail. Sans déclaration, compatibilité avec le résultat explicite et détection taille/date.

## Alternatives écartées
- Supprimer tous les contrôles : perd la détection des transferts incomplets et la protection du remplacement.
- SHA-256 après chaque condition : lit intégralement un fichier pourtant inchangé.
- Date de création seule : ne représente pas les modifications de contenu.

## Conséquences et risques
- Taille/date ne détectent pas un contenu changé en préservant ces deux valeurs. Les auteurs qui modifient le fichier courant doivent déclarer `modifiesCurrent` ou retourner explicitement ce fichier comme candidat ; les scripts utilisent `candidate`.
- Migration additive 0052 sur `execution_work`, état local non réconcilié. Les anciens workers gardent la préparation habituelle ; un checkpoint direct exige un worker annonçant la nouvelle capacité. Une reprise distante passe par le nœud d'accès et vérifie le relevé initial. Les déclarations d'effets font partie de l'empreinte de manifeste : un ancien worker ne peut pas se faire passer pour un hôte qui les comprend. Incrémenter la version d'un plugin qui adopte ces déclarations.
- Les scripts historiques peuvent encore imposer une préparation même lorsqu'ils ne modifient rien. Leurs flows ne deviennent pas tous instantanés par cette seule évolution.
- Tests : parcours réel ffprobe sans copie ni SHA source, préparation clôturée par époque, checkpoint de mutation refusé avant préparation, source modifiée, relais vers un ancien worker, contrats de résultat et empreintes des plugins. Non vérifié : performances NAS réelles et déploiement en production.
