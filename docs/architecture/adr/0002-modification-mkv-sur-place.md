---
id: 0002
title: Modification MKV sur place par bloc dédié
status: implemented
domain: runtime
date: 2026-10-22
decided_by: Rémi (2026-10-22) — pas de politique de remplacement, bloc dédié (case à cocher sur les blocs existants possible plus tard)
supersedes: []
superseded_by: []
tags: [mkvpropedit, sur-place, intégrité, bail, publication, reprise]
---

# 0002 — Modification MKV sur place par bloc dédié

## Contexte
Les blocs qui appellent `mkvpropedit` (`mkv-edit-track-properties`, `mkv-edit-matroska-metadata`, `mkv-remove-attachments`, `mkv-replace-chapters`, `subtitles-name-tracks`) travaillent sur la **copie de travail** ; le résultat est publié sans écrasement dans `.encoding-results/` ([runtime](../domains/runtime.md)). Pour un changement de métadonnées (langue, nom, drapeaux de piste), il faut donc copier puis republier tout le fichier, alors que `mkvpropedit` ne réécrit que les en-têtes. Le flow FileFlows d'origine le faisait dans un script ([fileflows-v3-flow](../../operations/fileflows-v3-flow.md)). Le remplacement de la source n'est possible que par la politique de bibliothèque (invariant 4) et Rémi ne veut pas l'étendre.

## Décision
- Un **bloc dédié** `mkv-edit-in-place` (plugin `mkv` 1.1.0) modifie la source **sur place**. Les blocs existants ne changent pas. Une case « sur place » sur certains d'entre eux reste possible plus tard avec le même moteur (`InPlaceMkvEditor`).
- Opérations : langue, nom, drapeaux défaut/forcé d'une piste, titre du conteneur. Chapitres et pièces jointes (qui peuvent déplacer des données) sont exclus tant qu'ils ne sont pas validés sur de vrais fichiers.
- Le bloc ne s'exécute que sur le **worker qui a l'accès direct** à la bibliothèque (c'est lui qui écrit dans le fichier ; sinon sortie « Erreur »), doit précéder tout bloc qui modifie le média et ne s'emploie qu'une fois par flow. L'instruction « Exiger un accès direct » (mode strict) place le flow sur ce worker.
- Protocole : (1) verrou média local et empreinte SHA-256 de la source égale à celle du scan ; (2) propriétés déjà conformes → sortie « Déjà conforme », rien n'est écrit ; (3) **copie intégrale** de la source dans `.encoding-replaced/<exécution>/before-edit.mkv`, empreinte vérifiée ; (4) bail encore valide ; (5) intention `in-place.json` (`started`) ; (6) `mkvpropedit` ; (7) contrôle par `mkvmerge -J` : mêmes pistes et codecs, mêmes pièces jointes, mêmes chapitres, durée à 0,5 s près, propriétés demandées présentes ; (8) intention `done`. Tout échec, exception ou annulation aux étapes 6 et 7 restaure la copie (copie temporaire voisine, empreinte vérifiée, renommage) puis la sortie est « Erreur ».
- La copie est supprimée après un contrôle réussi, sauf si le champ « Conserver la copie de sauvegarde » est coché (jamais purgée automatiquement).
- Pas de politique de remplacement ni de période de grâce : l'opération est explicite dans le flow. L'invariant 4 vise le **remplacement** de la source ; la modification d'en-têtes par ce bloc est un second cas, régi par cet ADR.
- Garde Plex (2026-10-23) : le bloc consulte automatiquement la garde de publication du plugin Plex, sans bloc à placer ni réglage de plus. Il attend la fin de la lecture (contrôle toutes les 30 s, 10 min par défaut), refait un contrôle sans attente juste avant d'écrire (après la sauvegarde) et sort en « Erreur » sans toucher au fichier si la lecture continue. Plex injoignable ou sans avis : le bloc écrit (choix de Rémi). Reste possible : Plex lit le fichier si la lecture démarre pendant l'écriture de `mkvpropedit`, durée très courte.

## Alternatives écartées
- Politique de bibliothèque « modifier sur place » : écartée par Rémi.
- Case à cocher sur tous les blocs `mkvpropedit` dès maintenant : change le comportement de flows existants et multiplie les cas à valider.
- Sauvegarde de l'en-tête seul (comme le script FileFlows) : non fiable tant qu'on ne sait pas quels éléments `mkvpropedit` déplace ; la copie intégrale est retenue.
- Modification sans sauvegarde : une coupure en cours d'écriture peut rendre le fichier illisible.

## Conséquences et risques
- Pour ce seul bloc, l'invariant 1 (« source préservée ») est remplacé par une copie intégrale temporaire : il faut l'espace d'un fichier de plus pendant l'opération.
- Un flow qui modifie ensuite le média échoue à la fin (le résultat ne correspondrait plus à la source d'origine) ; le bloc refuse de s'exécuter après un autre bloc modifiant.
- **Reprise** : si le nœud meurt entre les étapes 5 et 8, le fichier peut être déjà modifié ; l'exécution relancée refuse alors (empreinte différente du scan) et la copie reste dans `.encoding-replaced/<exécution>/`. La reprise automatique à partir de l'intention n'est pas livrée.
- Non livré : événement `media.modified` (Plex/*ARR ne sont pas prévenus), interrupteur de cluster, vérification de décodage complète, `base-disk-lock` automatique.
- **Non vérifié** : atomicité et verrous de fichiers sur NFS/SMB (le verrou média est local au nœud), comportement lors d'une coupure d'alimentation, gros fichiers sur NAS, Plex lisant le fichier pendant l'écriture.
