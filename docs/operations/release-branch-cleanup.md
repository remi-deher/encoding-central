# Nettoyage des branches après publication

Le job `cleanup-branches` de Release ne démarre qu'après réussite du job
`publish` (image publiée et tag Git poussé). Une publication ignorée ne déclenche
aucune suppression. Un échec du nettoyage reste visible sans annuler la release.

Une branche du dépôt est supprimable seulement si une PR vers `dev` est fusionnée,
si son SHA source correspond encore au dernier commit de la branche, et si le
`merge_commit_sha` de cette PR appartient à la fois à `dev` et au commit publié.
GitHub relie ainsi la branche à son résultat de fusion, y compris après squash
ou rebase de la PR. Si ce lien n'est plus démontrable (historique réécrit, par
exemple), la branche est conservée. Une promotion `dev` vers `main` par merge
commit conserve cette preuve ; une promotion squash peut conserver davantage de
branches par précaution.

`main`, `dev`, les branches protégées et les branches utilisées par une PR ouverte
sont conservées. Les PR de forks ne permettent pas de supprimer une branche locale
homonyme. Les PR et branches sont relues avant suppression. Le push de suppression
utilise un bail sur le SHA exact : un commit poussé entre la lecture et la suppression
fait échouer cette dernière. Ne pas pousser sur une branche déjà fusionnée pendant
le nettoyage ; créer une nouvelle branche pour le travail suivant.

Le script utilise `gh`, Git et Node.js 24, sans dépendances npm. Il pagine les listes
GitHub et échoue sur erreur API. Il journalise les branches conservées/supprimées.
Sans `--apply`, il simule uniquement :

```sh
GH_TOKEN=... GITHUB_REPOSITORY=owner/repo GITHUB_SHA=<sha-publie> node .github/scripts/cleanup-released-branches.mjs
node --test .github/scripts/cleanup-released-branches.test.mjs
```
