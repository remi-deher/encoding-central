---
id: 0009
title: Écrivain SQLite unique par nœud
status: implemented
domain: replication-storage
date: 2026-10-23
decided_by: Rémi, demande explicite de mise en place le 2026-10-23
supersedes: []
superseded_by: []
tags: [sqlite, écritures, concurrence, maillage, disponibilité]
---

# 0009 — Écrivain SQLite unique par nœud

## Contexte
Chaque serveur conserve sa base SQLite locale ; le maillage réconcilie les données par convergence éventuelle, sans coordinateur permanent ([replication-storage.md](../domains/replication-storage.md)). SQLite ne permet qu'un écrivain à la fois. Les transactions actuelles commencent en écriture immédiate et certaines tâches de fond ouvrent périodiquement une transaction même sans travail à effectuer. En production, des requêtes de présence et d'attribution ont attendu le délai de verrouillage puis échoué. La fiche runtime confirme que les baux et réservations restent locaux et transactionnels.

## Décision
Décision mise en place à la demande de Rémi le 2026-10-23 :

- Chaque processus serveur possède un écrivain SQLite local unique, alimenté par une file bornée. Toutes les écritures applicatives locales, y compris l'application des changements reçus des pairs, passent par cet écrivain.
- La file distingue les travaux urgents (présence et renouvellement de baux) des écritures ordinaires. Elle peut regrouper des travaux compatibles dans des transactions bornées ; l'échec d'un travail ne doit pas annuler les travaux indépendants du même lot.
- Une unité de travail conserve une transaction atomique pour ses propres invariants. Les baux, réservations et claims restent locaux au nœud et transactionnels.
- Les lectures n'acquièrent pas le verrou d'écriture : elles utilisent des transactions de lecture cohérentes avec le mode Snapshot. Un chemin nécessitant une transaction d'écriture ne peut pas être transformé en lecture seule sans préserver ses invariants.
- L'application de la réconciliation locale emprunte la file sans modifier le format, l'ordre logique, la déduplication ou les garanties de convergence des événements échangés entre nœuds.
- La file, les délais, les tentatives sur erreurs transitoires, les limites de lot et le comportement en cas de saturation sont explicites et observables. Une demande refusée ou arrivée à expiration produit une erreur exploitable ; une erreur de verrouillage transitoire ne devient pas une réponse HTTP 500 générique.
- Le déploiement se fait par étapes indépendantes : mesurer d'abord, introduire et tester la file sans migration d'appelants, migrer ensuite les chemins critiques puis les domaines restants. Les écritures directes ne sont interdites qu'après migration de leurs appelants, avec liste blanche d'exceptions d'infrastructure.

Cette décision ne change ni le moteur SQLite, ni le schéma, ni le protocole de réplication, ni le modèle sans coordinateur global. Chaque étape doit rester compatible avec les versions de nœuds qui échangent déjà le même protocole.

## Alternatives écartées
- **Remplacer SQLite par PostgreSQL** : cela ajoute un service de base de données à exploiter et un point de panne partagé, et contredit le stockage local validé sans preuve que ce changement répond au besoin.
- **Ajouter un coordinateur central permanent** : cela créerait une autorité et une dépendance communes incompatibles avec l'autonomie des nœuds.
- **Conserver les écritures concurrentes directes et seulement augmenter les délais de verrouillage** : cela peut allonger les requêtes bloquées sans garantir la priorité des heartbeats ni borner le temps de détention du verrou.
- **Réduire uniquement les transactions de quelques chemins** : cela laisse les autres écrivains concurrents libres de retarder les chemins critiques et n'établit pas une règle vérifiable à l'échelle du nœud.

## Conséquences et risques
- Les appels d'écriture doivent être migrés progressivement et les frontières transactionnelles réexaminées. Les transactions englobant des opérations longues doivent être découpées sans casser l'atomicité métier.
- Une file unique peut devenir un point de contention. Les métriques de temps d'attente et de détention, les avertissements sur les détentions longues, les lots bornés et la priorité des travaux urgents sont nécessaires pour détecter et limiter ce risque.
- Une unité rejouée après erreur transitoire doit être idempotente ou rendre les répétitions détectables. L'annulation d'une requête en attente ne doit pas annuler un travail déjà engagé dont le résultat est incertain.
- La saturation doit être explicite ; la capacité de la file et les délais doivent être réglés à partir de mesures. Les appels refusés nécessitent une réponse transitoire cohérente, avec `Retry-After` lorsqu'une route HTTP est concernée.
- Les sauvegardes et migrations, ainsi que les mécanismes de capture Raft optionnels, peuvent avoir des besoins d'accès spécifiques. Leur intégration à l'écrivain ou leur statut d'exception doit être documenté et testé avant d'interdire les transactions directes.
- Aucun changement de schéma ou de contrat de réplication n'est requis par cette décision. Les tests de convergence, de compatibilité de versions mixtes, de panne, de saturation et d'annulation restent des critères de validation des étapes concernées.
- Non vérifié : aucune mesure avant/après ni charge de production n'est revendiquée par cet ADR. Les preuves sous charge synthétique et en production devront être rapportées séparément ; toute validation en production nécessite l'accord explicite de Rémi.