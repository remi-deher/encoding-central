---
id: 0003
title: Publication comme étape de flow, distincte de la fin du flow
status: implemented
domain: flows
date: 2026-10-22
decided_by: Rémi (2026-10-22) pour le principe « la fin du flow ne marque pas le transfert » ; état final et tentatives : choix de l'implémentation ci-dessous
supersedes: []
superseded_by: []
tags: [publication, flow, fin-de-flow, plex, radarr, sonarr, jellyfin, media.published, reprise]
---

# 0003 — Publication comme étape de flow, distincte de la fin du flow

## Contexte
Aujourd'hui, la fin du flow déclenche la publication : une fois la sortie validée, le nœud d'accès publie, puis l'exécution passe à `succeeded` et `media.published` est écrit dans la même transaction (`RuntimeTransferRepository`). Rien ne peut donc s'exécuter **après** la publication : Plex, Radarr, Sonarr et Jellyfin ne sont pas actualisés (question ouverte n° 8), et un bloc placé dans le flow s'exécuterait avant que le fichier soit à sa place finale.

FileFlows sépare ces notions (flow « Convertir en HEVC » fourni par Rémi) : le plan (Builder Start, Video Encode), le traitement (Executor, avec sortie d'erreur vers un repli CPU), puis un **nœud explicite « Remplacer l'original »** après lequel le flow continue sur le fichier publié. Avant comme après le traitement, n'importe quelle opération peut s'enchaîner.

## Décision
- Trois notions distinctes : **terminaison du flow** (`base-complete-flow` : résultat du chemin), **traitement** (`base-prepare-processing` puis `base-execute-processing` : plan, sortie temporaire validée, sans toucher la source) et **publication** (étape explicite).
- Nouveau bloc `base-publish-result` (plugin `base`, version mineure suivante), placé après `base-execute-processing`. Il demande la publication au nœud d'accès, sans écrasement (comportement actuel), et rend la main au flow. Sorties : `published`, `deferred` (garde de publication, ex. lecture Plex, voir [runtime](../domains/runtime.md)), `error`. Après `published`, le **fichier courant du flow devient le fichier publié** (chemin et SHA-256) : les blocs suivants (Plex, Radarr, Sonarr, Jellyfin, scripts) travaillent dessus.
- `base-complete-flow` ne publie plus dans un flow qui contient `base-publish-result`. Un flow **sans** ce bloc garde la publication implicite en fin de flow (compatibilité, signalée comme héritée dans l'éditeur).
- Le **remplacement de la source** reste la politique de bibliothèque existante, inchangée et non étendue : le bloc ne remplace rien à lui seul.
- Garanties : publication toujours faite par le nœud d'accès sous bail et époque ; idempotente (même SHA-256 = même résultat) ; `media.published` écrit dans la même transaction que le checkpoint de l'étape. Après perte du worker pendant les blocs qui suivent, la reprise (ADR [0004](0004-reprise-au-checkpoint.md)) repart du dernier checkpoint, donc après le bloc de publication s'il est terminé, sans republier.
- Un échec en aval (actualisation Plex impossible, etc.) **ne défait jamais** la publication. Les blocs d'actualisation exposent une sortie « avertissement » ; l'état final de l'exécution dans ce cas est à décider (voir risques).
- `media.published` reste un événement d'audit durable, et non un déclencheur. Aucun consommateur en arrière-plan.

### Réalisation (plugin `base` 1.8.0, migration 0045)
- `base-publish-result` : sorties `published`, `unchanged` (le flow n'a rien modifié, rien n'est publié) et `error` (plan non exécuté, résultat déjà publié). Pas de sortie `deferred` : la garde de publication attend côté nœud d'accès (10 min par défaut) et, au doute, le résultat est publié à côté sans remplacer la source. Depuis le 2026-10-23, le bloc attend aussi lui-même la fin de la lecture Plex avant de demander la publication (même garde, Plex injoignable = on continue) et sort en « Erreur » si la lecture dure au-delà de l'attente maximale.
- Le worker poste `finish` avec `ContinueFlow` : le serveur enregistre le résultat comme aujourd'hui, mais l'exécution reste `running`, le bail et la réservation du média sont conservés (colonne `execution_work.continue_after_publication`). Le nœud d'accès publie, `ConfirmAsync` écrit `media.published` sans passer à `succeeded` ni libérer. Le worker lit `GET /executions/{id}/publication` jusqu'à `published`, renouvelle son bail, enregistre `runtime.published` (chemin, SHA-256) puis continue.
- Fin du flow : la référence est le fichier publié ; si le média a été modifié après la publication, l'exécution échoue (un second résultat ne peut pas être publié). Une seconde publication par bloc ou par la fin du flow est refusée par le serveur.
- État final après un échec en aval : l'exécution suit la sortie terminale choisie par le flow (réussite ou échec), la publication reste acquise. Pas de nouvel état ni de nouvelle tentative automatique : un flow qui veut réessayer utilise `base-wait` ou `base-defer-processing`.

## Alternatives écartées
- Service en arrière-plan abonné à `media.published` : plus complexe à exploiter et à rendre fiable en cluster ; écarté par Rémi (2026-10-22).
- Statu quo (publication implicite à la fin) : rien ne peut suivre la publication.
- Chaque bloc d'actualisation publie lui-même : couple publication et intégrations, risque de double publication.

## Conséquences et risques
- Compatibilité de cluster : le bloc exige une version du plugin `base` ; un worker plus ancien ne reçoit pas le flow (règle existante). Une étape de publication persistée dans le checkpoint peut demander une migration additive.
- Invariants 1 et 4 inchangés : la source reste préservée et aucun remplacement n'a lieu depuis un flow seul.
- Perte du worker après la publication : voir l'ADR [0004](0004-reprise-au-checkpoint.md). L'exécution est reprise au dernier checkpoint par un autre worker ; un bloc de publication interrompu est rejoué sans republier (la publication déjà demandée ou faite est adoptée).
- Lien avec [0002](0002-modification-mkv-sur-place.md) : `media.modified` suit la même logique.
- Tests livrés : scénario `PublishStepKeepsTheExecutionActive` (finish « continuer », exécution active après confirmation, `media.published`, réservation conservée, double publication refusée, fin normale). Compatibilité des versions de `base` jusqu'à 1.7.0. À écrire : exécution réelle du bloc par un runner (téléchargement, nœud d'accès réel), crash entre publication et bloc suivant, garde Plex.
- Non vérifié en réel : NAS/NFS (atomicité du renommage), Plex/Radarr/Sonarr réels, reprise multi-hôtes.
- Déploiement : mettre à jour tous les nœuds (serveurs avant workers) ; un worker sans `base` 1.8.0 ne reçoit pas un flow qui contient le bloc. Une migration (0045) est ajoutée.
