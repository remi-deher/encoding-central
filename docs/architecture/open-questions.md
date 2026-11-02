# Écarts constatés et décisions en attente

Dressé le 2026-10-21 en relisant les anciens ADR contre le code de `dev`. Cette liste conserve les constats et décisions en attente ; un point marqué « décision prise » est désormais suivi dans sa fiche de domaine et son ADR.

## À traiter en priorité (production)

### 1. Authentification par défaut : décidé (2026-10-21)
- Décision de Rémi : le setup d'un admin est imposé. `Auth:Enabled` vaut désormais `true` par défaut ; les écritures MCP exigent un jeton à portée explicite. Voir [auth-ops](domains/auth-ops.md). Reste à faire : vérifier sur les deux hôtes TrueNAS (comptes existants ? sinon l'assistant d'admin apparaîtra à la mise à jour) et ajouter un jeton MCP scopé pour les clients existants.

### 2. Deux mécanismes de réplication : retrait décidé et implémenté (2026-10-25)
Rémi retient le maillage LWW comme modèle unique. Le mode de consensus, sa dépendance, son coordinateur SQL, ses routes, tests et outils dédiés sont retirés (ADR [0013](adr/0013-retrait-mode-raft.md)). Le protocole du maillage ne change pas. La sortie d'une ancienne installation utilisant ce mode nécessite une sauvegarde et une transition explicite ; aucune conversion automatique n'est livrée. La reprise inter-serveurs est livrée séparément par l’ADR [0016](adr/0016-transferts-et-reprise-manuelle.md).

### 3. Sécurité du bac à sable : `/proc` et `/sys` lisibles
- Constaté : Landlock autorise la lecture de `/proc` et `/sys` (ajouté pour les CLI IA). `/proc/<pid>/environ` et `mem` de l'agent restent lisibles par un plugin du même utilisateur, limités seulement par les contrôles ptrace. Mesure déjà prise : environnement minimal de l'enfant. Un plugin .NET n'a jamais été essayé sous Landlock réel.
- Proposition : espace de noms PID ou utilisateur dédié aux plugins ; tester Landlock réel (aucun scénario Landlock n'a été exécuté en conteneur de développement).

### 4. Reprise après perte d’un serveur : décidée et implémentée (2026-10-25)
La reprise manuelle à deux serveurs est livrée par l’ADR [0016](adr/0016-transferts-et-reprise-manuelle.md) : neutralisation explicite de l’installation source et de ses workers, grâce de 135 s, checkpoint signé et fichiers vérifiés, époque supérieure, puis réconciliation avant réadmission. Un serveur injoignable n’est jamais considéré arrêté automatiquement. Les baux restent locaux. Témoin/majorité de reprise à trois serveurs et automatisation restent des extensions futures ; les validations multi-hôtes/NAS restent à réaliser.

## À décider (périmètre produit)

### 5. Relations entre clusters (ancien ADR 0038 §9, numéro 0042 réservé)
Transfert de nœuds entre clusters, partage de bibliothèques, propriété avec époque, garde-fous anti-doublon sans quorum : conçu, **jamais implémenté**. Garder comme feuille de route ou abandonner ?

### 6. Outils médias personnalisés / gérés — décision prise (2026-10-30)
Rémi retient un dépôt public unique pour plugins et outils, paquets d'outils partagés par rôle, versions contraintes par les plugins et hashes SHA-256 d'archive et d'exécutable. Le socle `tool.json`, Store serveur, import depuis un dépôt, distribution worker, résolution par rôle et affichage du Store sont implémentés (ADR [0029](adr/0029-store-plugins-outils-hashes.md)). Le dépôt public `remi-deher/encoding-store` contient maintenant les quatre intégrations historiques et les dix familles de manifestes natives (`base`, `nodes`, `conditions` et les sept familles média) ; les sources/tests des intégrations Python y vivent aussi. Le moteur ne préinstalle plus ces composants ; les handlers natifs privilégiés restent dans le moteur C#. Les paquets binaires FFmpeg/FFprobe, MKVToolNix, MediaInfo et ImageMagick pour linux-x64 Ubuntu Noble sont publiés dans le Store ; leurs hashes d'exécutables et d'archives sont contrôlés. L'image conserve leurs bibliothèques de runtime mais plus les exécutables. Les paquets pour les autres plateformes, les signatures de catalogues/paquets et la validation cluster multi-plateforme/matériel réel restent à faire. L'état du Store reste local à chaque serveur.

### 7. Store de plugins : compléments
Déjà livré (PR #205), complété par l'ADR [0029](adr/0029-store-plugins-outils-hashes.md). Reste : réplication de l'état entre serveurs (aujourd'hui local), signatures, `minEngineVersion`, désactivation sans retrait, refus de retrait d'un plugin utilisé, synchronisation périodique et choix de l'adresse du dépôt officiel.

### 8. ~~Aucun déclencheur « après publication »~~ — traitée
Résolue par l'ADR [0003](adr/0003-publication-etape-de-flow.md) : le bloc `base-publish-result` publie au milieu du flow et les blocs Plex, Radarr, Sonarr, Jellyfin le suivent. `media.published` reste un événement d'audit. Reste : un runner réel n'a pas encore exécuté le bloc de bout en bout, et le modèle de flow livré (FileFlows V3) n'utilise pas encore ce bloc.

### 9. Compatibilité entre versions de nœuds
Seule la version de protocole (`Current = 1`) est contrôlée. Pas de plage de schéma, pas de règle « serveurs avant workers ». Les migrations doivent rester additives en attendant.

## Hygiène du dépôt

- `backend/migrations/0036_library_cluster_tombstones.sql` (racine) n'est pas chargé par le migrateur (seul `migrations/sqlite/` l'est) : reliquat à supprimer.
- Les instructions du projet (mission) et la mémoire d'équipe citent encore PostgreSQL ; le code, `AGENTS.md` et la production (vérifiée le 2026-10-21) sont en SQLite seul. À aligner côté instructions du projet, hors dépôt.
- Documents d'exploitation : ré-audités le 2026-10-21 (index dans `docs/operations/README.md`). Restent à ré-auditer : `docs/design/*`.
- `backend/README.md` : sections datées par numéros de migration (« la migration 0008 ajoute… ») à réécrire en renvoyant vers les fiches.

## Non vérifié en conditions réelles (à ne pas présenter comme garanti)
Landlock en conteneur de production ; GPU réel (QSV, VAAPI, NVENC) ; Plex/Jellyfin/Sonarr/Radarr réels ; NAS/NFS (atomicité du renommage et des verrous) ; cluster multi-hôtes sous charge (changement de mode, reprise après perte de worker) ; thème sombre et mobile de la page Nœuds et de l'éditeur de flow.

## Reprise après perte du worker (ADR 0004)
Livrée : reprise au dernier checkpoint, bornée à 2, blocs rejouables. Reste à décider : attendre le retour du nœud d'accès (au lieu d'échouer) quand le traitement continue ailleurs ; reprendre le verrou de disque libéré ; checkpoint intermédiaire à l'intérieur d'un encodage long (aujourd'hui, le bloc est refait depuis son début).


## Cluster sans quorum : cadrage du 2026-10-25
- Proposition D1–D6 et audit : [revue](../design/cluster-without-quorum-review.md). Le retrait est implémenté par l’ADR 0013 ; l’attribution, les transferts et la reprise manuelle sont livrés par les ADR 0015 et 0016.
- Décision de Rémi : à deux nœuds, reprendre la file d'un serveur exige sa **neutralisation explicite**, y compris son autorité de publication ; sinon il conserve sa file. Une expiration estimée côté pair et une époque inconnue du serveur isolé ne constituent pas un fencing. La reprise manuelle livrée respecte cette précondition ; elle n’apporte pas de neutralisation technique à distance.
- Priorité D6 : la [sonde de coordination](../testing/coordination-measurements.md) couvre aussi 7 000 tâches et deux workers sur un serveur. La mesure complémentaire à [deux serveurs logiques](../testing/cluster-two-server-measurements.md) couvre capture concurrente et latences HTTP état/historique. Deux hôtes physiques, NAS et activité disque restent non vérifiés ; ne pas présenter ces sondes locales comme leur validation.

### Suivi de la proposition « cluster sans quorum » (2026-10-25)
- Livré : retrait du mode de consensus, maillage inchangé ; mesures initiales à 7 000 travaux et jitter ; protection des sources et attentes durables en partition (ADR 0014).
- Fondation D3 : attribution initiale signée atomique et rattrapage initial avant claims (ADR 0015). Ce volet est complété par les transferts et la reprise manuelle de l’ADR 0016.
- Livré par ADR 0016 : cession explicite depuis le propriétaire initial, transfert en deux phases, acheminement SHA-256, chaînes `nodes-handoff` entre serveurs, gossip signé avec TTL, quarantaine des doublons, indicateurs UI et reprise manuelle à deux serveurs après neutralisation/grâce. La majorité ou le témoin de reprise à trois serveurs reste une extension future, sans modification du maillage.
- Toujours non vérifié : charge à 7 000 travaux sur deux serveurs réels, partition entre hôtes et exclusion au chemin d'un média sur NAS. Une mesure complémentaire sur deux serveurs logiques, avec capture concurrente et les nouveaux checkpoints, est décrite dans `docs/testing/cluster-two-server-measurements.md` ; elle ne valide pas deux hôtes physiques ou un NAS.
