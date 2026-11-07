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
2. **Révision globale.** Le central tient une révision monotone de son état Store, incrémentée à chaque installation ou retrait. Les événements SSE portent `id: v2.{révision}` ; un curseur `v1.*` est illisible et provoque un état complet (même version d'application des deux côtés, [ADR 0039](0039-version-exacte-worker-central.md)).
3. **Enrôlement et démarrage.** L'état complet reçu à chaque connexion SSE déclenche la comparaison : le worker télécharge ce qui manque **depuis le central**, sonde ses outils, puis rapporte. Ensuite chaque changement du central arrive par SSE.
4. **Plateforme.** Un worker n'installe que les paquets d'outils de sa plateforme ; un paquet sans version pour sa plateforme est rapporté `unsupported_platform`.

### Conformité
5. **Définition.** Un worker est **conforme** quand, pour la révision globale courante : (a) il a rapporté cette révision ; (b) chaque plugin et chaque paquet d'outil de sa plateforme est `applied` (archive vérifiée par SHA-256, marqueur présent) ; (c) les **binaires requis** `ffmpeg` et `ffprobe` répondent à la sonde (`-version`, et pour `ffmpeg` la liste des encodeurs), qu'ils viennent d'un paquet du Store ou de l'image ; (d) chaque plugin installé répond à sa poignée de main `describe` avec les capacités de son paquet vérifié. Un paquet `unsupported_platform` ne rend pas non conforme, sauf s'il fournit `ffmpeg` ou `ffprobe` et qu'aucun autre fournisseur ne passe la sonde.
6. **États** (le central est l'autorité : il les recalcule à chaque rapport du worker et à chaque nouvelle révision, et stocke le résultat avec son motif, voir point 15) :
   - `syncing` : révision rapportée < révision globale (nouveau worker, démarrage, nouveau paquet sur le central) ;
   - `compliant` : définition du point 5 satisfaite ;
   - `non_compliant` : révision à jour mais un paquet en échec ou une sonde en échec ; le worker réessaie seul (délai croissant, plafond 15 min) ;
   - `failed` : `non_compliant` depuis plus d'**1 heure** ou après **5 essais** ; les essais continuent toutes les 15 min, l'interface signale l'échec et propose « Relancer la synchronisation ».
   Transitions : enrôlement ou démarrage → `syncing` ; rapport complet et sondes OK → `compliant` ; nouvelle révision → `syncing` ; échec → `non_compliant` → (seuil) `failed` ; tout rapport conforme ramène à `compliant`.
7. **Blocage.** Hors `compliant`, le central **n'attribue rien** au worker : ni tâche, ni découverte de bibliothèque, ni rôle de source pour un nouveau transfert. `workers.accepts_work` étant une colonne générée (migration 0033), elle n'est pas modifiée : la conformité est une condition **supplémentaire**, exprimée par **un seul prédicat partagé** ajouté là où `accepts_work` sert à **donner du nouveau travail** (`BACKEND_RULES.md`) : réclamation de tâches et de scans, choix d'un worker source, reprise (`RuntimeRepository.Claim.cs`, `RuntimeRepository.Steps.cs`, `LibraryWorkflowRepository.cs`, `RuntimeRecoveryService.cs`). Il n'est **pas** ajouté à `SourceReplacementRepository.cs` ni à `SourceWriteGuard.cs` (voir point 9). L'état de cycle de vie affiché gagne `StoreNotCompliant`, rangé entre `Incompatible` et `Draining`.
8. **Garde de dernier recours.** L'éligibilité par nœud (outil et version de plugin exigés par le flow) reste en place : elle protège une tâche si un outil disparaît entre deux rapports.

### Ce qui ne change pas ou est protégé
9. **Baux en cours.** Passer hors `compliant` ressemble à une vidange : les exécutions et baux en cours **continuent jusqu'à leur fin**, avec la version de paquet qu'ils utilisent ; rien n'est libéré ni réattribué ([ADR 0044](0044-echec-apres-perte-de-bail.md)). La **finalisation et le retour arrière d'un remplacement de source déjà effectué** continuent aussi, ainsi que la garde d'écriture de la source : les suspendre sur un état aussi fréquent mettrait la source en danger. Ces chemins gardent leurs conditions actuelles (vidange, quarantaine, incompatibilité), sans la conformité.
10. **Retrait.** Un paquet retiré du central quitte l'état désiré ; le worker ne le supprime que lorsque tout l'état a été appliqué sans échec **et** qu'aucune exécution locale ne l'utilise. Jamais de suppression sur une panne réseau.
11. **Outils lourds (jusqu'à 512 Mio).** Téléchargement en flux vers un fichier temporaire sur disque, SHA-256 calculé au fil de l'eau, reprise par `Range` (déjà accepté par les routes du central), délai d'inactivité plutôt que durée totale, un paquet à la fois, plugins avant outils. Le central borne les téléchargements d'outils simultanés (paramètre ; `503` + `Retry-After` au-delà). Un paquet déjà vérifié n'est jamais retéléchargé.
12. **Intégrité et isolation.** SHA-256 obligatoire, versions publiées immuables ; capacités lues dans le `plugin.json` vérifié et appliquées par Landlock/seccomp ([ADR 0042](0042-reseau-plugins-capacite-et-seccomp.md)). La règle de version de l'ADR 0039 ne change pas.
13. **Nouveau : pas de flux Store pour un worker incompatible.** `GET /api/v1/store/events` répond `409` (code `version_incompatible`) à un worker marqué `incompatible` ; aujourd'hui seul le jeton est contrôlé.
14. **Worker intégré.** Même mécanisme et même contrôle, par la boucle locale : un central sans `ffmpeg` utilisable a un worker intégré non conforme, et le dit.

### Stockage
15. Migration additive (numéro suivant, unique) : table `central_store_state` à une ligne (`revision`, `updated_at`) ; colonnes ajoutées à `worker_store_state` pour la conformité (`compliance_state`, `compliance_reason`, `compliance_since`, `failed_attempts`, `probe_report`). Les colonnes `desired_*` ne sont plus lues ; `desired_revision` reçoit `max(révision globale, applied_revision)` au rapport pour respecter le `CHECK` de 0076. Une restauration ramène ensemble ces tables ; seul le curseur du worker peut être en avance, cas déjà traité. La route `PUT /api/v1/workers/{id}/store-state` est retirée ; `GET` renvoie révision globale, état appliqué, conformité et motif ; `POST /api/v1/workers/{id}/store-sync` (administrateur) relance la synchronisation.

## Alternatives écartées
- **Garder l'état par worker, prérempli à l'enrôlement** : deux sources de vérité, dérive dès la première installation sur le central.
- **Ne synchroniser que les paquets activés** : la désactivation forcerait retrait puis nouveau téléchargement ; ADR 0021 sépare activation et installation.
- **Garde au seul placement (sonde `ffprobe` ou éligibilité par nœud)** : laisse travailler un worker à moitié équipé et rend son état illisible ; conservée seulement en dernier recours (point 8).
- **Le worker télécharge directement depuis le dépôt public** : contourne le central, unique autorité, et exige Internet sur chaque worker.
- **Modifier `accepts_work` ou supprimer `desired_*`** : interdit (migrations additives, jamais modifiées une fois appliquées).

## Conséquences et risques
- **Chaque installation sur le central suspend les nouvelles attributions de tous les workers** jusqu'à leur rapport conforme. Pour un plugin c'est quelques secondes ; pour un outil de 512 Mio, la durée du téléchargement le plus lent. Option à valider par Rémi : ne pas suspendre quand la nouvelle révision ne fait qu'**ajouter** un paquet qu'aucun flow publié n'utilise encore.
- Un central sans paquet `ffmpeg`/`ffprobe` ni binaire d'image utilisable rend tous les workers non conformes : c'est voulu, le motif l'indique.
- Les états désirés par worker déjà écrits sont ignorés au déploiement ; aucune donnée perdue. Le disque des workers doit contenir tous les paquets du central pour leur plateforme.
- Les capacités annoncées en dur pour les handlers natifs (`plugin/base` 1.9.0, `plugin/nodes` 1.8.0, `plugin/conditions` 1.2.0 dans `ToolCapabilityProbe`) restent hors de ce périmètre (ticket #7).
- Tests à écrire : worker neuf `syncing` → `compliant` sans action ; aucune attribution hors `compliant` (tâche, scan, source de transfert) ; bail en cours mené à terme pendant `syncing` ; seuil `failed` puis relance ; curseur `v1` ; reprise `Range` ; retrait différé ; worker incompatible sans flux. Non vérifié en conditions réelles : 512 Mio sur lien lent, NAS.
