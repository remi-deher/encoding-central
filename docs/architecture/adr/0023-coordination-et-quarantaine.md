---
id: 0023
title: Coordination des tâches en quarantaine sans blocage global
status: implemented
domain: runtime
date: 2026-10-27
supersedes: []
superseded_by: []
---

# 0023 — Coordination des tâches en quarantaine

## Constat

Un checkpoint d'époque courante dont l'auteur et la preuve précédente sont exacts était différé sans fin lorsque la propriété était `quarantined`. L'inbox empêchait le rattrapage initial du serveur. Les appels fréquents à la même projection accumulaient les tentatives. `activate` et `recover` avaient la même ambiguïté entre dépendance absente et quarantaine.

La quarantaine n'est pas nécessairement irréversible : le départage des créations concurrentes est une projection recalculable lorsque l'historique converge (ADR 0016). Une quarantaine de conflit de propriété n'est pas automatiquement résolue par ce recalcul. Supprimer indistinctement leurs événements perdrait donc des preuves potentiellement utiles. Une activation signée déjà émise constitue notamment une décision durable de cession et ne doit pas être oubliée.

## Décision

- Vérifier l'enveloppe, le cluster, l'admission et la signature avant toute décision. Une admission absente reste une dépendance ; une admission révoquée est un refus. Pour un checkpoint, exiger propriétaire, époque et preuve précédente exacts, puis vérifier/déchiffrer le snapshot.
- Un checkpoint valide est une **preuve passive**, pas une autorisation d'exécution. Le conserver dans `task_checkpoints`, y compris en quarantaine, selon la règle existante de révision non décroissante. Acquitter son inbox et enregistrer `retained / quarantined_checkpoint_retained` dans le registre local de dispositions existant, étendu de manière additive. Aucun import, changement de propriété, publication ou accès média n'en résulte. Le journal signé conserve aussi les versions antérieures.
- Un événement ancien ou d'autorité incompatible avec l'époque courante est rejeté avec une raison stable ; son registre signé reste conservé. Un événement en avance sur l'époque ou une dépendance réellement absente reste dans l'inbox.
- Une transition `activate` ou `recover` dont la chaîne courante et l'autorité sont vérifiées mais empêchée par la quarantaine est **suspendue**, pas rejetée. Conserver intégralement sa preuve et sa provenance dans `task_coordination_parked`, hors de la file des dépendances générales. Pour `recover`, vérifier aussi l'attestation et la grâce avant suspension.
- Si la projection existante constate ultérieurement un état différent de `quarantined`, remettre la preuve originale dans l'inbox et refaire **tous** les contrôles, notamment époque, propriétaire, admission, neutralisation, réception durable et fichiers déjà requis par le protocole. Ce n'est ni une nouvelle cession ni une reprise automatique : seule la décision signée préexistante peut être appliquée. Une preuve périmée est alors rejetée. Le correctif n'introduit aucune levée de quarantaine et ne réattribue aucune tâche de sa propre initiative.
- Réessais persistants dans la projection commune à tous ses appelants : 1, 2, 4, 8, 16, 32 puis 60 secondes maximum, sans expiration ni plafond de tentatives. La cadence utilise `TimeProvider` et le stockage local, suivant le mécanisme de travaux différés à échéance déjà utilisé par les sauvegardes. Les lignes non dues n'occupent pas les lots de 32. Un redémarrage ne remet pas le délai à zéro. Une dépendance arrivée peut attendre au plus le délai restant ; elle n'est jamais abandonnée pour son ancienneté.

La barrière sur `task_coordination_inbox` reste entière, y compris pour les lignes non dues. Les preuves suspendues sont réveillées avant la projection et après son recalcul de quarantaine, dans la même transaction ; elles redeviennent alors des dépendances bloquantes. Les autres conditions de préparation restent inchangées. Les clôtures de preuve et déplacements entre files sont atomiques.

## Alternatives écartées et limites

Forcer `ready`, ignorer l'inbox ou expirer les événements après N tentatives fragiliserait l'autorité et perdrait des dépendances légitimes. Rejeter toutes les transitions en quarantaine perdrait une activation redevenue applicable. Importer un checkpoint pour vider l'inbox réactiverait un travail interdit.

Migration additive 0061 ; tables et échéances **locales**, jamais réconciliées ; journal, signatures, chiffrement et protocole réseau inchangés. Aucun changement des migrations appliquées. Les anciens serveurs gardent leur comportement jusqu'à leur propre mise à jour.

Diagnostics de coordination ajoutés aux deux statuts existants : compteurs exacts et liste bornée à 256 métadonnées, en priorité les attentes et suspensions. Aucun corps signé, contenu chiffré, chemin média ou secret dans ce diagnostic. Les dispositions sont historiques : `retained` signifie conservation d'une preuve, jamais exécution autorisée.

Validation sur SQLite locaux et serveurs HTTP de test ; pas de validation de production ni de charge multi-hôtes revendiquée. Procédure de récupération : [exploitation](../../operations/cluster-task-recovery.md).
