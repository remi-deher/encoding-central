---
id: 0036
title: Séparation stricte du central et du worker
status: implemented
domain: workers
date: 2026-11-02
decided_by: Rémi (2026-11-02)
supersedes: [0032, 0033]
superseded_by: []
tags: [central, worker, image, autorité, passation]
---

# 0036 — Séparation stricte du central et du worker

## Contexte
Les ADR [0032](0032-serveur-central-fixe.md) et [0033](0033-images-central-et-worker.md) décidaient un central fixe et deux images, mais le code gardait un mode serveur/worker changeable à chaud, la passation de maîtrise, la promotion d'urgence et une sauvegarde miroir envoyée aux workers. `Central:FixedAuthority` n'était lu nulle part. Les routes `/api/v1/central/leadership/backup/latest` et `/keys` rendaient la base du cluster et les clés Data Protection à tout porteur d'un jeton de nœud, donc à tout worker enrôlé.

## Décision
- **Central** : une image qui embarque l'API, l'interface web, la base SQLite et un worker intégré. Il est autonome et reste l'unique autorité du cluster ; il pilote les workers. Le lanceur ne fait que démarrer et surveiller l'API et l'agent local, sans changer de mode.
- **Worker** : une image qui embarque seulement l'agent et son interface web. Aucune base de données, jamais autonome : il est relié à un central, dont l'API et le SSE alimentent son interface. Son enrôlement se fait à son setup, confirmé par un compte administrateur du central ([0034](0034-rattachement-par-compte-administrateur.md)).
- **Aucune passation** : une fonction serveur ne se donne pas à un worker et ne se retire pas. Retirés : passation de maîtrise, promotion d'urgence, sauvegarde miroir vers les workers, redirection `308`, changement de mode (local ou distant) et `desired_roles` livré par le heartbeat.
- **Plus de maillage** : un seul serveur, donc plus de réplication entre serveurs. Retirés : réconciliation (journal, échanges signés, projections, capture des changements), admissions de pairs et bootstrap de jonction, transferts, neutralisation et reprise de tâches entre serveurs, barrière d'écriture sur la source liée aux pairs, collisions de comptes à la jonction, récupération de ressources auprès d'un pair, échange IA entre serveurs. La propriété des tâches reste, en local (le central en est l'unique propriétaire). Les migrations 0066 (déclencheurs de capture) et 0067 (tables et colonnes de la réplication) nettoient la base.
- `POST /api/v1/worker/centrals` n'est anonyme que tant que le worker n'a pas de central ; ensuite il exige le secret local.

## Alternatives écartées
Garder la passation « pour plus tard » : elle expose la base et les clés à un worker et ne sert à rien avec un central unique. Garder le maillage inactif : code mort, déclencheurs de capture exécutés à chaque écriture pour rien. Un central sans worker intégré : choisi autrement par Rémi pour qu'un seul conteneur soit autonome.

## Conséquences et risques
- Un cluster qui a utilisé la passation n'a plus de chemin de promotion : la reprise après sinistre repose sur la restauration d'une sauvegarde du central ([backup-restore](../../operations/backup-restore.md)).
- Les groupes de nœuds restent (classement sans effet sur la gestion). Les résidus du maillage encore présents sont listés dans [open-questions](../open-questions.md).
- Non vérifié en conditions réelles : le parcours complet avec deux conteneurs et un navigateur.
