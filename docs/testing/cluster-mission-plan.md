<!-- Plan fourni par le propriétaire ; conservé pour la reprise de la mission. -->

# Proposition : cluster sans quorum — retrait de Raft, attribution planifiée et continuité de service

## Contexte

Application auto-hébergée de traitement de bibliothèques multimédias (C# / ASP.NET Core / .NET 10, Angular, SQLite local par serveur, image Docker unique). Le cluster cible est de **deux nœuds** aujourd'hui, devant pouvoir passer à **trois** sans refonte (une majorité à 3 nœuds pourra alors être introduite, limitée aux décisions de reprise). Chaque nœud reste **autonome** : aucun serveur central, aucune dépendance synchrone au pair sur le chemin critique.

La haute disponibilité visée :
- chaque nœud connaît l'état des autres et adapte son comportement ;
- à terme, la file d'un serveur arrêté peut être reprise par un serveur ayant les capacités ;
- au retour d'un serveur, il réconcilie l'état de ses tâches **avant** de reprendre, pour ne jamais refaire un travail déjà fait.

## Décisions structurantes

### D1 — Retrait du mode Raft (`PeerCluster__Enabled`)

**Motivation :**

- À deux nœuds, un quorum exige les deux votes : la panne d'un nœud bloque toute écriture. Raft supprime exactement l'indépendance recherchée. Il ne devient pertinent qu'à partir de 3 serveurs votants.
- Deux modèles de cohérence opposés coexistent : maillage LWW (convergence éventuelle, sans quorum — principe affiché du produit) et Raft (quorum, exclusion forte). Un contributeur ne sait pas quel modèle étendre ; les garanties d'exclusion de travail diffèrent radicalement selon le mode actif.
- Coût de maintenance pour un usage non démontré : ~15 fichiers dans `backend/src/Encoding.Api/Infrastructure/PeerCluster/`, un coordinateur distinct, la capture SQL brute de `PeerDatabaseSchema` qui contourne l'écrivain SQLite unique, des documents d'exploitation dédiés (`docs/operations/peer-cluster-preview.md`, `peer-cluster-production-todo.md`, `peer-cluster-alerts.yml`), pas de compatibilité N/N-1.
- Les invariants de protection des médias sont déjà portés par la réservation locale + époque de bail + propriété du serveur créateur, sans besoin d'exclusion forte distribuée.
- Si une exclusion forte devient nécessaire à 3+ nœuds, elle portera **uniquement sur la table de propriété des tâches** (quelques Ko), pas sur les transactions SQL entières — un mini-consensus dédié, dans un module séparé.

**Portée du retrait :**

- Supprimer `backend/src/Encoding.Api/Infrastructure/PeerCluster/`, les options `PeerCluster__*`, le `PeerDataSource`/`PeerLocalDatabase`, la branche Raft dans la composition DI (`Program.cs`), les tests dédiés et les exceptions d'infrastructure qui ne servent que Raft (capture SQL brute dans l'adaptateur SQLite).
- Supprimer la dépendance `DotNext.AspNetCore.Cluster` de `Encoding.Api.csproj`.
- Supprimer ou archiver `docs/operations/peer-cluster-preview.md`, `peer-cluster-production-todo.md`, `docs/operations/peer-cluster-alerts.yml` et `backend/tests/monitoring/` associé, ainsi que `.github/workflows/monitoring.yml` si son seul objet est le mode pair.
- Mettre à jour `docs/architecture/domains/replication-storage.md` (le maillage LWW devient le seul modèle), `docs/architecture/domains/cluster.md`, `docs/architecture/open-questions.md` (clôturer le point 2) et écrire un ADR court `docs/architecture/adr/00XX-retrait-mode-raft.md`.
- Vérifier que les workflows CI `backend.yml` / `monitoring.yml` et `.github/scripts/ci-scopes.mjs` restent cohérents après suppression des chemins concernés.

### D2 — Un seul modèle de cohérence : maillage LWW (inchangé)

Conserver le système actuel comme modèle unique : journal append-only signé, déduplication `(origine, séquence)`, dernier écrivain gagne par entité (horodatage UTC, puis origine/séquence), tombstones, capture incrémentale par triggers (migration 0049), secrets chiffrés AES-GCM avec la clé de données du cluster. **Ne rien changer au protocole d'échange** ; le retrait de Raft ne touche pas le maillage.

### D3 — File de travail : attribution planifiée, propriété certaine, transfert en deux phases

Principe : **toute tâche a exactement un propriétaire connu à tout instant**, et jamais deux propriétaires actifs. Les baux, claims et renouvellements restent locaux et synchrones avec la seule base du propriétaire.

- **Attribution au lancement** : au démarrage d'une file (batch de découverte, déclenchement utilisateur), le serveur initiateur produit un **plan d'attribution** — un événement unique du journal répliqué, signé, listant l'affectation tâche → serveur avec une époque. L'attribution respecte les règles existantes (capacités, encodeurs vérifiés, accès bibliothèque, plafonds, segments `nodes-*`) ou une répartition de charge ; le processus métier du flow peut la guider implicitement, comme aujourd'hui. **L'attribution est la création** : pas de tâche sans propriétaire, pas de propriétaire ambigu.
- **Chaînes multi-serveurs** : une tâche peut comporter des étapes planifiées sur plusieurs serveurs (ex. A encode → transfert → B remuxe). Le transit se fait à checkpoint durable, avec transfert de fichier vérifié (SHA-256), en généralisant le modèle `nodes-handoff` du niveau worker au niveau serveur ; le passage de relais suit le même protocole de transfert en deux phases ci-dessous.
- **Transfert en deux phases — réception ≠ activation** (déplacement administrateur, cession, relais inter-serveurs) :
  1. **Proposition** : la source gèle la tâche localement (plus de claim, plus de renouvellement nouveau) et envoie la proposition.
  2. **Réception** : la cible enregistre la tâche comme **`received`, inactive** — elle ne la traite pas — et accuse réception. Tant que l'accusé n'arrive pas, la source reste propriétaire et peut dégeler ; un déplacement sans réponse est refusé.
  3. **Activation** : la source journalise l'événement `activation` (époque +1) **après avoir gelé** et cesse définitivement d'être propriétaire ; la cible ne devient active qu'à la réception de cet événement (direct ou rattrapé via le journal).
  - Conséquence : source active ⟺ activation non émise ; cible active ⟺ activation reçue. **Aucun instant ne compte deux propriétaires actifs.** Une activation perdue laisse la tâche inactive des deux côtés (sûr, visible, reprise par réconciliation) plutôt que dupliquée.
  - La source meurt avant l'accusé ⇒ le transfert n'a jamais existé ; la tâche relève du chemin de reprise (D5).
- **Réévaluation** : les capacités déclarées au moment du plan peuvent changer ; le claim local revérifie l'éligibilité comme aujourd'hui. Une tâche devenue inéligible chez son propriétaire suit une règle bornée : replanification explicite ou échec visible — jamais de déplacement silencieux.

### D4 — Comportement en partition : le travail possédé continue, le destructif attend

Un nœud qui ne voit plus son pair **n'est pas mis en pause**. La propriété étant exclusive et attribuée avant la coupure :

**Autorisé en isolement :**
- Exécuter ses propres tâches (elles sont à lui, aucun conflit de bail possible).
- **Publier sans écrasement** dans `.encoding-results/<executionId>/` : la propriété ne peut pas avoir bougé pendant l'isolement (tout transfert exige journal ou accusé). Un doublon éventuel produit deux résultats distincts, résolus à la réconciliation.
- Servir son interface, ses workers locaux, sa réconciliation dès que le lien revient.

**Différé en isolement — opérations destructrices :**
- Remplacement de la source (politique de bibliothèque) et `mkv-edit-in-place` **exigent la connectivité cluster** : pair joignable, ou témoin/majorité à 3+ nœuds. En isolement, l'étape devient une attente durable (type `base-wait` sur `nodeState`) jusqu'au retour du lien ; l'exécution n'échoue pas, le résultat validé attend dans `.encoding-results/`.
- C'est le **fencing par la connectivité** : même si un média est traité deux fois en partition (double plan, ou reprise sur un pair seulement injoignable), **aucune des deux copies ne peut modifier la source**. La résolution de conflit à la réconciliation reste un choix de résultat, jamais une restauration de média corrompu.

**Suspendu en isolement (coordination) :** cessions, déplacements, reprises de la file du pair, retrait de nœud, changements de topologie — refusés avec message explicite ou fusionnés selon LWW documenté au cas par cas.

**Doublons de création — filet de sécurité :** deux files lancées en parallèle pendant la partition (surveillance des deux côtés d'un même NAS, déclenchement sur les deux) peuvent produire deux plans couvrant le même média. La réservation répliquée `(library_id, media_key)` révèle le doublon à la réconciliation ; une règle déterministe désigne le gagnant (`queued_at` le plus ancien, puis identifiant de serveur), le résultat du perdant part en quarantaine. Grâce au fencing par la connectivité, **le pire cas est un encodage fait deux fois — jamais une source remplacée ou modifiée en conflit**.

**État des pairs partagé transitivement (gossip) :** chaque serveur partage ses observations de présence via le maillage ; deux serveurs qui ne se voient pas directement apprennent l'état l'un de l'autre par un tiers. Garde-fous : provenance obligatoire (« B a observé C injoignable à t »), séquence par origine, TTL court. Une observation relayée est un **indice** (vérification directe, exclusion des cibles de cession), jamais une preuve de fencing.

### D5 — Reprise de la file d'un serveur défaillant : injoignable ≠ arrêté

**Constat fondateur :** LWW seul ne permet pas de réattribuer sûrement le travail d'un pair injoignable, car il peut être simplement partitionné et toujours en train de travailler. L'époque journalisée + la réconciliation avant reprise garantissent la convergence *après coup*, pas l'absence de double exécution *pendant*.

Règles de reprise :
1. **À 2 nœuds : reprise manuelle par défaut.** L'opérateur confirme explicitement que le serveur est arrêté (l'humain est le témoin). La reprise automatique exige un **témoin externe** ou passe à 3 nœuds avec constatation majoritaire (2/3) — cette majorité porte sur la décision de reprise seule, pas sur les données.
2. **Délai de grâce** : ≥ 2× la durée de bail + marge d'horloge, pour laisser un pair lent expirer naturellement.
3. **Reprise journalisée avec époque** : événement signé transférant la propriété avec une époque supérieure ; les anciens baux du défaillant deviennent invalides.
4. **Réconciliation avant reprise** : un serveur qui redémarre ou sort d'isolement rattrape le journal **avant** de réattribuer la moindre tâche ; toute tâche dont l'époque a avancé est abandonnée localement.
5. **Double exécution contenue** : si le « mort » était partitionné, son travail non destructif peut se terminer en double (CPU perdu) ; ses opérations destructrices, différées par D4 tant qu'il est isolé, ne peuvent pas entrer en conflit avec la reprise. Sa publication concurrente est écartée en quarantaine à la réconciliation (époque avancée).
6. Automatisation bornée possible à terme (délai, règles de flow type `nodes-retry-on-worker-loss`) une fois la reprise manuelle éprouvée.

### D6 — Priorité immédiate : contention sous charge (avant toute extension)

Avant d'implémenter D3–D5, traiter le goulot observé avec ~7 000 tâches en file (claims lents, état et historique dégradés) :

1. Mesurer en conditions représentatives : latences p95/p99 des claims et routes d'état/historique, métriques `encoding_sqlite_writer_*` (attente, détention, refus, expirations), activité disque. La sonde existante (`docs/testing/coordination-measurements.md`, 128 travaux / 1 worker, claim p95 ≈ 354 ms) ne couvre pas cette charge.
2. Réduire le travail répété : sondage des workers à fréquence maîtrisée et désynchronisée (jitter), pagination stricte de l'historique, chargement des détails à la demande, état courant par deltas/curseur plutôt que rechargement de listes.
3. Ne pas compenser en augmentant les capacités/délais de la file d'écriture : cela transformerait des refus visibles en attentes longues.

## Invariants

1. Un fichier SQLite appartient à un seul serveur ; jamais partagé entre hôtes.
2. Le maillage LWW est le **seul** mécanisme de réplication ; toute table est déclarée « réconciliée » ou « locale ».
3. **Toute tâche a exactement un propriétaire connu à tout instant, et jamais deux propriétaires actifs** : un transfert distingue réception (inactive) et activation (journalisée, époque +1) ; la source gèle avant d'émettre l'activation.
4. Un serveur isolé continue son travail possédé et publie sans écrasement ; il ne mène **aucune opération de coordination** et **diffère toute opération destructrice** (remplacement de source, modification sur place) jusqu'au retour de la connectivité cluster.
5. Un serveur qui redémarre ou sort d'isolement réconcilie le journal **avant** de reprendre toute tâche ; une époque de propriété plus récente invalide ses anciens baux.
6. Un seul traitement modifie un média à la fois ; la source est préservée jusqu'à validation et publication ; les doublons détectés à la réconciliation sont résolus de façon déterministe, résultat perdant en quarantaine.
7. Les observations de présence relayées sont des indices horodatés avec provenance, jamais des preuves de fencing. **Un pair injoignable n'est pas un pair arrêté** : aucune réattribution automatique sans témoin ou majorité.
8. Routes fermées par défaut ; aucun secret dans journaux, erreurs, exports.

## Critères de validation

- Suite CI complète verte après retrait de Raft ; aucune référence restante à `PeerCluster`/`DotNext` hors historique Git.
- Tests à ajouter : plan d'attribution répliqué ; chaîne multi-serveurs (encode A → remux B) avec reprise à checkpoint ; transfert en deux phases — accusé perdu ⇒ aucun double actif, activation perdue ⇒ tâche inactive des deux côtés puis rattrapée par le journal ; source morte avant accusé ⇒ pas de transfert ; partition ⇒ les deux nœuds poursuivent leur file, publication autorisée, **étape destructrice différée** ; double création en partition ⇒ dédup déterministe, aucune source modifiée ; redémarrage ⇒ aucune reprise avant réconciliation complète ; reprise manuelle après grâce ⇒ ancien propriétaire revenu ne peut plus publier.
- Charge : rejouer le scénario 7 000 tâches sur 2 nœuds, mesurer claims/état/historique ; résultat documenté comme « vérifié » ou maintenu dans la liste « non vérifié » de `open-questions.md`.

## Non-objectifs

- Pas de consensus/quorum sur les données à deux nœuds ; à 3+ nœuds, majorité limitée à la décision de reprise, dans un module séparé.
- Pas de reprise **automatique** inter-serveurs à 2 nœuds sans témoin externe ; la reprise manuelle (grâce + époque + réconciliation + confirmation opérateur) est le chemin supporté.
- Pas de changement du protocole de maillage ni des contrats versionnés `Encoding.Contracts` hors attribution/transfert.
- Pas de reprise transparente à chaud d'un encodage interrompu au milieu d'un bloc non rejouable (les règles existantes de checkpoint s'appliquent).

## Ordre d'implémentation recommandé

1. D6 — contention sous charge (mesures, sondage maîtrisé, pagination).
2. D1 — retrait de Raft + ADR.
3. D4 (partie fencing) — différer les opérations destructrices en isolement : précondition de sécurité de tout le reste.
4. D3 — propriété journalisée : plan d'attribution, transfert en deux phases, puis chaînes multi-serveurs.
5. D4 (complément) — gossip d'état, dédup de création, indicateurs d'isolement dans l'UI.
6. D5 — reprise manuelle avec grâce et époques ; témoin ou majorité avant toute automatisation.