# Cluster sans quorum : revue et découpage

Statut : cadrage de la proposition de Rémi, reçue le 25 octobre 2026. La version révisée complète comprend D1 à D6, réception/activation séparées et attente des opérations destructrices en isolement. Ce document évalue la proposition ; les transferts et la reprise inter-serveurs ne sont pas implémentés. D6 est prioritaire avant toute extension.

## Avis

Retirer le mode Raft optionnel et conserver le maillage comme modèle unique est cohérent avec deux serveurs autonomes. LWW convient aux projections métier partagées ; il ne prouve pas la possession exclusive d'une tâche. Le plan d'attribution doit séparer identité de tâche, propriété d'exécution et autorisation de publier.

Le principe sûr à deux serveurs est la continuité du travail déjà possédé, avec transfert coopératif. Un timeout, même confirmé par un tiers, ne prouve pas qu'un propriétaire est arrêté. Une reprise sans contact exige de neutraliser son autorité précédente ou d'accepter explicitement des doublons et de protéger les écritures finales. À trois serveurs, une majorité portant sur la propriété ne suffit que si les anciens propriétaires et le lieu de publication appliquent cette décision : un serveur isolé ne doit pas garder un droit de remplacement illimité après une reprise par la majorité.

## Points à préciser avant D3–D5

1. **Transfert et accusé perdu : principe corrigé.** La cible reste `received`, inactive, et seule l'activation journalisée permet son claim. L'activation doit être atomique avec la révocation locale de la source. Geler les nouveaux claims et renouvellements ne suffit pas : attendre l'expiration ou révoquer les baux déjà accordés et interdire les publications/effets encore en vol avant l'activation. Une proposition annulée doit être durablement identifiable ; un accusé tardif ne la réactive pas. Une activation perdue conduit à une attente visible, récupérée par réconciliation, sans reprise silencieuse côté source.
2. **Accusé et activation.** La révision retire l'accusé d'un tiers comme autorisation d'exécution. Garder identifiant de transfert, propriétaire précédent, cible, génération attendue et état terminal dans chaque transition ; dédupliquer et rejeter les activations obsolètes ou issues d'une proposition abandonnée. Une interruption entre commit local et émission réseau se résout en rejouant l'événement engagé.
3. **Propriété initiale et doublons.** Une propriété unique par tâche n'empêche pas deux tâches différentes sur le même média. La clé média doit être portable et représenter le même fichier entre accès et alias. La réservation actuelle est locale ; sa réplication et la résolution des collisions constituent une évolution explicite. Une règle de gagnant par date UTC suppose des horloges fiables et ne tranche qu'après rencontre des événements.
4. **Publication pendant une partition : frontière corrigée.** La nouvelle D4 conserve la publication sans écrasement et diffère remplacement de source et `mkv-edit-in-place`. C'est la bonne séparation. La joignabilité reste une condition nécessaire, pas un verrou : deux tâches concurrentes peuvent voir le pair en ligne et vouloir écrire. Au retour du lien, achever la réconciliation de propriété/réservation et acquérir l'autorisation exclusive d'écriture avant de lever l'attente. Définir le traitement d'une partition qui commence entre le contrôle et l'écriture ; un heartbeat ancien ne vaut pas autorisation. Le nœud autonome sans pair ne doit pas attendre un cluster inexistant.
5. **Retour d'un serveur.** Définir la barrière locale de réconciliation avant réactivation, les checkpoints et artefacts nécessaires, et le comportement si les pairs restent injoignables. Distinguer continuer une propriété inchangée et reprendre une tâche dont l'autorité a pu être cédée. Aucune règle ne doit inventer la certitude à partir d'une présence observée.
6. **Gossip.** Conserver observateur, sujet, identité de démarrage, instant d'observation et fraîcheur ; une observation relayée n'est ni un heartbeat direct ni un mandat de reprise. Éviter qu'une ancienne observation se rafraîchisse à chaque relais. Garder la disponibilité locale indépendante du gossip.
7. **Plan borné.** Un événement contenant toute une découverte peut dépasser la limite des échanges (16 Mio). Définir un plafond par plan ou des fragments avec manifeste et activation complète. Réévaluer l'éligibilité au claim ; rendre visibles les affectations devenues impossibles.
8. **Époques.** L'époque locale du bail worker et la génération de propriété inter-serveurs ont des responsabilités distinctes. Les messages et checkpoints doivent porter l'identité de la tâche, le propriétaire, la génération et l'identifiant idempotent de transfert. Un compteur concurrent fusionné en LWW ne constitue pas un fencing global.

## Audit de D1 avant retrait

Le retrait dépasse le dossier `Infrastructure/PeerCluster/` :

- `Program.cs` compose le coordinateur, les sources de données, les commandes CLI Raft et les routes du mode pair.
- `Infrastructure/Data/DatabaseProvider.cs` désactive actuellement l'écrivain local quand `PeerCluster:Enabled` est vrai.
- `PeerDataSource`, `PeerDatabaseSchema`, `PeerOperationsCommand` et les accès SQL bruts servent le chemin Raft.
- Les dépôts runtime, reprise, attente, bibliothèques, nœuds, flows et plugins utilisent les gardes d'attribution et/ou les époques du mode pair.
- `Logging/SystemLogRepository.cs` dépend de `PeerLocalDatabase` pour ses journaux locaux. Retirer l'enveloppe en conservant la sémantique locale des logs.
- Les métriques, la rétention, les protections de secrets, l'identité, l'authentification et les endpoints ont des références à examiner. Conserver les identités, signatures, protections et routes du **maillage** : le mot « peer » ne signifie pas automatiquement Raft.
- Supprimer les tests exclusivement Raft, garder les tests de sécurité des pairs du maillage, de ressources entre pairs et de capture incrémentale ; adapter leurs références éventuelles aux utilitaires retirés.
- Le workflow `monitoring.yml` ne teste que les alertes Raft. Mettre à jour les scopes et leurs tests, les exclusions des workflows backend/image, et les références des documents d'exploitation.
- La dépendance `DotNext.AspNetCore.Cluster` peut être retirée après suppression de ses usages. Les migrations historiques déjà appliquées ne doivent pas être modifiées.

### Mise à jour d'une installation existante

Avant la suppression, définir le traitement explicite de `PeerCluster__Enabled=true`. Ne pas ignorer silencieusement cette option et démarrer plusieurs anciennes projections comme des propriétaires autonomes. Fournir un diagnostic de configuration et un chemin de sortie documenté du mode Raft. Vérifier les sauvegardes et l'état local sans convertir ni supprimer automatiquement des données de production.

## D5 révisée : neutralisation explicite et attente du destructif

La révision reconnaît qu'un pair injoignable peut continuer à travailler. Rémi retient le 25 octobre 2026 : à deux nœuds, reprise uniquement après neutralisation explicite de l'ancien propriétaire ; sinon il conserve sa file. La confirmation opérateur doit donc attester cette neutralisation, y compris son chemin de publication, et non simplement une absence de réponse. Aucune reprise automatique sur timeout n'est ajoutée.

La grâce de deux durées de bail ne remplace pas cette condition : les baux locaux peuvent être renouvelés par un propriétaire isolé. L'époque journalisée devient opposable à l'ancien serveur après réception ; toute opération destructrice doit contrôler l'autorisation au lieu d'écriture. Une majorité à trois nœuds doit aussi être appliquée à cette frontière, pas seulement constater une absence.

Les résultats non destructifs restent distincts par exécution, puis le perdant est mis en quarantaine après réconciliation. L'attente durable de D4 doit conserver le candidat validé, sa raison d'attente et ses conditions de réveil. Une nouvelle connexion ne suffit pas à réveiller une tâche dont la propriété n'a pas encore été réconciliée.

Le gossip proposé dans D4 (provenance, séquence par origine, TTL et statut d'indice) est cohérent. Il conserve la fraîcheur de l'observation d'origine lors du relais et ne participe pas seul à une décision de fencing.

## D6 : commencer par une mesure bornée et reproductible

Les méthodes d'overview existantes imposent déjà une limite serveur de 500 et la route SSE recharge un snapshot paginé à chaque changement ou après son attente. Il faut mesurer leur coût réel avant de remplacer ce chemin par des deltas ; un changement de contrat de flux ne doit pas être fait à l'aveugle.

La sonde de coordination est étendue à 7 000 travaux, deux workers sur **un seul serveur SQLite**, 128 claims, 512 renouvellements, 64 lectures de pages de 50 exécutions et 64 lectures ciblées d'état/événements, avec et sans capture concurrente. Les mesures séparent préparation, claims, renouvellements, lectures et admissions de l'écrivain. Elle reste explicitement opt-in et ne prétend pas reproduire deux serveurs ou deux hôtes. Le test ne modifie pas la taille ni les délais de la file d'écriture.

Le scénario de 7 000 tâches sur deux serveurs avec échanges HTTP, stockage et activité disque représentatifs reste à exercer séparément. Cette sonde fournit une première base locale ; elle ne valide pas une correction de performance en production.

## Livraison proposée

1. **D6 : contention et lectures.** Mesures avant/après, puis correctifs justifiés par ces mesures : cadence/jitter des sondages, lecture bornée et détails à la demande. Conserver les limites de la file d'écriture.
2. **D1–D2 : retrait du mode Raft.** ADR de retrait, source SQLite locale et écrivain commun uniques, nettoyage DI/CLI/gardes/métriques/tests/CI/documentation. Aucun changement du format du journal ou du protocole du maillage, aucune reprise inter-serveurs ajoutée. Tests : démarrage autonome, indépendance à la panne du pair, migrations, writer, sécurité et réconciliation existante.
3. **D4 : protection des opérations destructrices.** Attente durable en isolement, réconciliation avant réveil et contrôle exclusif au point d'écriture ; préserver le fonctionnement autonome. Tester partition pendant une autorisation, remplacement et modification sur place.
4. **D3 : propriété et transfert coopératif.** Contrat versionné, propriété et intentions durables, réception inactive, activation et révocation atomiques, checkpoint et artefacts vérifiés, claims locaux. Tester pertes de messages, accusés tardifs, doublons, redémarrages, baux anciens et réordonnancement. Déclarer chaque nouvelle table locale ou réconciliée et prévoir les versions mixtes.
5. **D4 : observations et déduplication.** Présence avec provenance et fraîcheur, diagnostics d'isolement et résolution des créations concurrentes. Tester partitions asymétriques et retour du réseau.
6. **D5 : reprise manuelle.** Neutralisation explicite de l'ancien propriétaire, décision de Rémi du 25 octobre 2026, puis grâce et époque journalisée. Distinguer perte du worker et perte du serveur propriétaire. Tester retour de l'ancien propriétaire après achèvement ailleurs avant toute automatisation ; témoin/majorité futurs restent un module distinct.

Le numéro du prochain ADR doit être réservé après consultation du registre et des PR ouvertes au moment d'implémenter. Cette revue ne modifie aucun contrat d'exécution ni invariant en production.

## Avancement de l’implémentation

- D6 : sonde 7 000 tâches livrée avec résultats isolés ; sondage des claims runtime et de découverte désynchronisé au démarrage et entre tours (jitter borné de 0 à 500 ms, délai minimal conservé). Aucun agrandissement de file ni délai d’admission. La pagination serveur était déjà bornée ; pas de changement aveugle du protocole SSE. Mesures HTTP et deux serveurs représentatifs encore non vérifiées.
- D1–D2 : retrait du coordinateur SQL, de ses gardes, de ses sources de données, du transport, de la dépendance, du prototype et de leurs tests/alertes ; source SQLite locale et écrivain uniques. ADR [0013](../architecture/adr/0013-retrait-mode-raft.md). L’ancien indicateur à `true` est refusé ; seuls ce diagnostic, son test et les documents historiques mentionnent encore la configuration retirée.
- D4, D3, complément D4 et D5 : étapes suivantes ; la reprise, le transfert de propriété et l’attente des opérations destructrices ne sont pas présentés comme implémentés par ce nettoyage.
