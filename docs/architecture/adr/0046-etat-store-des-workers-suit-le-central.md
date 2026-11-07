---
id: 0046
title: L'état Store des workers suit celui du central
status: proposed
domain: plugins
date: 2026-11-07
decided_by: Rémi (2026-11-07, principe) ; modalités à valider
supersedes: []
superseded_by: []
tags: [store, plugins, outils, workers, sse, synchronisation]
---

# 0046 — L'état Store des workers suit celui du central

## Contexte
Depuis l'issue #17 (migration 0076, `worker_store_state`), l'état Store d'un worker est une liste **désirée par worker**, écrite par un administrateur (`PUT /api/v1/workers/{id}/store-state`) et vide par défaut ([plugins.md](../domains/plugins.md)). Un worker fraîchement enrôlé n'a donc ni plugin ni outil du Store : sur le banc du 2026-11-07, les workers n'avaient aucun paquet et les placements échouaient (« outil manquant : ffmpeg, ffprobe »). Le transport, lui, convient : flux SSE `GET /api/v1/store/events` avec état complet à chaque connexion (`snapshot`, ou `desired-state` si le curseur est en retard), téléchargement depuis les routes du central, contrôle SHA-256, rapport `PUT /api/v1/store/applied-state` ([ADR 0041](0041-sse-a-la-place-du-sondage.md)). Rémi a décidé que les workers suivent le central.

## Décision
1. **Source unique.** L'état désiré de **tous** les workers est l'ensemble des paquets **installés sur le central** : plugins, et paquets d'outils de chaque plateforme. Aucune liste par worker, aucune saisie administrateur. L'activation reste une préférence du central appliquée à l'attribution ([ADR 0021](0021-activation-cluster-plugins-outils.md), [ADR 0035](0035-depot-officiel-et-activation.md)) : installer n'est pas activer, désactiver ne désinstalle pas.
2. **Révision globale.** Le central tient une révision monotone de son état Store, incrémentée à chaque installation ou retrait. Les événements SSE portent `id: v2.{révision}` ; un curseur `v1.*` est illisible et provoque un snapshot complet (même version d'application des deux côtés, [ADR 0039](0039-version-exacte-worker-central.md)).
3. **Enrôlement et démarrage.** L'état complet envoyé à chaque connexion SSE (événement `snapshot`, ou `desired-state` si le curseur du worker est en retard : même contenu) est la vérification « au démarrage » : le worker compare, télécharge ce qui manque, puis rapporte. Ensuite chaque changement du central arrive par SSE.
4. **Retrait.** Un paquet retiré du central quitte l'état désiré ; le worker ne le supprime que lorsque tout l'état a été appliqué sans échec **et** qu'aucune exécution locale ne l'utilise. Il ne supprime jamais rien sur une panne réseau.
5. **Plateforme.** Un worker n'installe que les outils de sa plateforme. Un outil absent pour sa plateforme est rapporté `unsupported_platform` ; le worker reste admis, sans la capacité correspondante.
6. **Outils lourds (jusqu'à 512 Mio).** Le worker télécharge en flux vers un fichier temporaire sur disque, calcule le SHA-256 au fil de l'eau et reprend un transfert interrompu par `Range` (déjà accepté par les routes du central). Le délai est un délai d'inactivité, pas une durée totale. Un worker installe un paquet à la fois, plugins avant outils. Le central borne le nombre de téléchargements d'outils simultanés (paramètre, `503` + `Retry-After` au-delà). Un paquet déjà vérifié (marqueur SHA-256) n'est jamais retéléchargé.
7. **Échec ou retard.** Un paquet en échec est rapporté (`failed` + code) et retenté avec un délai croissant plafonné (15 min), même sans nouvel événement SSE. Pendant ce temps, l'**éligibilité par nœud reste la seule garde** : le worker n'annonce que les paquets vérifiés installés, donc il ne reçoit que les tâches qu'il sait traiter, à la version exigée par le flow. Aucun nouvel état de cycle de vie ; la page Workers affiche « Store en retard » (révision appliquée < révision du central) et les échecs.
8. **Baux en cours.** La synchronisation ne touche ni aux baux ni aux attributions : pas de libération, pas de réattribution ([ADR 0044](0044-echec-apres-perte-de-bail.md)). Une exécution commencée garde la version de paquet qu'elle utilise jusqu'à sa fin.
9. **Inchangé.** SHA-256 obligatoire et versions publiées immuables (même version, contenu différent : refus) ; capacités lues dans le `plugin.json` vérifié et appliquées par Landlock/seccomp ([ADR 0042](0042-reseau-plugins-capacite-et-seccomp.md)) ; la règle de version de l'[ADR 0039](0039-version-exacte-worker-central.md) (enrôlement refusé, worker `incompatible` sans tâche) ne change pas.
10. **Nouveau : pas de flux Store pour un worker incompatible.** `GET /api/v1/store/events` répond `409` (code `version_incompatible`) à un worker marqué `incompatible` ; aujourd'hui seul le jeton est contrôlé. Il reprend le flux après sa mise à jour, ce qui évite d'installer des paquets qu'un moteur d'une autre version ne saurait pas exécuter.
11. **Worker intégré.** Il suit le même mécanisme, par la boucle locale, sans cas particulier.
12. **Stockage.** Migration additive (numéro suivant, unique) : table `central_store_state` à une ligne (`revision`, `updated_at`). Dans `worker_store_state`, seules les colonnes `applied_*` restent utilisées ; les colonnes `desired_*` sont abandonnées sans être supprimées ni modifiées, et la contrainte `applied_revision <= desired_revision` est respectée en écrivant `desired_revision = max(révision globale, applied_revision déjà stockée)` au moment du rapport. Une restauration de sauvegarde ramène ensemble `central_store_state` et `worker_store_state` ; seul le curseur gardé par le worker peut être en avance, cas déjà traité par un nouvel état complet. La route `PUT /api/v1/workers/{id}/store-state` est retirée ; `GET` reste et renvoie la révision globale, l'état appliqué et l'écart.

## Alternatives écartées
- **Garder l'état par worker, prérempli à l'enrôlement** : deux sources de vérité et dérive dès la première installation sur le central.
- **Ne synchroniser que les paquets activés** : la désactivation forcerait un retrait puis un nouveau téléchargement à la réactivation ; ADR 0021 sépare déjà activation et installation.
- **Bloquer tout le worker tant que la synchro n'est pas complète** : un worker privé d'un outil lourd ne traiterait plus rien, alors que l'éligibilité par nœud protège déjà chaque tâche.
- **Le worker télécharge directement depuis le dépôt public** : contournerait le central, unique autorité, et exigerait un accès Internet sur chaque worker.
- **Supprimer `worker_store_state.desired_*`** : interdit (migrations additives, jamais modifiées une fois appliquées).

## Conséquences et risques
- Un central qui installe un outil de 512 Mio le pousse à tous les workers de la plateforme : charge réseau visible, bornée par la limite de téléchargements simultanés. Le disque des workers doit pouvoir contenir tous les paquets du central pour leur plateforme.
- Les états désirés par worker déjà écrits sont ignorés dès le déploiement : un worker reçoit tout ce que possède le central. Aucune donnée n'est perdue.
- Les capacités annoncées en dur pour les handlers natifs (`plugin/base` 1.9.0, `plugin/nodes` 1.8.0, `plugin/conditions` 1.2.0 dans `ToolCapabilityProbe`) ne sont pas traitées ici ; elles coexistent avec les paquets synchronisés (dette signalée au ticket #7).
- Tests à écrire : premier snapshot après enrôlement, curseur `v1` → snapshot, échec puis reprise d'un outil (Range), retrait différé pendant une exécution, worker incompatible sans flux, plateforme absente. Non vérifié en conditions réelles : transfert de 512 Mio sur un lien lent, NAS.
