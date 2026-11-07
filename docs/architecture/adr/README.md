# Registre des décisions (ADR)

> **Vocabulaire.** Les ADR antérieurs au 2026-11-02 emploient « nœud » et « cluster » : depuis l'[ADR 0040](0040-vocabulaire-central-worker.md), il n'y a plus qu'un **central** et des **workers**. Ces textes sont conservés tels quels (archives de décision) ; la fiche de domaine fait foi.

Les ADR de cette série commencent à **0001** (la série historique est retirée, voir [legacy-adr-map.md](../legacy-adr-map.md) ; les numéros de commentaires du code comme « 0049 » désignent l'ancienne série).

## Quand écrire un ADR
Une décision structurante : nouveau principe, changement d'un invariant des fiches de domaine, choix entre plusieurs alternatives, dérogation à une règle d'`AGENTS.md`. Pas pour documenter une fonctionnalité : cela va dans la fiche du domaine.

## Règles
- Un fichier `NNNN-sujet-court.md`, numéro suivant libre (consulter ce dossier **et les PR ouvertes** avant d'en prendre un : deux ADR ne partagent jamais un numéro).
- Court : contexte (≤ 10 lignes), décision, alternatives écartées, conséquences et risques. Utiliser [TEMPLATE.md](TEMPLATE.md).
- Statuts autorisés : `proposed`, `accepted`, `implemented`, `superseded`, `abandoned`. Un ADR remplacé n'est pas réécrit : on ajoute `superseded_by` et un nouvel ADR.
- Une décision de Rémi est citée avec sa date ; elle n'est pas modifiée sans son accord.
- Dès qu'un ADR est `implemented`, la fiche du domaine devient la référence et mentionne l'ADR dans ses sources.

## Registre

| Numéro | Titre | Statut | Domaine |
| --- | --- | --- | --- |
| [0001](0001-replication-connexions-ia.md) | Réplication chiffrée des connexions IA | superseded | ai |
| [0002](0002-modification-mkv-sur-place.md) | Modification MKV sur place par bloc dédié | implemented | runtime |
| [0003](0003-publication-etape-de-flow.md) | Publication comme étape de flow, distincte de la fin du flow | implemented | flows |
| [0004](0004-reprise-au-checkpoint.md) | Reprise au checkpoint après perte du worker, blocs rejouables | superseded | runtime |
| [0005](0005-bloc-bibliotheque-sur-ce-noeud.md) | Bloc « Bibliothèque sur ce nœud » : brancher un flow selon l'accès direct | implemented | flows |
| [0006](0006-ressources-de-flow.md) | Ressources de flow : fichiers partagés du cluster (polices, images, audio) | accepted | flows |
| [0007](0007-decodage-unique-avant-publication.md) | Un seul décodage complet, juste avant la publication | superseded | runtime |
| [0008](0008-assistant-de-configuration-plex.md) | Assistant de configuration Plex : connexion plex.tv et correspondance des bibliothèques | implemented | plugins |
| [0009](0009-ecrivain-sqlite-unique-par-noeud.md) | Écrivain SQLite unique par nœud | implemented | storage |
| [0010](0010-capture-incrementale-locale.md) | Capture incrémentale locale par clés modifiées | superseded | storage |
| [0011](0011-controles-media-selon-effets.md) | Contrôles média selon les effets des opérations | implemented | runtime |
| [0012](0012-builder-ffmpeg-et-assemblage.md) | Builder FFmpeg explicite et assemblage MKV atomique | implemented | runtime |
| [0013](0013-retrait-mode-raft.md) | Retrait du mode de consensus SQL | implemented | storage |
| [0014](0014-attente-des-ecritures-source.md) | Attente durable des écritures sur les sources en cluster | superseded | runtime |
| [0015](0015-attribution-journalisee-des-taches.md) | Attribution initiale journalisée des tâches | superseded | runtime |
| [0016](0016-transferts-et-reprise-manuelle.md) | Transferts inter-serveurs et reprise manuelle sans quorum | superseded | runtime |
| [0017](0017-parametres-de-configuration-des-modeles-de-flow.md) | Paramètres de configuration portés par les modèles de flow | implemented | flows |
| [0018](0018-shell-adaptatif-et-palettes.md) | Shell adaptatif et palettes Material partagées | implemented | ui |
| [0019](0019-recherche-contextuelle-du-shell.md) | Recherche contextuelle dans le shell | implemented | ui |
| [0020](0020-gabarit-partage-de-sous-navigation.md) | Gabarit partagé de sous-navigation | implemented | ui |
| [0021](0021-activation-cluster-plugins-outils.md) | Activation cluster des plugins et outils média | implemented | plugins |
| [0022](0022-controle-media-explicite-et-rattachement.md) | Contrôle média explicite et prérequis de rattachement | superseded | runtime, cluster |
| [0023](0023-coordination-et-quarantaine.md) | Coordination des tâches en quarantaine sans blocage global | superseded | runtime, replication-storage |
| [0024](0024-choix-du-noeud-avant-execution.md) | Choix du nœud avant exécution et minimisation des transferts | implemented | runtime |
| [0025](0025-liberation-des-reprises-source-apres-neutralisation.md) | Libération d'une reprise source après neutralisation | superseded | runtime |
| [0026](0026-reprise-auto-reparatrice-deux-noeuds.md) | Reprise auto-réparatrice à deux nœuds | superseded | runtime |
| [0027](0027-controle-integrite-optionnel.md) | Contrôle d'intégrité facultatif par bloc de flow | implemented | runtime, flows |
| [0028](0028-publication-locale-et-journaux-operations.md) | Publication locale et journaux d'opérations | implemented | runtime |
| [0029](0029-store-plugins-outils-hashes.md) | Store commun pour plugins et outils, identifiés par hash | accepted | plugins |
| [0030](0030-migration-composants-et-plugins-historiques-au-store.md) | Migration des composants et plugins historiques vers le Store | accepted | plugins |
| [0031](0031-frontieres-backend-verifiees-par-tests.md) | Frontières du backend vérifiées par des tests d'architecture | accepted | runtime |
| [0032](0032-serveur-central-fixe.md) | Serveur central fixe et workers qui ne deviennent pas serveurs | superseded | workers |
| [0033](0033-images-central-et-worker.md) | Image centrale autonome et image worker sans base | superseded | workers |
| [0034](0034-rattachement-par-compte-administrateur.md) | Rattachement du worker par compte administrateur | implemented | workers |
| [0035](0035-depot-officiel-et-activation.md) | Dépôt officiel fixe et activation explicite | implemented | plugins |
| [0036](0036-separation-stricte-central-worker.md) | Séparation stricte du central et du worker | accepted | workers |
| [0037](0037-ressources-a-plat.md) | Ressources de flow à plat, une version par fichier | implemented | flows |
| [0038](0038-profils-et-depot-complet.md) | Profils de sous-titres et dépôt complet | implemented | plugins |
| [0039](0039-version-exacte-worker-central.md) | Un worker doit avoir la version exacte du central | implemented | workers |
| [0040](0040-vocabulaire-central-worker.md) | Vocabulaire central / worker | accepted | workers |
| [0041](0041-sse-a-la-place-du-sondage.md) | SSE à la place du sondage, pour l'interface et pour les workers | accepted | runtime |
| [0042](0042-reseau-plugins-capacite-et-seccomp.md) | Réseau des plugins : capacité déclarée, Landlock ABI 4 et seccomp minimal | proposed | plugins |
| [0043](0043-sql-sqlite-natif.md) | SQL SQLite natif, sans couche de traduction | implemented | storage |
| [0044](0044-echec-apres-perte-de-bail.md) | Échec après perte de bail sans réattribution automatique | implemented | runtime |
| [0045](0045-websocket-telemetrie-worker.md) | WebSocket de télémétrie worker → central | accepted | runtime |
| [0046](0046-etat-store-des-workers-suit-le-central.md) | L'état Store des workers suit celui du central ; worker non conforme sans attribution | proposed | plugins |
| [0047](0047-cycle-test-dev-main-et-images-par-branche.md) | Cycle de branches test → dev → main et images Docker par branche | accepted | auth-ops |
