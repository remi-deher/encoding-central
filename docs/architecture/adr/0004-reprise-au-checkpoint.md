---
id: 0004
title: Reprise au checkpoint après perte du worker, blocs rejouables
status: implemented
domain: runtime
date: 2026-10-22
decided_by: Rémi (2026-10-22) pour le principe (flows idempotents, meilleur effort, contraintes d'encodage et d'accès respectées) ; bornes et délais : choix de l'implémentation ci-dessous
supersedes: []
superseded_by: []
tags: [reprise, checkpoint, idempotence, perte-du-worker, relais, charge]
---

# 0004 — Reprise au checkpoint après perte du worker, blocs rejouables

## Contexte
Une exécution écrit un checkpoint durable après chaque bloc (variables, fichier courant, plan). Aujourd'hui, quand le bail d'un worker expire (60 s, renouvelé toutes les 15 s), `RuntimeRecoveryService` **fait échouer** l'exécution ; seule la règle de flow `nodes-retry-on-worker-loss` crée une nouvelle tentative, **depuis l'entrée du flow**. Un encodage d'une heure est donc perdu pour une coupure de quelques secondes, alors que le checkpoint et le relais (`handoff`) savent déjà faire reprendre un flow par un autre worker. L'ADR [0003](0003-publication-etape-de-flow.md) rend la chose plus visible : après une publication au milieu du flow, une perte du worker ne devrait ni rejouer le début ni republier.

Rémi a demandé de rendre les flows **idempotents** et d'adopter un **meilleur effort** : un worker qui prend le relais continue le flow sans le renvoyer au worker initial, en respectant les capacités d'encodage choisies, l'accès au fichier (direct ou relayé) et en répartissant la charge.

## Décision
- **Reprise au lieu d'échec.** Quand le bail d'une exécution `running` expire, le service de reprise la rend à la file des relais : même identité d'exécution, époque de bail suivante (le worker perdu est écarté), réservation du média conservée, verrous de disque libérés, dernier checkpoint conservé. Le détail d'historique indique « reprise n/2 ». C'est le mécanisme de relais existant avec un sélecteur `resume`.
- **Aucune nouvelle logique de choix.** Le worker qui reprend est celui qui réclame la tâche selon les règles habituelles de `ClaimAvailableAsync` : éligibilité du bloc d'entrée du checkpoint (`RuntimeEligibility.CanRun` : plugins, outils, capacités d'encodage, `nodes-require-capability`, `nodes-run-on-node`, groupes et étiquettes), accès au média (direct, ou transfert depuis le nœud d'accès d'origine, avec exigence d'accès direct), calendrier, plafonds de concurrence du nœud, de la bibliothèque et du couple bibliothèque/nœud. Le premier worker éligible qui a de la capacité la prend : la charge se répartit comme pour toute nouvelle tâche. Le worker perdu n'est pas exclu s'il revient en ligne. **La destination du dernier relais reste la règle de la reprise** (nœud, groupe, étiquettes de `nodes-handoff`, conservés dans le sélecteur avec un indicateur `resumed`) : un encodage confié à un nœud précis ne reprend pas ailleurs ; si ce nœud ne revient pas, la reprise échoue après le délai de grâce (décision de Rémi, 2026-10-22).
- **Borne** : au plus 2 reprises par exécution (`execution_work.resume_count`). Au-delà, l'exécution échoue avec un message explicite.
- **Blocs rejouables.** Chaque étape est classée à son démarrage (`execution_steps.idempotent`, via `RuntimeStepEffects`). Est rejouable un bloc qui, interrompu, ne laisse aucun effet hors du dossier de travail ou un effet identique à son rejeu : `base` (sauf `base-script`), `nodes`, `conditions`, `audio`, `video`, `subtitles`, `probe`, `images`, `mkv` (sauf `mkv-edit-in-place`), `ai`, et les actualisations/vérifications/analyses de `plex`, `jellyfin`, `radarr`, `sonarr`. Ne sont **pas** rejouables : `base-script`, `mkv-edit-in-place`, les blocs `*-search-*` (recherches qui lancent des téléchargements) et tout plugin inconnu. Une étape non rejouable inachevée à la perte du worker garde le comportement actuel : échec avec « effet incertain », relance explicite.
- **Publication rejouable.** `base-publish-result` interrogé après reprise adopte une publication déjà demandée ou faite pour le même SHA-256 au lieu de la refaire ; un résultat différent est refusé. Une reprise après `base-publish-result` terminé repart après lui.
- **Exceptions, comportement inchangé** : annulation en cours, sonde d'attente en cours (échec), flow avec sa propre règle `nodes-retry-on-worker-loss` (la règle explicite reste prioritaire et crée sa nouvelle tentative), source indisponible (échec existant : aucun nœud d'accès direct en ligne).
- **Aucun worker éligible.** Une reprise en attente est déclarée impossible après un délai de grâce de 2 minutes sans worker éligible (le temps qu'un worker redémarre) : l'exécution échoue, le média est libéré.

## Alternatives écartées
- Nouveau moteur de choix du worker de remplacement (moins chargé, etc.) : dupliquerait les règles d'attribution, et le tirage par réclamation les respecte déjà.
- Reprise illimitée : boucle infinie sur un fichier qui fait tomber les workers.
- Reprise des étapes à effet incertain : peut doubler une recherche Sonarr/Radarr ou rejouer un script inconnu.
- Reprendre aussi les sondes d'attente : l'attente sait déjà se reprogrammer.

## Conséquences et risques
- Invariants : l'exécution garde une seule époque active (le worker perdu est refusé par le bail, il ne peut plus écrire) ; la source reste préservée. L'invariant « une tentative échouée se relance en nouvelle exécution » ne vaut plus que pour les étapes non rejouables, la borne atteinte et les flows avec règle de relance.
- Le **verrou de disque** pris par le worker perdu est libéré : après reprise, la protection d'un `base-disk-lock` déjà passé n'est plus garantie (le verrou n'est pas repris automatiquement).
- Un encodage vidéo interrompu est **refait depuis le début du bloc** sur le nouveau worker (le checkpoint est par bloc, pas par image). Les encodeurs sont résolus par le worker qui exécute.
- Le nœud d'accès d'origine reste épinglé : si le worker perdu **était** aussi le seul nœud d'accès direct, la reprise est impossible et l'exécution échoue (règle existante). Une panne du nœud d'accès pendant que le traitement continue ailleurs n'est pas couverte : à étudier (attendre son retour au lieu d'échouer).
- Compatibilité : migration 0046 (colonne additive). Les nœuds d'un cluster doivent être mis à jour ensemble ; un serveur ancien continue d'échouer sans reprise.
- Tests livrés : perte puis reprise sur un autre worker au dernier checkpoint (variables conservées, époque, historique), deux reprises puis échec à la borne, attente puis échec sans worker éligible, rejeu d'une étape rejouable (même invocation), étape à effet incertain toujours en échec, règle de relance prioritaire, verrou de disque libéré. À écrire : reprise après publication avec un runner réel, répartition entre plusieurs workers éligibles sous charge.
- Non vérifié en réel : perte d'un worker sur les hôtes TrueNAS (.50/.51), reprise d'un transfert via nœud d'accès réel.
