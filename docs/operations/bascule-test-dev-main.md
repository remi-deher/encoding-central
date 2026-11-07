# Bascule vers le cycle test → dev → main

Spécification d'exécution de l'[ADR 0047](../architecture/adr/0047-cycle-test-dev-main-et-images-par-branche.md), destinée à DevOps. État de départ relevé le 2026-11-07 sur `dev` (`276aad34`) : branche par défaut `dev`, aucune protection sur `dev` ni `main`, fusion squash/merge/rebase autorisées, auto-merge du dépôt désactivé, `qa-auto-merge.yml` déjà retiré (#36).

## 1. Règles cibles

| Base | Sources admises | Contrôles requis | Méthode de fusion | Publication |
| --- | --- | --- | --- | --- |
| `test` | toute branche du dépôt sauf `main` | `policy` | squash ou merge | `test`, `test-<sha7>` |
| `dev` | `test` uniquement | `policy`, `dev-ci` (inclut « dev ⊆ test »), `frontend` si concerné | merge uniquement | `dev`, `dev-<sha7>` |
| `main` | `dev` uniquement | `policy`, `promotion-ci` | merge uniquement | `latest`, `<version>` (`release.yml`) |

## 2. Fichiers à changer

| Fichier | Changement attendu |
| --- | --- |
| `.github/workflows/pr-policy.yml` | Étape « Validate » : accepter `base=test` si `head≠main` et dépôt source = ce dépôt ; `base=dev` si `head=test` ; `base=main` si `head=dev` ; sinon commentaire et fermeture (message à mettre à jour). Étape d'auto-merge `dev → main` inchangée. |
| `.github/scripts/pr-policy.test.mjs` | Le test n'extrait que le dernier bloc `run` (auto-merge) : ajouter un test de la matrice de l'étape « Validate » (`feature→test` OK, `dev→test` OK, `main→test` fermé, `test→dev` OK, `feature→dev` fermé, `dev→main` OK, `test→main` fermé, base inconnue fermée). |
| `.github/workflows/backend-dev.yml` | Déclencheurs inchangés (`push`/`pull_request` sur `dev`). Ajouter un job `dev-ancestry`, sur `pull_request` seulement : exige `head.ref = test` et même dépôt, extrait `github.event.pull_request.head.sha` avec `fetch-depth: 0` (comme `promotion.yml` ; jamais le commit de fusion de la PR, qui contient déjà `dev`), puis `git fetch origin dev` et échoue si `git rev-list --no-merges origin/dev ^HEAD` n'est pas vide (« dev porte des commits absents de test : les reporter dans test par PR »). L'ajouter aux `needs` et à la conclusion de `dev-ci` (succès requis sur PR, ignoré sur push). |
| `.github/workflows/frontend.yml` | Inchangé (`pull_request` sur `dev`, `main`) : ne pas ajouter `test`. |
| `.github/workflows/backend.yml`, `image.yml`, `release-scripts.yml`, `promotion.yml` | Inchangés (PR vers `main`). `promotion.yml` garde « main ne porte aucun changement absent de dev ». |
| `.github/workflows/stress.yml` | Inchangé (`workflow_dispatch`). |
| `.github/workflows/release.yml` | Inchangé, sauf le job `cleanup-branches` qui suit le script ci-dessous. |
| `.github/workflows/image-test.yml` (PR #38) | Déclencheur `push: branches: [test]` au lieu de `dev`. Remplacer par un seul workflow `image-branch.yml` sur `push: [test, dev]` est accepté (étiquette dérivée de la branche, cache GHA par branche). Exigences : `APP_VERSION=<branche>-<sha7>` identique pour les deux images ; pousser d'abord `<branche>-<sha7>` des **deux** images, puis seulement déplacer `<branche>` (par `docker buildx imagetools create`) ; `concurrency` par branche **sans** annulation, pour que chaque commit ait son image ; aucun test ; échec explicite si un secret manque. |
| `.github/workflows/image-dev.yml` (nouveau, sauf si `image-branch.yml`) | Même contenu sur `push: [dev]`, étiquettes `dev` et `dev-<sha7>`. |
| `.github/scripts/cleanup-released-branches.mjs` | Exclure `test` (`['main','dev','test']`) ; lister les PR fusionnées de **base `test`** (`pulls?state=closed&base=test`) et exiger `pr.base.ref === 'test'` ; `included(sha)` = ancêtre du commit publié (`GITHUB_SHA`), ancêtre de `dev` conservé. |
| `.github/scripts/cleanup-released-branches.test.mjs` | Adapter les cas : base `test`, `test` jamais supprimée, PR vers `dev` (promotions) jamais source de suppression. |
| `AGENTS.md` (« Flux des PR », phrase sur `dev-ci`) | Toute PR de travail cible `test` ; `dev` ne reçoit que `test` ; `main` que `dev` ; fusion par commit de fusion pour les promotions ; correction en avant sur `test`. |
| `TEST_RULES.md` (l. 67 et section CI) | `backend-dev.yml` tourne sur PR `test → dev` et push `dev` ; `test` n'a aucune CI hors images. |
| `docs/operations/release.md` | Remplacer « les changements sont poussés sur `dev` » par le cycle à trois branches ; ajouter les étiquettes `test*` / `dev*` ; retirer le texte de la PR #38 qui cible `dev`. |
| `docs/operations/ci-scopes.md` (§ « Promotion et publication ») | Squash admis seulement vers `test` ; promotions `test → dev` et `dev → main` en merge. |
| `docs/operations/release-branch-cleanup.md` | Branche supprimable si sa PR vers `test` est fusionnée et incluse dans le commit publié ; `test` jamais supprimée. |
| Réglages GitHub | Branche par défaut `test`. Rulesets : `test`, `dev`, `main` sans suppression ni force-push ; PR obligatoire ; méthodes autorisées : `test` squash+merge, `dev` et `main` merge seul ; contrôles requis du tableau 1. |

## 3. Transition, dans l'ordre

1. **Gel** : prévenir les agents (brief en 6) ; aucune fusion vers `dev` pendant les étapes 2 à 5.
2. **PR ouvertes** : #36 déjà fusionnée. #37 (T1, sous revue) : la fusionner vers `dev` avant l'étape 3 si elle est prête, sinon la recibler vers `test` après l'étape 5 (`gh pr edit 37 --base test`). #38 : ne pas fusionner en l'état (il publierait sur push `dev`) ; la reprendre dans la PR de bascule. #20 (brouillon) : ne pas y toucher ; **attention**, après la bascule, tout push ou modification sur #20 relance `pr-policy` qui la fermera (source ≠ `test` vers `dev`) : son auteur doit d'abord la recibler vers `test`.
3. **PR de bascule** vers `dev` (encore acceptée par l'ancienne règle) : tous les fichiers du tableau 2, fusion par commit de fusion. `pr-policy` (déclenché en `pull_request_target`) valide cette PR avec l'ancienne version : pas de blocage.
4. **Créer `test`** depuis `dev` juste après cette fusion : `git push origin origin/dev:refs/heads/test`. `test` = `dev`, donc le contrôle « dev ⊆ test » est vrai. Le push déclenche la première image `test` : vérifier les deux étiquettes `test-<sha7>` et `test` avant de poursuivre.
5. **Réglages** : branche par défaut `test`, puis rulesets.
6. **Annonce** (brief type) : « Le cycle de branches change (ADR 0047). Toute PR de travail cible désormais `test` (`gh pr create --base test`) ; une PR vers `dev` ou `main` depuis une autre branche est fermée automatiquement. `dev` ne reçoit que la promotion `test → dev`, `main` que `dev → main`, toujours en commit de fusion. `test` n'a pas de CI : chaque fusion publie `mrcryllix/encoding-central` et `encoding-worker` en `test-<sha7>`. On corrige en avant par une nouvelle PR vers `test`, jamais de force-push. Vos branches en cours : fusionnez `origin/test` dans votre branche puis ouvrez ou reciblez la PR vers `test`. »
7. **Branches de travail existantes** : `test` étant égale à `dev` au départ, une simple fusion de `origin/test` suffit ; une PR ouverte vers `dev` est reciblée (`--base test`), ce qui relance la policy avec la nouvelle règle.
8. **Première promotion `test → dev`** dès qu'une PR a été fusionnée sur `test`, pour éprouver `dev-ancestry`.

## 4. Correction et urgence

- Correction en avant sur `test` : nouvelle PR, éventuellement un `git revert` passé par PR. Jamais de reset, force-push, cherry-pick direct sur `dev` ou `main`.
- Correctif urgent : PR vers `test`, fusion, image `test-<sha7>` déployée par prod-tester ; la promotion vers `dev` puis `main` suit le rythme normal. Si un commit absent de `test` apparaît sur `dev` ou `main` (erreur), `dev-ancestry` ou `promotion-ci` bloque : le reporter par PR vers `test`.

## 5. Angles morts

| Sujet | Effet | Réponse proposée |
| --- | --- | --- |
| Promotion `test → dev` en lot | Un commit qui casse `dev-ci` bloque tout le lot | Promotions fréquentes ; correction en avant ; option non bloquante (à décider) : `dev-ci` en avertissement sur les PR vers `test` |
| Production sur `test` | Code non testé en production | Déployer `test-<sha7>` immuable ; retour à l'étiquette précédente en cas de défaut |
| Images partielles | `central` publiée, `worker` en échec : versions différentes (ADR 0039) | Étiquette flottante déplacée seulement après les deux images |
| Échec de construction `test` | prod-tester n'a rien de nouveau | Statut d'échec visible sur le commit ; correction en avant |
| Coût | 2 images par push `test` et `dev` | Dépôt public (minutes gratuites) ; cache GHA par branche ; durée à mesurer |
| Rétention Docker Hub | `*-<sha7>` s'accumulent | Job de nettoyage (garder les 30 dernières de chaque branche) ; politique Docker Hub à vérifier |
| `main` non protégée | `promotion-ci` n'est pas réellement exigé aujourd'hui | Ruleset de l'étape 5 |
| Auto-merge du dépôt désactivé | L'étape d'auto-merge de `pr-policy` (`gh pr merge --auto`) échoue | Activer « Allow auto-merge » ou retirer l'étape (à décider) |
