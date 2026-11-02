---
id: 0015
title: Attribution initiale journalisée des tâches
status: implemented
domain: runtime
date: 2026-10-25
decided_by: Rémi (proposition D3–D5, 2026-10-25)
supersedes: []
superseded_by: []
tags: [propriété, attribution, signature, redémarrage]
---

# 0015 — Attribution initiale journalisée des tâches

## Contexte
Les baux locaux ne suffisent pas à identifier le serveur propriétaire dans le maillage. La création doit attribuer une autorité explicite ; un serveur redémarré doit rattraper le journal avant de réclamer du travail.

## Décision
La création de `execution_work` et son plan d'attribution initiale sont atomiques dans la même transaction locale, pour les lancements manuels, les découvertes, les reprises et les reports. Le serveur initiateur reste le propriétaire initial ; les règles existantes choisissent ses workers éligibles. Ce premier volet n'introduit pas de répartition de création vers un autre serveur.

Le plan versionné porte l'identité du cluster, un identifiant immuable, l'auteur, les identifiants de tâches/bibliothèques, les clés de médias, les dates de mise en file et les propriétaires. Il est signé RSA-PSS par l'identité persistante du serveur. L'origine du journal de maillage peut être différente : l'auteur est vérifié indépendamment du relais.

Les plans utilisent des clés uniques `tasks/v1/events/<id>` dans les registres existants ; le format du maillage ne change pas. Leur projection locale accepte seulement les signatures des pairs admis du cluster courant, ou la signature locale. Une admission reçue après son plan laisse celui-ci en attente. Un événement accepté ne peut pas être remplacé par une autre valeur de la même clé. Deux plans distincts revendiquant le même identifiant d'exécution mettent cette autorité en quarantaine ; ce cas est distinct de la future déduplication de deux exécutions du même média.

Un plan est borné à 128 attributions et 64 K caractères sérialisés. Un lancement plus grand est découpé en plans, dans la même transaction de création : aucune partie n'est active avant le commit. Une création unique ne peut pas franchir la limite d'événement du maillage. Les signatures et le journal constituent un coût nouveau à remesurer dans le scénario de charge.

Les projections de propriété, preuves acceptées, inbox et identité locale sont **locales** (migration additive 0055). Les preuves sont réconciliées par leurs registres signés, jamais par une table de propriété LWW modifiable librement.

Au démarrage, l'identité de la file est vérifiée et l'autorisation de réclamer est remise à zéro. Avec des serveurs admis, le premier réveil exige le rattrapage authentifié de tous et la projection des événements. Une file déjà démarrée continue pendant une coupure ultérieure. Les anciens travaux locaux sans plan sont attribués par lots de 128 après ce rattrapage. Les claims et les clôtures d'étapes contrôlent le propriétaire ; les écritures différées, restaurations et purges font de même. Une histoire répliquée ne crée jamais de `execution_work` local et ne devient donc pas implicitement exécutable.

## Portée et suite
Cette décision livre l'attribution initiale et la barrière de redémarrage, pas le transfert de propriété. Les états de réception/gel et les époques préparent les évolutions ; aucune route ne permet encore de déplacer ou reprendre une tâche distante. Les volets suivants sont maintenant livrés par [ADR 0016](0016-transferts-et-reprise-manuelle.md) : protocole source gelée → cible inactive → activation signée, transfert des artefacts vérifiés et chaînes inter-serveurs. D4 complément (gossip, dédup par média, UI) et D5 (neutralisation explicite, grâce et reprise manuelle) sont également décrits par cet ADR. Une simple injoignabilité ne permet toujours aucune reprise.

## Validation et limites
Tests SQLite isolés : atomicité avec rollback, signature falsifiée, réplication entre deux bases, admission tardive, répétition, redémarrage et poursuite après partition. Les tests des publications/remplacements synthétiques utilisent aussi les contrôles de propriété. Aucun test entre deux hôtes ni NAS réel n'est revendiqué. Les anciens serveurs ignorent ces registres ; aucun transfert ne doit être activé avec eux. Le déploiement de cette fondation n'ajoute donc aucune garantie de reprise à un cluster de versions mixtes.
