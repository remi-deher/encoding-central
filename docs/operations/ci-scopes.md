# Périmètres CI

Les workflows utilisent les mêmes filtres sur les PR et les push vers dev/main :

| Workflow | Fichiers concernés |
| --- | --- |
| Backend | backend et son workflow |
| Frontend | frontend et son workflow |
| Image | backend, frontend, .dockerignore et son workflow |
| Release scripts | scripts GitHub et workflows release*.yml |
| Promotion CI | toutes les PR vers main, sans filtre de chemins |

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

Validation locale et CI : `node --test .github/scripts/*.test.mjs`.

Ne pas rendre obligatoires dans les protections de branche les workflows qui peuvent
être absents à cause des filtres : GitHub les laisserait en attente. Si une protection
globale est ajoutée, utiliser un contrôle agrégateur qui distingue les suites non
concernées des suites en échec.

## Promotion et publication

Les PR de fonctionnalité vers dev sont fusionnées en squash ; la PR de promotion dev vers main
reste en fusion « Merge » pour que main demeure contenue dans dev.

`promotion-ci` contrôle le SHA exact de dev et les derniers résultats des domaines
inchangés. Il est exécuté pour chaque PR vers main, sans filtre de chemins, et doit
être requis dans une règle de protection de main. Cette règle ne doit pas offrir de
dérogation aux intégrations qui activent l'auto-merge. La politique de PR désactive
l'auto-merge existant et ne l'active de nouveau qu'après réussite de promotion-ci,
avec vérification du SHA de tête. Il vérifie aussi que main ne porte aucun commit hors fusion absent de dev (les fusions de
promotion précédentes sont normales) ; sinon il échoue en demandant de fusionner main dans dev. Un nouveau push invalide le contrôle précédent.
Un échec impose une correction puis un nouveau contrôle, ou une relance si le test
était transitoirement en panne.

La release réutilise la même vérification (`wait-ci.mjs`). Un échec CI, une erreur
API, une source manquante ou un délai dépassé font échouer le job avec code non nul.
Les contrôles en échec apparaissent dans les annotations et le résumé du job avec
leur lien. Un push sur main sans PR dev vers main fusionnée (commit direct) ne publie rien : le job
échoue avec une annotation et un résumé explicites. Un lancement manuel ne contourne pas la CI. Un commit déjà publié reste
un cas normal sans nouvelle publication.
