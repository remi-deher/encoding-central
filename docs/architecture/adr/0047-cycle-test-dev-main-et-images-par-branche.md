---
id: 0047
title: Cycle de branches test → dev → main et images Docker par branche
status: accepted
domain: auth-ops
date: 2026-11-07
decided_by: Rémi (2026-11-07)
supersedes: []
superseded_by: []
tags: [ci, branches, docker, publication, promotion]
---

# 0047 — Cycle de branches test → dev → main et images Docker par branche

## Contexte
Aujourd'hui toute PR cible `dev` (CI légère `dev-ci`), puis une PR de promotion `dev` → `main` exécute la CI complète (`promotion-ci`) et `release.yml` publie `latest` et `<version>` sur Docker Hub. Aucune image n'existe avant `main` : l'agent prod-tester ne peut pas déployer un état intermédiaire. Ni `dev` ni `main` ne sont protégées par une règle GitHub (vérifié le 2026-11-07) ; la branche par défaut est `dev`.

## Décision
1. **Trois branches, un seul sens.** `test` reçoit par PR le code de toute branche sauf `main` ; `dev` ne reçoit que des PR depuis `test` ; `main` ne reçoit que des PR depuis `dev`. Toute autre PR est fermée par `pr-policy`. Invariant d'ascendance : `main` ⊆ `dev` ⊆ `test` (hors commits de fusion de promotion).
2. **CI par branche.** `test` : aucune CI hormis la construction et la publication des images. `dev` : lint/format backend (`backend-dev.yml`) et frontend (`frontend.yml`) seulement, plus un contrôle « `dev` ne porte aucun changement absent de `test` ». Le contrôle requis `dev-ci` garde son nom et conclut toujours ; il agrège les vérifications backend et l'ascendance. Aucun build ni test n'est exécuté sur `dev`. `main` : inchangé (`policy`, `promotion-ci`, CI complète, `release.yml`).
3. **Images par branche** (`mrcryllix/encoding-central` et `mrcryllix/encoding-worker`, secrets `DOCKERHUB_USERNAME` / `DOCKERHUB_TOKEN`) : `test` et `test-<sha7>` à chaque push sur `test` ; `dev` et `dev-<sha7>` à chaque push sur `dev` ; `latest` et `<version>` par `release.yml`, inchangé. Pour un commit, les deux images portent la même `APP_VERSION` (`test-<sha7>`, `dev-<sha7>`), condition de l'[ADR 0039](0039-version-exacte-worker-central.md). L'étiquette flottante (`test`, `dev`) n'est déplacée qu'une fois **les deux** images du commit publiées sous leur étiquette immuable. Ce déplacement n'est pas atomique (une commande par image) : seule l'étiquette immuable sert au déploiement.
4. **Méthode de fusion.** PR de travail → `test` : squash ou merge. `test` → `dev` et `dev` → `main` : **commit de fusion uniquement** (jamais squash ni rebase), pour garder l'ascendance du point 1.
5. **Correction en avant.** Un défaut se corrige par une nouvelle PR vers `test` (un `git revert` passé par PR compte comme correction en avant). Jamais de réécriture, de force-push ni de suppression de `test`, `dev`, `main`. Un correctif urgent suit le même chemin : PR vers `test`, image `test-<sha7>` déployée par prod-tester, puis promotions normales.
6. **Branche par défaut GitHub : `test`.**

La spécification fichier par fichier et la procédure de transition sont dans [bascule-test-dev-main.md](../../operations/bascule-test-dev-main.md).

## Alternatives écartées
- **Garder `dev` comme entrée et ajouter une image `dev`** : la production testée par prod-tester recevrait le même code que la CI légère, sans branche d'essai brute ; refusé par Rémi.
- **Publier une image par PR** : coût de construction et d'étiquettes multiplié, sans état de référence unique pour prod-tester.
- **Squash à chaque promotion** : `test`, `dev` et `main` divergeraient, et les contrôles d'ascendance comme le nettoyage des branches deviendraient impossibles.
- **CI légère aussi sur `test`** : écartée par décision ; reste une option non bloquante (voir risques).

## Conséquences et risques
- **La production (prod-tester) tourne sur des images non testées.** Atténuation : prod-tester déploie une étiquette immuable `test-<sha7>`, jamais `test` flottante, et revient à la précédente `test-<sha7>` en cas de défaut (retour d'image, pas de retour de code).
- **Une PR `test` → `dev` embarque tout l'accumulé** ; un seul commit qui casse `dev-ci` bloque la promotion jusqu'à sa correction sur `test`. Atténuation : promouvoir souvent (petits lots), corriger en avant.
- **Image `test` non construite** : l'étiquette `test` ne bouge pas, prod-tester reste sur la dernière `test-<sha7>` valide ; l'échec doit être visible (statut du workflow sur le commit).
- **Coût** : deux constructions d'image par push sur `test` et sur `dev`, en plus de la CI existante (dépôt public : minutes Actions gratuites ; durée réelle à mesurer).
- **Rétention Docker Hub** : les étiquettes `test-<sha7>` et `dev-<sha7>` s'accumulent ; un nettoyage gardant les N dernières est à prévoir (politique Docker Hub non vérifiée).
- Documents à aligner lors de la bascule : `AGENTS.md` (« Flux des PR »), `TEST_RULES.md`, `docs/operations/release.md`, `ci-scopes.md`, `release-branch-cleanup.md`.
