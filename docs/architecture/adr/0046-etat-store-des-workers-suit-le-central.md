---
id: 0046
title: L'état Store des workers suit celui du central
status: proposed
domain: plugins
date: 2026-11-07
decided_by: Rémi (2026-11-07, principe et blocage du worker non conforme) ; modalités à valider
supersedes: []
superseded_by: []
tags: [store, plugins, outils, workers, sse, synchronisation, conformité]
---

# 0046 — L'état Store des workers suit celui du central

## Contexte
Depuis l'issue #17 (migration 0076, `worker_store_state`), l'état Store d'un worker est une liste **désirée par worker**, écrite par un administrateur (`PUT /api/v1/workers/{id}/store-state`) et vide par défaut ([plugins.md](../domains/plugins.md)). Un worker fraîchement enrôlé n'a donc ni plugin ni outil du Store : sur le banc du 2026-11-07, les workers n'avaient aucun paquet et les placements échouaient (« outil manquant : ffmpeg, ffprobe »). Le transport, lui, convient : flux SSE `GET /api/v1/store/events` avec état complet à chaque connexion (`snapshot`, ou `desired-state` si le curseur est en retard), téléchargement depuis les routes du central, contrôle SHA-256, rapport `PUT /api/v1/store/applied-state` ([ADR 0041](0041-sse-a-la-place-du-sondage.md)). Rémi a décidé que les workers suivent le central et qu'un worker non conforme ne traite rien.

## Décision

### Ce que suit le worker
1. **Source unique.** L'état désiré de **tous** les workers est l'ensemble des paquets **installés sur le central** : plugins, et paquets d'outils de chaque plateforme. Aucune liste par worker, aucune saisie administrateur. L'activation reste une préférence du central appliquée à l'attribution ([ADR 0021](0021-activation-cluster-plugins-outils.md), [ADR 0035](0035-depot-officiel-et-activation.md)) : installer n'est pas activer, désactiver ne désinstalle pas.
2. **Révision globale.** Le central tient une révision monotone de son état Store (diffusion et curseurs SSE ; la conformité, elle, se juge par paquet, points 5 et 6), incrémentée à chaque installation ou retrait. Les événements SSE portent `id: v2.{révision}` ; un curseur `v1.*` est illisible et provoque un état complet (même version d'application des deux côtés, [ADR 0039](0039-version-exacte-worker-central.md)).
3. **Enrôlement et démarrage.** L'état complet reçu à chaque connexion SSE déclenche la comparaison : le worker télécharge ce qui manque **depuis le central**, sonde ses outils, puis rapporte. Ensuite chaque changement du central arrive par SSE.
4. **Plateforme.** Un worker n'installe que les paquets d'outils de sa plateforme ; un paquet sans version pour sa plateforme est rapporté `unsupported_platform`.

### Conformité
5. **Paquets requis d'un worker, R(W).** Le central calcule pour chaque worker W l'ensemble des paquets qui le **concernent** :
   - les plugins natifs `base`, `nodes`, `conditions` et les paquets d'outils fournissant `ffmpeg` et `ffprobe` pour sa plateforme ;
   - tout plugin référencé par une version de flow **publiée et attribuée** à une bibliothèque à laquelle W a un accès **direct** ou par **transfert**, et les paquets d'outils que ce plugin exige (`requiredToolPackages`) **pour la plateforme de W**.
   Un paquet d'outil d'une autre plateforme, un plugin qu'aucun flow attribué à ses bibliothèques n'utilise, ou un paquet retiré n'appartiennent pas à R(W). **Si un élément ne peut être établi** (plateforme non déclarée, flow ou accès illisible, dépendance non résolue), le paquet est réputé **requis** par prudence. R(W) est recalculé à chaque nouvelle révision du Store, à chaque attribution ou retrait d'une version de flow à une bibliothèque, et à chaque changement d'accès de W.
6. **Définition de « conforme ».** W est conforme quand : (a) chaque paquet de R(W) figure `applied` dans son dernier rapport (archive vérifiée par SHA-256, marqueur présent) ; (b) `ffmpeg` et `ffprobe` répondent à la sonde (`-version`, et pour `ffmpeg` la liste des encodeurs), qu'ils viennent d'un paquet du Store ou de l'image ; (c) chaque plugin de R(W) répond à sa poignée de main `describe` avec les capacités de son paquet vérifié. Les paquets hors R(W) se synchronisent quand même en arrière-plan ; leurs échecs sont signalés (avertissement) sans rendre W non conforme. Un paquet de R(W) sans version pour la plateforme de W (`unsupported_platform`) rend W non conforme.
7. **États, par worker** (le central est l'autorité : il les recalcule à chaque rapport de W et à chaque recalcul de R(W), et stocke le résultat avec son motif, voir point 16) :
   - `syncing` : un paquet de R(W) n'est pas encore `applied` et n'est pas en échec (nouveau worker, démarrage, nouveau paquet qui concerne W) ;
   - `compliant` : définition du point 6 satisfaite ;
   - `non_compliant` : un paquet de R(W) ou une sonde en échec ; W réessaie seul (délai croissant, plafond 15 min) ;
   - `failed` : `non_compliant` depuis plus d'**1 heure** ou après **5 essais** ; les essais continuent toutes les 15 min, l'interface signale l'échec et propose « Relancer la synchronisation ».
   Transitions : enrôlement ou démarrage → `syncing` ; rapport couvrant R(W) et sondes OK → `compliant` ; R(W) s'agrandit d'un paquet non appliqué → `syncing` ; échec → `non_compliant` → (seuil) `failed` ; tout rapport conforme ramène à `compliant`.
   **Une nouvelle révision ne suspend donc que les workers qu'elle concerne** : un outil `linux-arm64` ne suspend pas un worker `linux-x64` ; un plugin que ses bibliothèques n'utilisent pas ne le suspend pas ; un **retrait** ne suspend personne (le paquet sort de R(W), sa suppression locale suit le point 11) ; une **mise à jour** (nouvelle version d'un paquet) suspend seulement les workers pour lesquels cette nouvelle version entre dans R(W).
8. **Blocage.** Hors `compliant`, le central **n'attribue rien** au worker : ni tâche, ni découverte de bibliothèque, ni rôle de source pour un nouveau transfert. `workers.accepts_work` étant une colonne générée (migration 0033), elle n'est pas modifiée : la conformité est une condition **supplémentaire**, exprimée par **un seul prédicat partagé** ajouté là où `accepts_work` sert à **donner du nouveau travail** (`BACKEND_RULES.md`) : réclamation de tâches et de scans, choix d'un worker source, reprise (`RuntimeRepository.Claim.cs`, `RuntimeRepository.Steps.cs`, `LibraryWorkflowRepository.cs`, `RuntimeRecoveryService.cs`). Il n'est **pas** ajouté à `SourceReplacementRepository.cs` ni à `SourceWriteGuard.cs` (voir point 10). L'état de cycle de vie affiché gagne `StoreNotCompliant`, rangé entre `Incompatible` et `Draining`.
9. **Garde de dernier recours.** L'éligibilité par nœud (outil et version de plugin exigés par le flow) reste en place : elle protège une tâche si un outil disparaît entre deux rapports.

### Ce qui ne change pas ou est protégé
10. **Baux en cours.** Passer hors `compliant` ressemble à une vidange : les exécutions et baux en cours **continuent jusqu'à leur fin**, avec la version de paquet qu'ils utilisent ; rien n'est libéré ni réattribué ([ADR 0044](0044-echec-apres-perte-de-bail.md)). La **finalisation et le retour arrière d'un remplacement de source déjà effectué** continuent aussi, ainsi que la garde d'écriture de la source : les suspendre sur un état aussi fréquent mettrait la source en danger. Ces chemins gardent leurs conditions actuelles (vidange, quarantaine, incompatibilité), sans la conformité.
11. **Retrait.** Un paquet retiré du central quitte l'état désiré ; le worker ne le supprime que lorsque tout l'état a été appliqué sans échec **et** qu'aucune exécution locale ne l'utilise. Jamais de suppression sur une panne réseau.
12. **Outils lourds (jusqu'à 512 Mio).** Téléchargement en flux vers un fichier temporaire sur disque, SHA-256 calculé au fil de l'eau, reprise par `Range` (déjà accepté par les routes du central), délai d'inactivité plutôt que durée totale, un paquet à la fois, plugins avant outils. Le central borne les téléchargements d'outils simultanés (paramètre ; `503` + `Retry-After` au-delà). Un paquet déjà vérifié n'est jamais retéléchargé.
13. **Intégrité et isolation.** SHA-256 obligatoire, versions publiées immuables ; capacités lues dans le `plugin.json` vérifié et appliquées par Landlock/seccomp ([ADR 0042](0042-reseau-plugins-capacite-et-seccomp.md)). La règle de version de l'ADR 0039 ne change pas.
14. **Nouveau : pas de flux Store pour un worker incompatible.** `GET /api/v1/store/events` répond `409` (code `version_incompatible`) à un worker marqué `incompatible` ; aujourd'hui seul le jeton est contrôlé.
15. **Worker intégré.** Même mécanisme et même contrôle, par la boucle locale : un central sans `ffmpeg` utilisable a un worker intégré non conforme, et le dit.

### Stockage
16. Migration additive (numéro suivant, unique) : table `central_store_state` à une ligne (`revision`, `updated_at`) ; colonnes ajoutées à `worker_store_state` pour la conformité (`compliance_state`, `compliance_reason`, `compliance_since`, `failed_attempts`, `probe_report`, `required_items` : dernier R(W) calculé, pour l'affichage et l'audit). Les colonnes `desired_*` ne sont plus lues ; `desired_revision` reçoit `max(révision globale, applied_revision)` au rapport pour respecter le `CHECK` de 0076 ; il ne sert plus à juger la conformité, qui dépend de R(W) et du dernier rapport, pas de l'égalité des révisions. Le contrôle de `PUT /api/v1/store/applied-state` (révision périmée ou inconnue : `409` avec la révision attendue) se fait désormais contre la révision globale. Une restauration ramène ensemble ces tables ; seul le curseur du worker peut être en avance, cas déjà traité. La route `PUT /api/v1/workers/{id}/store-state` est retirée ; `GET` renvoie révision globale, état appliqué, conformité et motif ; `POST /api/v1/workers/{id}/store-sync` (administrateur) relance la synchronisation.

## Alternatives écartées
- **Garder l'état par worker, prérempli à l'enrôlement** : deux sources de vérité, dérive dès la première installation sur le central.
- **Ne synchroniser que les paquets activés** : la désactivation forcerait retrait puis nouveau téléchargement ; ADR 0021 sépare activation et installation.
- **Garde au seul placement (sonde `ffprobe` ou éligibilité par nœud)** : laisse travailler un worker à moitié équipé et rend son état illisible ; conservée seulement en dernier recours (point 9).
- **Le worker télécharge directement depuis le dépôt public** : contourne le central, unique autorité, et exige Internet sur chaque worker.
- **Modifier `accepts_work` ou supprimer `desired_*`** : interdit (migrations additives, jamais modifiées une fois appliquées).

## Conséquences et risques
- **Suspension par worker** (décision de Rémi, 2026-11-07) : une installation sur le central ne suspend que les workers qu'elle concerne, le temps de leur téléchargement. Le calcul de R(W) dépend des flows attribués et des accès : il doit rester une seule fonction partagée, testée, et pencher vers « requis » dans le doute. Publier un flow qui utilise un plugin pas encore appliqué sur W suspend W jusqu'à son rapport (en pratique bref, la synchro d'arrière-plan l'ayant souvent déjà installé).
- Un central sans paquet `ffmpeg`/`ffprobe` ni binaire d'image utilisable rend tous les workers non conformes : c'est voulu, le motif l'indique.
- Les états désirés par worker déjà écrits sont ignorés au déploiement ; aucune donnée perdue. Le disque des workers doit contenir tous les paquets du central pour leur plateforme.
- Les capacités annoncées en dur pour les handlers natifs (`plugin/base` 1.9.0, `plugin/nodes` 1.8.0, `plugin/conditions` 1.2.0 dans `ToolCapabilityProbe`) restent hors de ce périmètre (ticket #7).
- Tests à écrire : worker neuf `syncing` → `compliant` sans action ; outil d'une autre plateforme, plugin inutilisé et retrait sans suspension ; plateforme ou accès inconnus → requis ; publication d'un flow → `syncing` du seul worker concerné ; aucune attribution hors `compliant` (tâche, scan, source de transfert) ; bail en cours mené à terme pendant `syncing` ; seuil `failed` puis relance ; curseur `v1` ; reprise `Range` ; retrait différé ; worker incompatible sans flux. Non vérifié en conditions réelles : 512 Mio sur lien lent, NAS.
