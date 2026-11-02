---
id: 0021
title: Activation cluster des plugins et outils média
status: implemented
domain: plugins
date: 2026-10-27
decided_by: Rémi (2026-10-27)
supersedes: []
superseded_by: []
tags: [plugins, outils-media, cluster, reconciliation]
---

# 0021 — Activation cluster des plugins et outils média

## Contexte
Les plugins et les outils média doivent pouvoir être désactivés depuis une préférence unique du cluster. Le Store garde cependant ses paquets sur chaque serveur, et le cluster converge sans coordinateur ni quorum. Il faut séparer la disponibilité souhaitée de l’installation locale et préserver les traitements engagés.

## Décision
L’état activé/désactivé est enregistré dans les registres LWW existants du journal de réconciliation, avec activation par défaut. La désactivation d’un plugin interdit sa publication et son usage dans les nouveaux traitements. La désactivation d’un outil retire sa capacité des nouvelles attributions. Les exécutions déjà lancées, y compris leur reprise à checkpoint, continuent. `base` et `nodes` restent activés. La désactivation ne désinstalle aucun paquet ; l’installation du Store reste locale à chaque serveur.

## Alternatives écartées
- Préférence locale au serveur : elle autoriserait des politiques différentes selon le serveur qui reçoit une tâche.
- Retrait du paquet : il mélange activation et installation, et empêche une réactivation immédiate.
- Arrêt des tâches déjà lancées : il interromprait des opérations média en cours.

## Conséquences et risques
Les registres convergent éventuellement. Pendant une partition ou avant réception de l’événement par un serveur, celui-ci peut encore appliquer son état connu ; la modification n’est pas un verrou global sans quorum. Les anciens serveurs ne comprennent pas ce réglage avant leur mise à jour. Les paquets tiers restent installés séparément sur les serveurs concernés.
