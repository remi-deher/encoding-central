# Reprise IA — cluster sans quorum

Plan utilisateur complet : [cluster-mission-plan.md](cluster-mission-plan.md).

## Mission et décisions imposées

Terminer D3/D4/D5 du plan utilisateur : propriété journalisée, transfert interserveurs avec accusé, checkpoints et fichiers vérifiés, continuité en partition, reprise manuelle sûre et réconciliation avant réadmission. D1/D2 (retrait Raft, maillage LWW unique) sont déjà sur dev via les PR précédentes. Première PR [#399](https://github.com/remi-deher/encoding/pull/399), non brouillon vers `dev`, déjà fusionnée. Le complément [#400](https://github.com/remi-deher/encoding/pull/400), également fusionné, ajoute la mesure CI et les preuves. La clôture documentaire est dans `docs/cluster-validation-closeout`, basée sur `d7f8a71`. L’utilisateur demande explicitement de poursuivre après ce premier point : **la première PR ne constituait pas la fin de la mission**.

Décision utilisateur obligatoire : à deux serveurs, reprise uniquement après neutralisation explicite de l’ancienne installation et de tous ses workers. Ni timeout, ni gossip, ni majorité imaginaire n’autorisent une reprise. À trois serveurs, aucun basculement tant que le futur module dédié n’est pas implémenté. Ne pas réintroduire Raft ni changer le protocole du maillage.

## Avancement actualisé après validation locale

| Étape | État | Points d’entrée / preuves |
|---|---|---|
| Propriété, epochs, journal signé | Implémenté | TaskOwnershipRepository, TaskCoordinationProjection |
| Checkpoints chiffrés et capture différée | Implémenté | TaskCheckpointRepository, migration 0056 |
| Transfert reçu puis activé, perte d’accusé / activation | Implémenté, suite runtime finale réussie | ClusterTaskCoordinator / Transport / Endpoints |
| Reprise à deux, neutralisation, réadmission signée | Implémenté, scénarios ciblés passés | ClusterTaskTransferTests |
| Isolation, gossip borné, doublons/quarantaine | Implémenté | ClusterTaskMaintenance, protections publication |
| nodes-handoff serveur et runtime 1.8 | Implémenté | nodes.json, RuntimeEligibility, NodeAgent |
| Annuler / reprendre / réessayer via propriétaire | Implémenté | ExecutionRepository et RPC signé |
| Statuts interface et propriété | Implémenté | activité, accueil, catalogue des nœuds |
| Suite runtime complète | Dernière exécution sur le code livré : 515 réussis, 6 ignorés en 2 min 41 s | /tmp/cluster-final-runtime.log ; inclut le dernier patch transport |
| Mesure 7 000 tâches, deux SQLite | Réussie sur le code livré (`0e07a56`) | cluster-two-server-measurements.md et données JSON versionnées |
| Dernier patch de partage des téléchargements | Compilation stricte et suite runtime finale réussies | ClusterTaskTransport, RuntimeArtifactStore, test ajouté |
| Frontend | 102 tests Node + 224 tests Angular, lint et format des fichiers modifiés réussis | Build production réussi ; avertissement de budget préexistant sur ai-assistant-page.component.scss (+232 octets) |
| Launcher, plugins, prototype | Réussis en local (Landlock indisponible : scénarios concernés ignorés) et jobs CI réussis | Run backend #399 |
| CI, documentation finale | CI #399 entièrement réussie, mesure dédiée #400 réussie ; guides et rapport complétés | Liens de preuve ci-dessous |
| Machines physiques / NAS / partition réelle | **Non vérifié** | Ne pas prétendre ces garanties testées en production |

## Consignes strictes pour la reprise

1. Lire AGENTS.md, BACKEND_RULES.md, TEST_RULES.md et UI_RULES.md, puis ADR 0016 et fiches runtime/cluster/replication-storage. Respecter le writer SQLite unique ; aucun HTTP, copie média ou hash long dans sa transaction.
2. Les PR #399 et #400 sont fusionnées. Pour un correctif nouveau, partir du dernier `dev` dans une branche dédiée. Ne pas écraser les changements et ne pas fusionner automatiquement. Vérifier git status et le dernier commit avant toute action.
3. Ne pas déclarer fini sur la base des tests intermédiaires. Recompiler avec warnings en erreurs, exécuter les tests après les dernières modifications et mettre à jour ce tableau ainsi que la description de PR avec résultats exacts.
4. Préserver les contrôles de rôle administrateur explicites sur chaque écriture. L’endpoint interserveurs anonyme au sens session est authentifié par signature, nonce, appartenance et admission ; conserver les tests du catalogue d’accès.
5. Préserver les époques, le gel source, les tombstones d’abandon et la vérification de révision au commit. Un transfert dont l’activation a été journalisée ne peut pas être annulé. Un accusé tardif ne ressuscite pas un transfert abandonné.
6. Ne jamais modifier un média réel pour tester. Préserver source, validation, publication sans écrasement, scan des sorties et barrières des écritures destructives. Ne pas retirer un verrou sous prétexte qu’il est ancien.
7. Migration 0056 additive, tables locales explicitement classées. Elle est désormais intégrée via #399 : ne pas la réécrire ; ajouter une nouvelle migration pour toute évolution.
8. Aucun quorum sur le chemin critique. La présence relayée ne permet ni reprise ni réadmission ; les baux restent locaux au propriétaire.
9. Ne pas ajouter de fonctions facultatives avant de terminer les validations. Aucun nouveau sous-agent sans instruction explicite.
10. Publier régulièrement un avancement honnête en français. Garder une liste de limitations concrètes ; ne pas convertir les scénarios synthétiques en preuve multi-hôtes.

## Travail restant

Les validations locales et la mesure finale sont terminées. Les workflows backend/frontend/image de #399 sont réussis, ainsi que la nouvelle mesure CI de #400. Docker, compatibilité avec l’image précédente, perte de worker multiprocessus et E2E frontend ont réussi en CI. Les scénarios Landlock ignorés localement restent distingués de cette validation des jobs CI. Ne pas fusionner automatiquement les PR documentaires de clôture.

Les limites de déploiement restent explicites : deux hôtes physiques, NAS/NFS/SMB, coupures réseau réelles et activité disque non validés ici. La reprise à trois serveurs et l’automatisation ne font pas partie de cette livraison. Tout changement fonctionnel ultérieur doit reprendre les invariants ci-dessus et ses tests pertinents.

## Environnement de cette session

Dépôt `/workspace/encoding`. SDK `/tmp/encoding-dotnet/dotnet` (.NET 10). Builds `-m:1` pour limiter la mémoire. Pour les tests média : ajouter `/tmp/encoding-mkv/root/usr/bin` à PATH et `/tmp/encoding-mkv/root/usr/lib/x86_64-linux-gnu` à LD_LIBRARY_PATH ; `ENCODING_TEST_REQUIRE_MEDIA_TOOLS=1`. Ne pas lancer les builds Angular et .NET lourds simultanément. Les fichiers /tmp ne sont pas des artefacts durables : conserver les preuves utiles dans le dépôt ou les artefacts CI. GitHub `remi-deher/encoding`, cible `dev`. Après création, attacher la PR à la conversation.

## Mise à jour après le premier point

La PR #399 a été fusionnée pendant la validation, sans action de fusion de l’agent. La branche de suivi est basée sur ce merge. Le job CI `cluster-two-server-measurements` exécute la mesure opt-in avec limite 15 minutes et conserve journal/TRX ; `ci-scopes.mjs` le rend visible dans les vérifications de release. Les 15 tests des scripts CI/release réussissent. Les 102 tests Node et 224 tests Angular réussissent sur le code livré ; le build strict .NET a zéro avertissement et zéro erreur. La suite runtime finale réussit : 515 tests, 6 ignorés (trois mesures opt-in, un scénario multiprocessus et deux Docker). Le build frontend production et le maillage à trois processus réussissent. Les scénarios launcher/plugins réussissent (Landlock non disponible localement).

Les commandes locales finales ont été exécutées avec la configuration Release : `dotnet build backend/Encoding.sln --no-restore -warnaserror -m:1`, `dotnet test backend/tests/Encoding.Runtime.Tests --no-build --filter "Category!=Stress"`, les exécutables de tests launcher/plugins et `ClusterMeshPrototype -- test` (SDK présent dans PATH). Frontend : `npm run lint`, `npm test`, `npm run build`, Prettier sur les fichiers modifiés. Scripts : `node --test .github/scripts/*.test.mjs`. Ne pas relancer ces suites sans modification ou échec justifiant une nouvelle exécution.

## Preuves de clôture GitHub

| Vérification | Preuve | Résultat |
|---|---|---|
| Backend : compilation, runtime, plugins, launcher, prototype, CLI natifs, worker tué, Docker et image précédente | [Run 40168955760](https://github.com/remi-deher/encoding/actions/runs/40168955760) | Réussi |
| Frontend : lint, format, tests, build et E2E | [Run 40168955718](https://github.com/remi-deher/encoding/actions/runs/40168955718) | Réussi |
| Image Docker de l’application | [Run 40168955744](https://github.com/remi-deher/encoding/actions/runs/40168955744) | Réussi |
| Nouvelle mesure 7 000 tâches, deux SQLite | [Job 121096520847](https://github.com/remi-deher/encoding/actions/runs/40169232482/job/121096520847) | Réussi |

Les scénarios Docker tournent dans des conteneurs réels sur un runner : ils ne prouvent pas une installation sur deux machines physiques ni les propriétés d’un NAS partagé. Les contrats de reprise et leurs limites sont décrits dans l’ADR 0016 et le guide d’exploitation. Aucune nouvelle fonction métier n’est laissée à une IA pour terminer D3–D5 ; les extensions et validations de déploiement hors de ce périmètre restent explicitement recensées.
