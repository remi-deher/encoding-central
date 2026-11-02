---
domain: replication-storage
updated: 2026-11-02
legacy_adr: [0001, 0004, 0029, 0043]
code:
  - backend/src/Encoding.Api/Infrastructure/Data/
  - backend/src/Encoding.Api/Infrastructure/DatabaseMigrator.cs
  - backend/migrations/sqlite/
tags: [sqlite, migrations, sauvegarde, registres, clé de données]
---

# Stockage

Le central est l'unique serveur ([ADR 0036](../adr/0036-separation-stricte-central-worker.md)) : sa base SQLite est la seule. Il n'y a **plus de réplication** entre serveurs (maillage, journal, capture des changements, projections) ; la migration 0066 retire les déclencheurs de capture. La migration 0067 supprime les tables et colonnes de la réplication (journal, pairs, projections, capture, transferts, neutralisation, sauvegarde miroir, `desired_roles`). Restent `cluster_sync_registers` (registres locaux : activation des plugins et des outils, sessions IA chiffrées), `task_ownership` et `task_server_identity` (le central y est l'unique propriétaire).

## État courant

### Stockage : SQLite local par serveur
- SQLite est le **seul** moteur (PostgreSQL a été retiré ; ne pas le réintroduire sans décision). Chaque serveur ouvre son `cluster.db` (`ConnectionStrings:Cluster`, défaut `Data Source=./data/cluster.db`) : WAL, clés étrangères, délai d'écriture, transactions d'écriture immédiate. **Jamais** de fichier partagé entre hôtes ni sur stockage réseau.
- Constaté en production le 2026-10-21 (lecture seule, deux hôtes TrueNAS) : un seul conteneur `encoding` par hôte, `ConnectionStrings__Cluster=Data Source=/var/lib/encoding-node/cluster.db` monté depuis un dataset local, WAL actif, aucun conteneur ni connexion PostgreSQL pour encoding, aucune erreur `database is locked` dans les derniers logs.
- Les dépôts écrivent encore du SQL au dialecte historique (`FOR UPDATE SKIP LOCKED`, `'infinity'`, `clock_timestamp()`, `gen_random_uuid()`, `<@`) : `SqliteTranslator` l'adapte à l'exécution et échoue sur une construction inconnue. Dates en texte UTC de largeur fixe, UUID et JSON en texte. Ajouter une construction SQL nouvelle = étendre le traducteur et ses tests.
- Migrations : `backend/migrations/sqlite/NNNN_description.sql`, **numéro unique** (le chargement échoue sinon), empreinte SHA-256 dans `schema_migrations.checksum` (modifier une migration appliquée bloque le démarrage : en écrire une nouvelle). `MigrationCatalog.RenamedFrom` relie un fichier renommé. Baseline `0033_baseline.sql`, dernière migration connue `0042_system_logs.sql` (vérifier le dossier avant d'en créer : les threads parallèles doivent se coordonner sur le prochain numéro). Une version enregistrée absente du binaire rend le nœud `degraded`.
- `backend/migrations/0036_library_cluster_tombstones.sql` (racine, hors `sqlite/`) n'est pas chargé : reliquat à supprimer ou confirmer.

### Écrivain local (ADR [0009](../adr/0009-ecrivain-sqlite-unique-par-noeud.md))
- `SqliteDataSource`, singleton du serveur, possède une file d’admission locale commune à toutes ses connexions. Les transactions d’écriture y entrent avant `BEGIN IMMEDIATE` et libèrent leur admission après commit, rollback ou fermeture. Les commandes hors transaction y passent aussi, quel que soit leur mode d’exécution ; un lecteur `UPDATE … RETURNING` garde l’admission jusqu’à sa fermeture. L’appelant admis exécute son unité : aucun thread d’exécution dédié ni transfert du code métier.
- Présence (`RecordHeartbeatAsync`) et renouvellement des baux (`RenewAsync`) prioritaires, avec 32 places réservées ; 256 places ordinaires. FIFO dans chaque priorité ; une écriture ordinaire passe après huit urgentes au plus. Attente bornée à 5 s, saturation refusée immédiatement. Ces limites sont configurables sous `Database:Writer`.
- Une transaction reste une unité atomique, sans regroupement de transactions indépendantes (lot = 1). Aucun code métier n’est rejoué. Seul `BEGIN`, avant la première instruction métier, retente deux fois les erreurs SQLite 5/6 (attentes de 25 puis 50 ms, délai SQLite de 1 s par tentative). Une erreur de commande n’est pas rejouée, car un lot hors transaction peut avoir produit un effet partiel.
- `IsolationLevel.Snapshot` ouvre une transaction différée avec `query_only=ON`, réinitialisé à sa clôture : aucune admission d’écriture et aucune promotion en écriture. Les lectures SELECT simples hors transaction contournent aussi la file ; les lots/CTE suivent conservativement l’admission, sans ouvrir de transaction d’écriture implicite.
- Métriques `encoding_sqlite_writer_*` sur `/metrics` : files, admission active, refus, expirations, tentatives, temps cumulé d’attente/détention et détentions longues ; avertissement Trace après détention supérieure à 1 s. Saturation, expiration et erreurs SQLite 5/6 deviennent HTTP 503 avec code `sqlite_write_unavailable` et `Retry-After: 1`, sans SQL dans la réponse.
- Déploiement par étapes : `Database:Writer:Enabled=false` mesure les unités sans les sérialiser ; `true` active la file dans l’adaptateur commun, d’abord pour les chemins critiques avec `CriticalOnly=true`, puis pour tous les domaines avec `CriticalOnly=false` (défaut final). La phase critique seule conserve des écritures ordinaires directes et ne revendique pas encore l’exclusivité globale. Les migrations et copies de base utilisent aussi cette admission. Exceptions d’infrastructure : initialisation des PRAGMA de connexion et garde `query_only`. Le serveur n’a qu’une source SQLite locale et un écrivain commun. Ne pas ajouter d’accès SQLite brut hors de ces exceptions.
- Les délais bornent l’attente avant admission, pas une transaction déjà engagée. Une annulation en attente retire la demande ; après admission, l’appelant conserve la responsabilité de terminer ou d’annuler sa transaction et la file ne la rejoue jamais. Les transactions longues restent à réduire à partir des mesures. Exploitation et limites : [sqlite-writer.md](../../operations/sqlite-writer.md). Aucune validation sous charge de production ni entre versions mixtes n’est revendiquée.

### Sondages de sauvegarde
- La présence d’un travail de sauvegarde dû est consultée sans transaction d’écriture ; le travail est relu sous transaction avant traitement. Un sondage vide ne prend donc pas le verrou SQLite. La publication atomique et les délais de reprise restent inchangés.

### Sauvegardes de flows
- Chaque version publiée et chaque révision de brouillon écrit un travail local dans la même transaction ; `{id}.v{version}.json` est écrit atomiquement (temporaire, flush, déplacement sans écrasement) dans `FlowBackups:Directory` (volume séparé). Un fichier existant d'empreinte différente est conservé et signalé `failed`. Retentative 30 s, revérification 10 min, aucune suppression automatique. Statuts `pending/ready/failed` **locaux** à chaque serveur. Les définitions de `flow_draft_backups` convergent par LWW sans bloquer la file de projection tout en préservant le statut local (ADR [0026](../adr/0026-reprise-auto-reparatrice-deux-noeuds.md)). Les fichiers sont des instantanés récupérables, pas une autorité d'écriture.
- Restauration : réimport par `PUT` dans l'ordre des versions (identité conservée, `expectedVersion` = précédente) ou import JSON/ZIP ([flows.md](flows.md)).

## Invariants
1. Un fichier SQLite appartient à un seul serveur.
2. Toute nouvelle table est locale au central : le documenter ici si elle n'est pas évidente.
3. Une migration appliquée ne se modifie jamais ; numéro unique ; additive tant que N/N-1 n'est pas garanti.
4. Les secrets ne vont jamais en clair dans les sauvegardes, les exports ni les journaux.

## Ressources de flow (ADR 0006, PR 1)
- Métadonnées (ressource, versions immuables, noms de fichiers, tailles, SHA-256) en base ; **octets** dans `Resources:Directory` (défaut `<Node:DataDirectory>/resources/ab/<sha256>`), jamais dans le journal : c'est l'exception assumée à l'invariant « binaires jamais réconciliés ».
- Envoi : `POST /api/v1/resources/blobs?kind=font|subtitle-profile|subtitle&name=…` (corps brut, un fichier, 20 Mio au plus, extension et signature contrôlées), puis `POST /api/v1/resources` ou `…/{id}/versions` avec une seule empreinte. Lecture du fichier : `GET /api/v1/resources/{id}/versions/{version}/content` (session). Écriture réservée à `admin`, lecture à tout rôle. Une ressource est un fichier ([ADR 0032](../adr/0032-ressources-a-plat.md), types ouverts par [ADR 0033](../adr/0033-profils-et-depot-complet.md)).
- Suppression : refusée (409) tant qu'un flow, toute version confondue, référence l'identifiant ; le message nomme le flow et le bloc. Un fichier n'est effacé du magasin que si aucune version ne l'utilise encore.
- Worker (PR 3) : `GET /api/v1/runtime/nodes/{nodeId}/resources/{id}/versions/{n}` (liste des fichiers) et `…/resources/blobs/{sha256}` (octets), authentifiés par le jeton de nœud. Les polices, le profil de sous-titres et le fichier de sous-titres cités par un bloc passent par ce chemin.
- Non livré : interface (PR 4).  Un fichier envoyé sans être référencé reste dans le magasin (nettoyage à prévoir).

## Non livré / limites
- Pas de plage de schéma ni de règle de mise à jour progressive entre serveurs.
- Pas de restauration guidée des sauvegardes de flows ; pas de rétention des sauvegardes.
- Les artefacts doivent être sur un stockage accessible aux serveurs concernés (`Runtime:ArtifactDirectory`) ; un volume local n'établit pas de haute disponibilité inter-hôtes.
- Maillage : un changement qui conserve taille et date n'est pas concerné ici (voir sondage de surveillance dans [runtime.md](runtime.md)) ; deux créations isolées de même nom attendent la résolution de la collision.

## Index du runtime
- `0048_runtime_lookup_indexes.sql` ajoute des index de recherche des baux et checkpoints, sans nouvelle table ni changement de protocole. Les index sont locaux à la base et reconstruits par les migrations sur chaque serveur ; ne pas les répliquer dans le journal métier. Voir les [plans vérifiés](../../testing/sqlite-runtime-query-plans.md).

