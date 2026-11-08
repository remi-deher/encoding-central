# Périmètres CI

Les déclencheurs des workflows suivent le cycle test→dev→main (ADR 0047) et limitent les suites aux changements concernés :

| Workflow | Fichiers concernés |
| --- | --- |
| Backend | backend et son workflow |
| Backend dev | PR et push vers dev ; lint/format C# et contrôle de politique locale si backend ou son workflow changent ; agrégateur `dev-ci` toujours présent |
| Frontend | PR vers dev/main et push dev ; lint/format sur dev, suite complète sur main |
| Image | backend, frontend, .dockerignore et son workflow |
| Release scripts | scripts GitHub et workflows release*.yml |
| Promotion CI | toutes les PR vers main, sans filtre de chemins |

Backend dev est un workflow à part : `backend.yml` ne s'exécute pas sur dev, car ses jobs ignorés
produiraient des contrôles « skipped » que `wait-ci.mjs` lirait comme des échecs. Le contrôle
`backend-dev-lint` n'est pas dans `ci-scopes.mjs` : la promotion et la release l'ignorent.
Le workflow n'a pas de filtre de chemins : un job `changes` détecte les fichiers backend, et le
contrôle agrégateur `dev-ci` conclut toujours (succès si `backend-dev-lint` a réussi ou a été ignoré
faute de changement backend, échec sinon). Le ruleset `dev` exige `policy` et `dev-ci` ; ne pas
requérir `backend-dev-lint`, absent des PR hors backend. Le lint frontend est indépendant et n'est
pas requis. Le ruleset `main` conserve uniquement `promotion-ci`.
Tous les workflows déclarent `permissions: contents: read` au minimum ; seuls policy, promotion et release élargissent.

Une documentation ordinaire ne déclenche aucune compilation. Les changements des
workflows restent testés par le workflow qu'ils modifient. La construction de
l'image reste nécessaire pour un changement frontend : le Dockerfile embarque
l'interface. Le contrôle de politique de PR reste indépendant des fichiers.

La release exige toujours les contrôles de chaque domaine. Si un contrôle est absent
du commit publié parce que son domaine n'a pas changé, elle consulte le dernier
commit de son périmètre (y compris son workflow), et uniquement celui-ci. Les
contrôles présents sur le commit publié restent prioritaires. Seule une conclusion
`success` est acceptée ; contrôle annulé, ignoré ou neutre n'autorise pas la promotion.
En cas de relance, la dernière tentative du contrôle prime. Les filtres et le
résolveur de .github/scripts/ci-scopes.mjs sont synchronisés par des tests.

Les tests structurels des workflows restent réservés à GitHub Actions.

Ne pas rendre obligatoires dans les protections de branche les workflows qui peuvent
être absents à cause des filtres : GitHub les laisserait en attente. Si une protection
globale est ajoutée, utiliser un contrôle agrégateur qui distingue les suites non
concernées des suites en échec.

## Promotion et publication

**Méthode de fusion** :
- Les PR de travail vers `test` : **squash ou merge** autorisés
- Les promotions `test` → `dev` et `dev` → `main` : **merge uniquement** (jamais squash ni rebase)

`promotion-ci` contrôle le SHA exact de `dev` et les derniers résultats des domaines
inchangés. Il est exécuté pour chaque PR vers `main`, sans filtre de chemins, et doit
être requis dans une règle de protection de `main`. Chaque fusion est décidée
explicitement après une relecture indépendante favorable sur le SHA exact et des
contrôles verts. L'auto-merge du dépôt reste désactivé ; `pr-policy` valide les
branches et ferme les PR interdites, sans demander de fusion.

`pr-policy` utilise `pull_request_target`, donc la version de la branche de base.
Le retrait de son ancienne étape d'auto-merge ne s'applique aux promotions vers
`main` qu'une fois le correctif arrivé sur `main`. D'ici là, l'ancienne étape y
demande encore la fusion dès que `promotion-ci` réussit.

`dev-ancestry` (dans `backend-dev.yml`) vérifie que `dev` ne porte aucun commit absent de `test`.
Ce job est exécuté uniquement sur les PR `test` → `dev` et doit réussir ou être sauté.

La release réutilise la même vérification (`wait-ci.mjs`). Un échec CI, une erreur
API, une source manquante ou un délai dépassé font échouer le job avec code non nul.
Les contrôles en échec apparaissent dans les annotations et le résumé du job avec
leur lien. Un push sur `main` sans PR `dev` → `main` fusionnée (commit direct) ne publie rien : le job
échoue avec une annotation et un résumé explicites. Un lancement manuel ne contourne pas la CI. Un commit déjà publié reste
un cas normal sans nouvelle publication.
