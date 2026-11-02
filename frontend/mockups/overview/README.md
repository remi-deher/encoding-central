# Vue d’ensemble — proposition

Maquette Angular/Material isolée, données fictives, aucune requête backend.

Depuis `frontend/` :

```sh
npx ng build --configuration development --browser mockups/overview/main.ts --ts-config mockups/overview/tsconfig.json --index mockups/overview/index.html --output-path dist/overview --source-map=false --output-hashing=none
node mockups/serve.mjs dist/overview/browser 4320
```

Recherche au centre du shell, nombre de jobs visible, métriques groupées, traitements au premier plan, nœuds et alertes à droite, publications récentes en bas. Le changement de période et les boutons sont simulés explicitement. Aucun bandeau de bonne santé. Les estimations et économies affichées illustrent le design ; une intégration devra conserver les valeurs indisponibles lorsqu’elles ne sont pas fournies par le backend.

Le fichier `maquette-vue-ensemble.html` s’ouvre directement dans un navigateur. Pour le régénérer après compilation : `node mockups/overview/package.mjs` puis copier le fichier produit depuis `dist/overview/browser/`. Les captures 1440/390 px sont disponibles dans les deux thèmes.
