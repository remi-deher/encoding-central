---
id: 0042
title: Réseau des plugins : capacité déclarée, Landlock ABI 4 et seccomp minimal
status: proposed
domain: plugins
date: 2026-11-03
decided_by: à décider (validation attendue de Rémi avant passage à accepted)
supersedes: []
superseded_by: []
tags: [landlock, seccomp, réseau, capacité, manifeste, proc]
---

# 0042 — Réseau des plugins : capacité déclarée, Landlock ABI 4 et seccomp minimal

## Contexte
Landlock ne restreignait que le système de fichiers ([plugins](../domains/plugins.md), [runtime](../domains/runtime.md)) : un plugin pouvait ouvrir n'importe quelle connexion et lire `/proc` en entier (`cmdline`, `status`, `maps` des autres processus ; `environ` est déjà refusé par la portée ptrace de Landlock, mais le reste ne l'est pas). Les plugins officiels Plex, Jellyfin, Sonarr et Radarr ont pourtant besoin du réseau.

## Décision
- **Capacité déclarée** : le manifeste porte `capabilities` (liste, absente par défaut). Seule valeur : `network`. Une valeur inconnue invalide le manifeste. Sans `network`, le processus du plugin n'a aucune socket hors `AF_UNIX` ; la poignée de main `describe` n'a jamais de réseau. Le worker prend la capacité dans le `plugin.json` du paquet vérifié, jamais dans la réponse du processus : un `describe` dont les capacités diffèrent du paquet, ou qui en contient une inconnue, est refusé ; les capacités entrent (triées, seulement si non vides) dans l'empreinte du manifeste comparée par le central, donc une mise à jour qui ajoute `network` change l'empreinte. Un plugin déclaré à la main dans la configuration du worker (sans `plugin.json`) est approuvé par l'opérateur : son `describe` fait foi.
- **Deux couches** quand `network` est absente : règle Landlock ABI ≥ 4 (refus du `bind`/`connect` TCP) et filtre seccomp (`socket`/`socketpair` limités à `AF_UNIX`, donc UDP et le reste inclus). Avant l'ABI 4, seccomp est la seule couche réseau : ce n'est pas un repli non confiné, c'est un confinement complet des sockets ; seul le filet de sécurité Landlock manque. Seccomp indisponible ou architecture hors x86-64/ARM64 : `ProcessSandbox.IsAvailable` est faux, aucune capacité n'est annoncée, rien n'est exécuté.
- Même avec `network`, seccomp n'accepte que `AF_UNIX`, `AF_INET`, `AF_INET6` et `AF_NETLINK` (résolution d'adresses de la libc) ; `AF_PACKET`, `AF_VSOCK`… restent refusés. Landlock ABI ≥ 6 ajoute `LANDLOCK_SCOPE_ABSTRACT_UNIX_SOCKET` pour les plugins (pas de connexion aux sockets abstraits de l'hôte : D-Bus, X11).
- Le relais vide le masque de signaux (`sigprocmask`) et rétablit `SIGPIPE` avant `execve`.
- Un plugin sans capacité dont l'échec contient EPERM/EACCES/« Operation not permitted » reçoit dans le message d'erreur l'indication qu'il lui manque `network`.
- Seccomp refuse toujours `ptrace`, `process_vm_readv/writev`, `io_uring_*` (création de sockets sans passer par `socket`), `bpf`, `perf_event_open`, `userfaultfd`.
- **`/proc` et `/sys`** : lecture limitée à `/proc/self` (le relais remplace son processus par l'outil via `execve` : le PID est celui du plugin), quelques fichiers globaux de `/proc` et `/sys/devices/system/cpu`, `/sys/fs/cgroup`. Les CLI IA officiels gardent l'accès large et le réseau (`ENCODING_SANDBOX_POLICY=network,host-proc`).
- Le niveau est transmis au point d'entrée `--isolated-process` par `ENCODING_SANDBOX_POLICY` ; absent, c'est le niveau le plus strict.

## Alternatives écartées
- Exiger l'ABI 4 pour tout plugin : rend les plugins inutilisables sur les noyaux < 6.7 (Debian 12) sans gain, seccomp couvrant déjà les sockets.
- Réseau accordé à tous les plugins : exfiltration triviale.
- Liste d'hôtes autorisés : Landlock ne filtre que des ports TCP, pas des hôtes.
- Espace de noms réseau/PID : exige des privilèges ou des espaces utilisateur non garantis en conteneur (voir [open-questions](../open-questions.md) point 3).

## Conséquences et risques
- Les plugins du Store qui appellent un service (plex, jellyfin, sonarr, radarr) doivent déclarer `capabilities: ["network"]` et être republiés ; **sans cela ils perdent le réseau** après la mise à jour du worker. Un worker ancien ignore le champ.
- Un plugin avec `network` peut atteindre n'importe quelle adresse TCP/UDP (réseau local compris) : la déclaration est visible dans le manifeste mais sa revue relève de l'opérateur qui installe le paquet.
- Les sous-processus d'un plugin (ex. `ffprobe`) n'ont pas accès à leur propre `/proc/<pid>` (la règle vise le PID du plugin) ; non couvert par un test.
- `AF_UNIX` reste permis : les sockets à chemin (`docker.sock`…) exigent un droit d'écriture que Landlock ne donne pas hors de l'espace de travail, mais un socket **abstrait** de l'hôte n'est bloqué qu'à partir de l'ABI 6, et un descripteur de socket hérité n'est pas filtré. **Ne jamais monter un socket sensible (docker.sock, D-Bus, agent SSH) dans le conteneur du worker ni dans l'espace de travail d'un plugin.**
- Le chemin « ABI < 4 » (seccomp seul) n'a pas de test dédié : forcer l'ABI exigerait une option de test dans le code de production. Le test réel vérifie le refus de l'UDP, que Landlock ne sait pas bloquer, ce qui exerce la couche seccomp indépendamment de Landlock. Le job CI affiche `uname -r` et l'ABI de Landlock pour savoir ce qui a été exercé.
- Non vérifié : conteneur de production, noyaux < 6.7 (seul seccomp), architectures autres que x86-64.
