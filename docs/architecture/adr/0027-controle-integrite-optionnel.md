---
id: 0027
title: Contrôle d'intégrité facultatif par bloc de flow
status: implemented
domain: runtime, flows
date: 2026-10-30
decided_by: Rémi (2026-10-30)
supersedes: [0007, 0022]
superseded_by: []
tags: [intégrité, validation, flow, décodage, compatibilité]
---

# 0027 — Contrôle d'intégrité facultatif par bloc de flow

## Contexte

Le décodage complet avant publication peut prendre plusieurs minutes pour un fichier qui n'a subi qu'un remux ou des changements de métadonnées. Le bloc `probe-check-integrity` existe déjà avec des modes rapide et approfondi, mais les flows historiques sans ce bloc étaient encore décodés intégralement par défaut.

## Décision

- Tous les flows, y compris ceux déjà publiés, gardent le scan structurel de sortie, la cohérence de durée, les empreintes SHA-256 des artefacts et la publication atomique sans écrasement de la source.
- Seul le bloc explicite `probe-check-integrity` déclenche un contrôle d'intégrité média : `quick` vérifie la structure et échantillonne des passages ; `deep` décode intégralement les flux audio et vidéo. L'empreinte du fichier contrôlé est liée au checkpoint durable et doit correspondre exactement au résultat publié.
- Sans ce bloc, aucun décodage média complet n'est implicite. Le champ historique `probe-validate-output.verifyDecoding` reste accepté pour lire et valider les anciennes définitions, mais il est ignoré ; les flows et versions stockés ne sont pas réécrits.
- Le flow guidé et les modèles standards n'insèrent pas le contrôle par défaut ; un opérateur peut ajouter le bloc explicitement. Les flows sans bloc n'ont pas besoin de la capacité `media-integrity-policy/v1`.
- Cette décision remplace le décodage final obligatoire de l'ADR 0007 et la politique des flows historiques dans l'ADR 0022. Les décisions de l'ADR 0022 sur l'admission des nœuds et les permissions de bibliothèque restent en vigueur.

## Alternatives écartées

- Garder un décodage complet implicite pour les anciens flows : rejeté par Rémi le 2026-10-30.
- Supprimer le scan structurel et les vérifications de SHA-256 en même temps : non retenu ; ils protègent les contrats et les transferts, indépendamment du décodage.

## Conséquences et risques

- L'absence du bloc accepte qu'une corruption de paquets non échantillonnés ne soit pas détectée avant publication. Le flow peut demander un contrôle rapide ou approfondi selon son besoin.
- Le fichier source reste conservé ; l'option contrôle la validation du résultat, jamais les garanties de copie vérifiée et de publication.
- Les workers plus anciens peuvent encore décoder intégralement ces flows ; la compatibilité des versions mixtes reste limitée comme indiqué dans `open-questions.md`.
- Le changement de comportement n'a pas été exercé sur les médias des hôtes TrueNAS.
