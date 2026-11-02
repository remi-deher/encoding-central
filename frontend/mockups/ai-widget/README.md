# Assistant · maquette du panneau de réglages

Maquette isolée du composant de chat. Un petit bouton « Modèle » ouvre une carte de réglages flottante au-dessus du fil de discussion. La carte apparaît depuis le bouton avec une légère animation. Le fournisseur filtre les modèles disponibles et le curseur d’effort affiche les niveaux disponibles pour le modèle choisi. L’ajout d’une ressource reste disponible dans le menu secondaire de l’en-tête. Un bouton « + » près du modèle ouvre le sélecteur de fichiers; le fichier choisi apparaît sous forme de puce retirable.

Le fil de conversation et la zone de saisie restent visibles sous les réglages. La phrase d’aide « Posez votre question… » est retirée. La démonstration permet aussi de joindre une ressource, d’envoyer des messages et d’observer une réponse simulée. Elle ne contacte aucun fournisseur IA.

Le composant utilise Angular Material et les jetons de thème partagés. Aucun composant de production n’est modifié. Les fournisseurs, modèles et niveaux d’effort présentés sont des données de démonstration; l’intégration réelle conserve le catalogue fourni par le backend.

Depuis `frontend/` :

```sh
npm exec ng build -- --configuration development --browser mockups/ai-widget/main.ts --ts-config mockups/ai-widget/tsconfig.json --index mockups/ai-widget/index.html --output-path dist/ai-widget --source-map=false --output-hashing=none
node mockups/serve.mjs dist/ai-widget/browser 4319
```

Ouvrir http://127.0.0.1:4319. Pour produire un fichier autonome : `node mockups/ai-widget/package.mjs`. Résultat : `dist/ai-widget/browser/maquette-widget-ia-v2.html`.
