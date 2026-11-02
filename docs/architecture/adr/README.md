# Registre des décisions (ADR)

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
| [0001](0001-replication-connexions-ia.md) | Réplication chiffrée des connexions IA | implemented | ai |
| [0002](0002-modification-mkv-sur-place.md) | Modification MKV sur place par bloc dédié | implemented | runtime |
| [0003](0003-publication-etape-de-flow.md) | Publication comme étape de flow, distincte de la fin du flow | implemented | flows |
| [0004](0004-reprise-au-checkpoint.md) | Reprise au checkpoint après perte du worker, blocs rejouables | implemented | runtime |
| [0005](0005-bloc-bibliotheque-sur-ce-noeud.md) | Bloc « Bibliothèque sur ce nœud » : brancher un flow selon l'accès direct | implemented | flows |
| [0006](0006-ressources-de-flow.md) | Ressources de flow : fichiers partagés du cluster (polices, images, audio) | accepted | flows |
| [0007](0007-decodage-unique-avant-publication.md) | Un seul décodage complet, juste avant la publication | superseded | runtime |
| [0008](0008-assistant-de-configuration-plex.md) | Assistant de configuration Plex : connexion plex.tv et correspondance des bibliothèques | implemented | plugins |
| [0009](0009-ecrivain-sqlite-unique-par-noeud.md) | Écrivain SQLite unique par nœud | implemented | replication-storage |
| [0010](0010-capture-incrementale-locale.md) | Capture incrémentale locale par clés modifiées | implemented | replication-storage |
| [0011](0011-controles-media-selon-effets.md) | Contrôles média selon les effets des opérations | implemented | runtime |
| [0012](0012-builder-ffmpeg-et-assemblage.md) | Builder FFmpeg explicite et assemblage MKV atomique | implemented | runtime |
| [0013](0013-retrait-mode-raft.md) | Retrait du mode de consensus SQL | implemented | replication-storage |
| [0014](0014-attente-des-ecritures-source.md) | Attente durable des écritures sur les sources en cluster | implemented | runtime |
| [0015](0015-attribution-journalisee-des-taches.md) | Attribution initiale journalisée des tâches | implemented | runtime |
| [0016](0016-transferts-et-reprise-manuelle.md) | Transferts inter-serveurs et reprise manuelle sans quorum | implemented | runtime |
| [0017](0017-parametres-de-configuration-des-modeles-de-flow.md) | Paramètres de configuration portés par les modèles de flow | implemented | flows |
| [0018](0018-shell-adaptatif-et-palettes.md) | Shell adaptatif et palettes Material partagées | implemented | ui |
| [0019](0019-recherche-contextuelle-du-shell.md) | Recherche contextuelle dans le shell | implemented | ui |
| [0020](0020-gabarit-partage-de-sous-navigation.md) | Gabarit partagé de sous-navigation | implemented | ui |
| [0021](0021-activation-cluster-plugins-outils.md) | Activation cluster des plugins et outils média | implemented | plugins |
| [0022](0022-controle-media-explicite-et-rattachement.md) | Contrôle média explicite et prérequis de rattachement | superseded | runtime, cluster |
| [0023](0023-coordination-et-quarantaine.md) | Coordination des tâches en quarantaine sans blocage global | implemented | runtime, replication-storage |
| [0024](0024-choix-du-noeud-avant-execution.md) | Choix du nœud avant exécution et minimisation des transferts | implemented | runtime |
| [0025](0025-liberation-des-reprises-source-apres-neutralisation.md) | Libération d'une reprise source après neutralisation | implemented | runtime |
| [0026](0026-reprise-auto-reparatrice-deux-noeuds.md) | Reprise auto-réparatrice à deux nœuds | implemented | runtime |
| [0027](0027-controle-integrite-optionnel.md) | Contrôle d'intégrité facultatif par bloc de flow | implemented | runtime, flows |
| [0028](0028-publication-locale-et-journaux-operations.md) | Publication locale et journaux d'opérations | implemented | runtime |
| [0029](0029-store-plugins-outils-hashes.md) | Store commun pour plugins et outils, identifiés par hash | accepted | plugins |
