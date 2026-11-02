# Maquette interactive de l’assistant IA

Maquette Angular isolée du produit, compilée aussi en fichier HTML autonome. Les échanges et le catalogue affiché sont des exemples : aucune API ni inférence réelle n’est appelée, et aucune route de production n’est modifiée.

Ouvrir directement [`maquette-ia.html`](./maquette-ia.html) dans un navigateur. Le fichier inclut les styles et le JavaScript compilés ; il ne dépend pas d’un CDN.

La maquette de mise en page de l’assistant est disponible dans [`assistant-layout-proposal.html`](./assistant-layout-proposal.html). Elle permet de replier et déplier l’historique sur ordinateur et mobile, de parcourir les conversations et de démarrer une nouvelle conversation depuis le bouton intégré à la barre de recherche globale. Aucun champ de recherche n’est placé dans l’historique.

## Sélecteur IA

- Un premier menu ne liste que les fournisseurs activés qui exposent des modèles : ChatGPT, Claude et OpenAI API dans cet exemple.
- Un second menu affiche explicitement « Modèles disponibles pour [fournisseur sélectionné] » ; changer de fournisseur actualise cette liste et sélectionne un modèle disponible.
- Sous les modèles, le curseur d’effort reprend les valeurs `reasoningEfforts` du modèle sélectionné ; le modèle sans effort expose un curseur désactivé.
- Les interactions du fichier HTML utilisent un catalogue de démonstration. L’intégration produit doit alimenter le même menu avec `GET /api/v1/tools/ai/assistant/options` : `models` fournit le fournisseur, le modèle, `reasoningEfforts`, `defaultReasoningEffort` et `reasoningKnown` déjà filtrés selon les fournisseurs et modèles activés.

Les réglages de modèles dans l’onglet Fournisseurs, les conversations et les actions de la maquette restent simulés.

## Autres parcours disponibles

- Envoyer un message ou démarrer depuis une suggestion ; les réponses sont simulées.
- Ouvrir le menu « + » pour joindre une ressource, simuler un import ou accéder aux outils Encoding.
- Parcourir l’historique, les onglets Fournisseurs et Activité.

La présentation reprend les menus flottants, la barre de saisie arrondie et la palette sombre de la référence Gemini, avec des actions adaptées aux flows, au cluster, aux exécutions et aux ressources média.

## Régénérer le HTML autonome

Depuis `frontend/` :

```sh
npm exec ng build -- --configuration development --browser mockups/ai/main.ts --ts-config mockups/ai/tsconfig.json --index mockups/ai/index.html --output-path dist/ai-mockup --source-map=false --output-hashing=none
node mockups/ai/package.mjs
cp dist/ai-mockup/browser/maquette-ia.html mockups/ai/maquette-ia.html
```
