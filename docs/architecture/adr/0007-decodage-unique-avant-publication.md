---
id: 0007
title: Un seul décodage complet, juste avant la publication
status: superseded
domain: runtime
date: 2026-10-23
decided_by: Rémi (2026-10-23) : un décodage unique ; pas d'option pour rétablir l'ancien comportement
supersedes: []
superseded_by: [0027]
tags: [validation, decodage, performance, integrite, publication]
---

# 0007 — Un seul décodage complet, juste avant la publication

## Contexte
`RuntimeFlowRunner.ApplyOutcomeAsync` décodait entièrement le fichier (`ffmpeg -v error -xerror -i … -f null -`) après **chaque** étape qui produisait ou modifiait le fichier courant, y compris le rattrapage de fin d'étape (script `mkvpropedit`). Mesure sur l'exécution `33e03b0a` (30 s de HEVC Dolby Vision) : 4 à 5 décodages de 7 à 9 s, dont trois sur des flux inchangés. Sur un film de 2 h, des dizaines de minutes de CPU par fichier. `base-publish-result` ne décodait pas : la protection avant publication était le dernier de ces décodages. Constat de la mesure : ffmpeg 6.1 **sort en code 0** sur une image corrompue ou un conteneur abîmé, même avec `-xerror` ; l'ancien contrôle ne détectait donc que les erreurs fatales. FileFlows, Unmanic et Tdarr ne décodent pas chaque étape ; Tdarr traite le décodage complet comme un contrôle de santé distinct.

## Décision
- Après chaque étape, `ApplyOutcomeAsync` fait le contrôle **léger** (`MediaScanner.ValidateAsync` sans décodage) : fichier non vide, média attendu, durée cohérente avec la source.
- Le **décodage complet** (`MediaScanner.DecodeAsync`) a lieu **une fois**, sur le fichier exact à publier : dans `base-publish-result` avant de demander la publication, et à la fin du flow avant `finish` si le résultat diffère de la source. Il est mémorisé par SHA-256 pendant l'exécution du runner ; une reprise (ADR 0004) le refait. Aucun état de checkpoint ajouté.
- Le décodage échoue si ffmpeg sort en code non nul **ou écrit quoi que ce soit sur stderr** (`-v error`). Le message d'échec porte les 400 premiers caractères de stderr.
- Échec dans `base-publish-result` : sortie `error` du bloc (comme les autres erreurs de publication), rien n'est publié. Échec en fin de flow : exécution en échec, source intacte.
- Aucune option pour rétablir le décodage à chaque étape (refusé par Rémi).

## Alternatives écartées
- Empreinte des paquets A/V pour prouver l'identité des flux après chaque étape : détecte l'étape fautive mais relit tout le fichier à chaque étape ; Rémi a retenu le décodage unique.
- Décodage après réencodage seulement : non retenu, le décodage final couvre tout.

## Conséquences et risques
- Une corruption introduite par une étape intermédiaire n'est vue qu'à la fin ; le diagnostic ne désigne pas l'étape. Les étapes qui échouent (code de sortie, `ffprobe`, durée) restent détectées tout de suite.
- Contrôle plus **strict** qu'avant sur stderr : un fichier source déjà abîmé (avertissements d'erreur à la lecture) échouera désormais avant publication au lieu de passer. À surveiller sur la bibliothèque réelle.
- Le transfert vers le nœud d'accès garde son contrôle sans décodage (déjà le cas) ; le SHA-256 vérifié prouve qu'il s'agit du fichier décodé.
- Compatibilité cluster : aucun contrat, aucune migration. Un ancien worker décode à chaque étape (plus lent, sûr).
- Tests : `MediaDecodeValidationTests` (la validation d'étape ne décode pas ; le décodage détecte un flux corrompu que `ffprobe` accepte) ; les tests de relais média existants traversent la publication. Non couvert par un test dédié : décodage absent des étapes intermédiaires d'un flow complet et corruption injectée par un script au milieu du flow.
- Non vérifié en réel : gains mesurés sur .50/.51.
