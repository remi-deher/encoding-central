---
id: 0043
title: SQL SQLite natif, sans couche de traduction
status: implemented
domain: storage
date: 2026-11-03
decided_by: Rémi (2026-11-03)
supersedes: []
superseded_by: []
tags: [sqlite, sql, migrations, ecrivain-unique]
---

# 0043 — SQL SQLite natif, sans couche de traduction

## Contexte
SQLite est le seul moteur ([storage](../domains/storage.md)), mais les dépôts écrivaient encore du SQL au dialecte PostgreSQL (`FOR UPDATE SKIP LOCKED`, `clock_timestamp()`, `interval`, `ANY(@p)`, `::text`, `jsonb_*`, `LATERAL`…) que `SqliteTranslator` réécrivait par expressions régulières à chaque exécution. Une construction non prévue échouait à l'exécution, le SQL lu n'était pas le SQL exécuté, et chaque nouvelle requête demandait d'étendre le traducteur.

## Décision
- Tout le SQL du backend (dépôts, `Sql.Command`, migrations) est du **SQLite natif**, exécuté tel quel : `SqliteTranslator` est supprimé.
- **Instants** : texte UTC de largeur fixe, `Sql.UtcNow` ou l'expression littérale `(strftime('%Y-%m-%d %H:%M:%f', 'now'[, modificateur]) || '000Z')` ; un décalage est un modificateur SQLite (`'+180 seconds'`, `'-' || (@x) || ' minutes'`, ou un paramètre `TimeSpan` converti en `'+N seconds'`). Un retrait d'une durée passée en paramètre se fait en passant la durée négative.
- **Tableaux** : un paramètre tableau est sérialisé en JSON et lu par `x IN (SELECT value FROM json_each(@p))` ; une colonne tableau (JSON) par `EXISTS(SELECT 1 FROM json_each(col) WHERE value = @v)` ; un tableau littéral par `json_array(…)`.
- **Autres équivalents** : plus de casts `::`, `MIN(a, b)`/`MAX(a, b)` pour `LEAST`/`GREATEST`, `json_array_length`, `UPDATE t AS a SET`, `lower(hex(randomblob(…)))` pour un UUID, `'9999-12-31 23:59:59.999999Z'` pour l'infini.
- **Sérialisation** : aucun `FOR UPDATE` ni `SKIP LOCKED`. SQLite n'a pas de verrou de ligne ; l'exclusion repose sur l'**écrivain unique** (`SqliteWriter`, ADR [0009](0009-ecrivain-sqlite-unique-par-noeud.md)) : toute transaction d'écriture est admise une à la fois puis ouverte en `BEGIN IMMEDIATE`, donc une séquence lecture puis écriture dans la même transaction ne peut pas être entrelacée par un autre écrivain du central. Une lecture cohérente sans écriture utilise `IsolationLevel.Snapshot`.
- **Migrations** : jamais modifiées. La baseline `0033` contient `DEFAULT (repeat('1', 672))`, fonction absente de SQLite (les bases existantes portent le défaut traduit) ; la migration additive `0075` reconstruit `worker_processing_settings` avec le défaut natif équivalent.
- **Vérification en CI** : `SqlStatementsPrepareTests` extrait chaque requête de `backend/src`, la compile avec SQLite (`EXPLAIN`, sans exécution) sur une base migrée, et refuse les restes de dialecte PostgreSQL.

## Alternatives écartées
- Garder le traducteur : SQL non lisible tel quel, erreurs à l'exécution, dette qui grandit avec chaque construction.
- Émuler `FOR UPDATE` : SQLite n'a pas de verrou de ligne ; l'écrivain unique donne déjà une exclusion plus forte, et une émulation masquerait cette garantie.
- Fonctions SQLite applicatives nommées comme celles de PostgreSQL (`repeat`, `clock_timestamp`) : elles maintiendraient une dépendance implicite que le SQL brut ne porte pas.

## Conséquences et risques
- Le SQL est lisible et exécutable tel quel dans `sqlite3`. Une requête qui ne compile pas échoue à la CI, pas en production.
- Toute nouvelle requête dynamique (fragment construit à l'exécution) doit être déclarée dans `DynamicFragments` du test, sinon le test échoue.
- Comportement inchangé par construction (mêmes réécritures que le traducteur) ; non revalidé sous charge. Les lignes verrouillées en lecture (`FOR UPDATE`) restent des lectures simples : leur rôle d'exclusion est tenu par la transaction d'écriture englobante.
