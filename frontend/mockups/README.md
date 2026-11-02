# Maquette Nœuds et Cluster

Maquette Angular 22 isolée, sans route ajoutée à l'application ni requête vers une API. Tous les nœuds, adresses, codes et résultats sont fictifs. Aucun changement n'est effectué sur une installation réelle.

Depuis `frontend/` :

```powershell
npm ci
npm exec ng build -- --configuration development --browser mockups/node-cluster/main.ts --ts-config mockups/node-cluster/tsconfig.json --index mockups/node-cluster/index.html --output-path dist/node-cluster --source-map=false --output-hashing=none
node mockups/serve.mjs dist/node-cluster/browser 4317
```

Ouvrir `http://127.0.0.1:4317`. Le HTML compilé est `dist/node-cluster/browser/index.html` ; conserver les fichiers JavaScript et CSS voisins et les servir par HTTP.

Pour produire également un fichier HTML autonome, incluant le JavaScript et le thème compilés :

```powershell
node mockups/package-html.mjs dist/node-cluster/browser
```

Le résultat `maquette-noeuds-cluster.html` contient les vrais composants Angular/Material/CDK compilés et ne dépend d'aucun CDN.

## Parcours

- **Ce nœud** : fiche locale et fonctions serveur/worker, sans tableau pour le nœud autonome. La modification réutilise la fenêtre commune.
- **Cluster, autonome** : créer un cluster ou rejoindre un cluster existant.
- **Création** : nom, récapitulatif modifiable, création fictive ; le nœud local reste le premier membre.
- **Rattachement** : adresse, vérification d'identité et conséquences, demande, attente d'approbation, résultat. Fermer l'attente avec **Fermer et suivre plus tard**, puis utiliser **Simuler l'approbation** dans la barre de maquette pour afficher le résultat sans prétendre contacter Maison.
- **Scénarios → Administrateur de Maison** : liste des membres, demande en attente, confirmation commune avec comparaison du code.
- **Scénarios → Simuler un échec de connexion** : la prochaine vérification échoue, conserve l'adresse et permet un nouvel essai.
- La maquette conserve une interface après approbation pour explorer le résultat. Dans le parcours métier actuel, la fonction serveur locale s'arrête après le rattachement ; ce changement est indiqué avant l'envoi de la demande.

## Composants utilisés

Le gabarit de page est `ManagementCatalogTemplateComponent`. Les onglets, bandeaux, badges, cartes et tableaux viennent de `src/app/shared/ui`. Les cartes utilisent `CdkAccordion`, le tableau `MatTable` et `SelectionModel`. Les fenêtres de fonctions et de confirmation utilisent le socle `MatDialog` et son overlay CDK. Les notifications et le thème réutilisent les services du projet.

Le prototype générique `mockups/shared/guided-setup.ts` décrit les étapes par données, avec `MatStepper` (basé sur CDK Stepper), et utilise exclusivement le formulaire commun `RecordFormComponent`. Les tiroirs proviennent du gabarit partagé (`MatDrawer`). Aucun comportement d'onglet, de fenêtre, de formulaire ou de stepper n'est réimplémenté en HTML/JavaScript artisanal.

Seuls les fichiers de `mockups/` sont ajoutés. Les composants partagés et les routes de l'application restent ceux de la branche de travail. La compilation produit une application statique de démonstration, pas une intégration du nouveau parcours au produit.
