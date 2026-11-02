---
id: 0014
title: Attente durable des écritures sur les sources en cluster
status: implemented
domain: runtime
date: 2026-10-25
decided_by: Rémi (2026-10-25)
supersedes: []
superseded_by: []
tags: [partition, source, attente, publication]
---

# 0014 — Attente durable des écritures sur les sources en cluster

## Contexte
La proposition révisée D4 autorise les calculs et publications sans écrasement pendant une partition. Le remplacement de source et la modification sur place doivent attendre. Une présence récente ou une époque inconnue du serveur isolé ne bloque pas une écriture au fichier.

## Décision
- Une installation autonome continue sans dépendance à un pair inexistant. Dans un cluster, chaque serveur admis doit avoir répondu à un échange signé et achevé le rattrapage des vecteurs. Les observations transitives ne donnent aucune autorisation. Un protocole de statut des tâches distinct, signé et lié à un nonce, vérifie aussi que le pair implémente la protection des sources ; une réponse ancienne rejouée ou un ancien serveur ne suffisent pas. Le format des échanges du maillage reste inchangé.
- Le dernier contrôle avant une écriture destructive déclenche les échanges existants, hors admission SQLite, puis revérifie le bail local. Cette dépendance réseau est limitée aux opérations destructrices ; les claims ordinaires et la publication sans écrasement ne l'utilisent pas.
- Une modification MKV sur place bloquée garde le checkpoint précédent et la réservation du média, libère le worker et invalide son époque. Aucun appel d'outil pouvant modifier la source n'a lieu avant le contrôle final. L'attente est locale et durable, réévaluée aux prochains claims ; elle n'est pas un échec et ne crée pas de tentative neuve.
- En cluster, le résultat validé est d'abord publié sous `.encoding-results/<execution>/`. Le remplacement demandé par la politique devient une tâche locale durable distincte ; une exécution qui attend sa publication finale reste en cours, sans worker ni bail actif, jusqu’à résolution. Cette tâche possède un identifiant de claim borné, un contrôle final et une réservation conservée. Une interruption avant autorisation remet l'opération en attente, pas en échec. Les rapports terminaux sont idempotents par claim. Les gardes de publication configurées (dont Plex) sont conservées et réévaluées ; les droits et le chemin de bibliothèque sont revérifiés avant mutation.
- Les nouveaux workers annoncent `runtime/source-write-guard-v1`. Un ancien worker ne reçoit pas de modification sur place ou de remplacement différé en cluster. Les restaurations/purges vérifient aussi la barrière.
- Une création exclusive de fichier adjacent à la source (`<nom-sans-extension>.encoding-write.lock`) protège la frontière d'écriture entre processus et chemins locaux vers un même stockage partagé. Aucun timeout ne retire ce verrou : après arrêt brutal, sa résolution exige de vérifier que l'ancien propriétaire est neutralisé. Les vérifications SHA-256 et les sauvegardes restent obligatoires.

## Alternatives écartées
- Bloquer tout le runtime dès la perte d'un pair : empêche le travail non destructif sans améliorer son intégrité.
- Considérer un heartbeat ou une observation relayée comme une autorisation : présence et autorité sont distinctes.
- Déclarer un remplacement échoué pendant l'isolement : perd l'intention au lieu d'attendre avec le résultat validé.
- Expirer automatiquement un verrou média au bout d'un délai : un ancien écrivain peut encore agir.

## Conséquences et risques
Migration additive 0054 : `runtime_source_write_waits` et `pending_source_writes` sont **locales**, comme les baux et files actives ; jamais capturées par le maillage. La rétention conserve les artefacts tant qu'une écriture attend. Le protocole d'échange du maillage ne change pas. Les extensions de transfert sont additives.

Une connexion n'est pas une propriété globale ; cette barrière ne livre pas D3/D5. La future propriété journalisée doit être contrôlée avant le réveil et au point d'écriture. La reprise à deux serveurs exige la neutralisation explicite de l'ancien propriétaire. Les opérations déjà engagées par une ancienne version doivent être drainées avant mise à jour. Les échanges réseau ne sont pas atomiques avec le système de fichiers : le dernier contrôle ne promet pas de détecter instantanément une coupure survenant après sa réponse. Le verrou au chemin du média et les empreintes protègent la frontière physique. Atomicité des créations exclusives et comportement en panne de NAS/NFS restent non vérifiés en conditions réelles : le test local ne les prouve pas.
