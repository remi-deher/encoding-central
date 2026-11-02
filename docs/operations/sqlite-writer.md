# Écrivain SQLite local

L’[ADR 0009](../architecture/adr/0009-ecrivain-sqlite-unique-par-noeud.md) est appliqué dans l’adaptateur SQLite partagé. Une instance serveur utilise un `SqliteDataSource` singleton et sa file locale ; aucun service externe, changement de schéma ou changement du protocole de maillage n’est nécessaire.

## Configuration

| Clé sous `Database:Writer` | Défaut | Sens |
| --- | --- | --- |
| `Enabled` | `true` | `false` mesure les unités sans admission exclusive ; `true` sérialise les écritures. |
| `CriticalOnly` | `false` | Phase intermédiaire : seuls présence et baux passent par la file ; les autres unités sont mesurées. |
| `Capacity` | `256` | Nombre maximal d’unités ordinaires en attente. |
| `UrgentCapacity` | `32` | Places réservées aux heartbeats et renouvellements de baux. |
| `WaitTimeout` | `00:00:05` | Attente maximale avant admission. |
| `LongTransaction` | `00:00:01` | Seuil de détention longue, comptée et signalée par Trace à la libération. |
| `BeginRetries` | `2` | Nouvelles tentatives de BEGIN sur SQLite BUSY/LOCKED ; plage 0–10. |

Les capacités et délais doivent être strictement positifs. Une configuration invalide bloque le démarrage. Dans Docker, utiliser par exemple `Database__Writer__WaitTimeout=00:00:05`.

`CriticalOnly=true` est une phase de migration : les écritures ordinaires peuvent encore concurrencer les urgentes dans SQLite. Seul `Enabled=true` avec `CriticalOnly=false` établit l’écrivain unique pour toutes les écritures applicatives.

La file accorde les admissions FIFO par priorité, avec au plus huit urgentes consécutives quand une unité ordinaire attend. Une unité correspond à une transaction explicite ou une commande hors transaction ; aucun regroupement d’unités indépendantes. La file ne termine pas une transaction engagée à l’expiration du délai d’attente.

## Déploiement progressif

1. Sur un environnement de test, démarrer avec `Enabled=false` et relever les métriques `encoding_sqlite_writer_*`, les temps HTTP et les erreurs de verrouillage sous une charge reproductible. Les compteurs sont locaux au processus et repartent de zéro au redémarrage. Ce mode conserve les délais SQLite historiques ; les mesures de détention incluent l’attente du verrou SQLite.
2. Activer `Enabled=true`, d’abord avec `CriticalOnly=true` pour les chemins critiques, puis `CriticalOnly=false` pour tous les domaines, sur le même jeu synthétique, avec les mêmes opérations, en vérifiant admissions, saturation, annulation, priorité des renouvellements et absence de pertes de mises à jour. Le délai de verrouillage SQLite est alors de 1 s par tentative ; BEGIN retente avant toute instruction métier avec 25 puis 50 ms d’attente par défaut. Les commandes déjà engagées ne sont jamais rejouées automatiquement.
3. Vérifier les suites runtime et de réconciliation, puis les scénarios Docker multi-nœuds et versions mixtes de la CI. Le format des échanges reste inchangé. Ne pas interpréter une compilation ou un test local comme une preuve de convergence en production.
4. Après accord de Rémi pour la validation en production, mesurer les mêmes indicateurs sur un nœud, puis étendre aux autres. Ajuster capacités et délais à partir des observations. Le retour à `Enabled=false` demande seulement un redémarrage avec ce paramètre ; aucune migration inverse.

Une file pleine ou expirée, ainsi qu’une erreur SQLite BUSY/LOCKED remontant à l’API, produit HTTP **503**, `Retry-After: 1` et le code `sqlite_write_unavailable`. Un client doit respecter le délai et les règles d’idempotence de son opération : une erreur de commande multi-instruction hors transaction peut suivre un effet partiel. Aucune répétition automatique d’un bloc métier n’est introduite.

## Observabilité et limites

`/metrics` expose les files ordinaires/urgentes, l’admission active, les compteurs d’admissions/refus/expirations/reprises de BEGIN/détentions longues et les temps cumulés d’attente et de détention. Le temps de détention comprend le temps passé à acquérir le verrou SQLite après admission. Ces totaux donnent des moyennes par intervalle, pas des percentiles. Les avertissements passent par `System.Diagnostics.Trace` ; leur destination dépend des listeners du processus.

Les lectures `Snapshot` sont différées et protégées par `query_only`, sans passage par la file. Les SELECT simples hors transaction passent directement ; les CTE, commentaires initiaux et lots sont admis conservativement, sans transaction implicite supplémentaire. Les écritures avec lecteur de résultat gardent leur admission jusqu’à fermeture du lecteur ou de sa connexion. Annuler une attente la retire ; annuler le jeton d’un appelant déjà admis ne libère pas sa place à son insu. L’appelant reste responsable de clôturer sa transaction.

Exceptions d’infrastructure autorisées : initialisation des PRAGMA de connexion, activation/réinitialisation de `query_only`. Les migrations, copies et sauvegardes via l’adaptateur suivent ses transactions. Une autre instance de `SqliteDataSource` possède sa propre file : ne pas en créer une seconde pour la base du serveur. Aucun fichier SQLite ne doit être partagé entre nœuds.

Non vérifié par la seule implémentation : charge réelle, comparaison avant/après en production, scénarios Docker multi-hôtes, compatibilité entre images de versions différentes. Aucun média réel n’est nécessaire aux tests de concurrence.

## Vérification locale du 2026-10-23

La [sonde de coordination](../testing/coordination-measurements.md) complète le compteur synthétique ci-dessous par des mesures des claims et renouvellements réels avec et sans capture incrémentale concurrente (p95/p99, temps de file, admissions et erreurs). Elle est reproductible sur des fichiers temporaires et ne constitue pas une validation de production.

Une sonde sur deux fichiers SQLite temporaires, même charge pour chaque mode : huit clients concurrents, seize transactions chacun, lecture du compteur puis mise à jour dans la même transaction (128 unités). Un seul passage par mode, sur cet environnement de développement :

| Mode | Durée totale | Compteur final | Attente cumulée de file | Détention cumulée après admission | Refus / expirations / reprises BEGIN |
| --- | --- | --- | --- | --- | --- |
| Mesure (`Enabled=false`) | 274,8 ms | 128 | 0 ms | 714,8 ms | 0 / 0 / 0 |
| File complète (`Enabled=true`, `CriticalOnly=false`) | 249,0 ms | 128 | 11,6 ms | 16,7 ms | 0 / 0 / 0 |

La détention cumulée peut dépasser la durée totale en mode mesure : plusieurs appels attendent simultanément le verrou SQLite. L’initialisation de la table est incluse dans les compteurs d’admission/détention, mais pas dans la durée de la charge. Les temps dépendent de la machine et de l’ordonnancement ; aucune conclusion de performance en production n’est tirée de ce passage. Les tests `SqliteWriterTests` couvrent séparément saturation, expiration, annulation, priorité bornée, lecture Snapshot, rollback, fermeture de lecteur/connexion, reprise de BEGIN, absence de pertes concurrentes et réponses HTTP 503.
