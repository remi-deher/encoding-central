# Frontend

Application Angular du projet de traitement de bibliothèques multimédias.

## Développement local

```powershell
npm ci
npm start
```

L’application est ensuite disponible sur `http://localhost:4200`.

## Compilation

```powershell
npm run build
```

Le frontend utilise Angular Material pour ses composants partagés. L’accueil lit les nœuds et bibliothèques via les API du cluster et s’actualise toutes les 10 secondes. Les exécutions et leur chronologie proviennent du registre de la base configurée, actualisé toutes les cinq secondes. Les économies ne sont pas encore mesurées. Les workers n’exécutent pas encore de flows ; le registre est vide tant qu’aucune attente n’y est enregistrée. Le catalogue liste les flows dans la base configurée. L’éditeur enregistre des versions validées et les rouvre ; leur sauvegarde JSON est effectuée dans un volume séparé. Aucun script ou outil média n’est encore lancé.

## Vérifications

```powershell
npm run lint
npm run format:flows
npm test
npm run build
```

Le lint contrôle les types TypeScript et les déclarations inutilisées. La vérification de format couvre les composants et modèles de l’éditeur. Les tests rapides utilisent le runner Node et protègent les règles du graphe (entrée unique, reconvergence, cycles, sorties et champs obligatoires). Le workflow GitHub Actions Frontend exécute ces mêmes commandes.

## Éditeur de flows

Chaque flow comporte une entrée obligatoire, générique ou spécialisée selon son type. Les actions Script, Préparer/Exécuter le traitement, Reporter et Terminer sont disponibles dans le catalogue. Une sortie peut ajouter un nouveau bloc ou rejoindre un bloc existant ; plusieurs chemins alternatifs peuvent converger.

Double-cliquer une étape pour la configurer, ou l’atteindre avec Tab puis Entrée. Appliquer au flow modifie la session d’édition ; Enregistrer une version publie une version validée dans SQLite local puis la propage au cluster. L’éditeur présente une séquence verticale et des branches repliables, avec les sorties déclarées par les plugins. Ajouter une étape insère le bloc sans supprimer la suite ; Ajouter une suite aux chemins non terminés crée une étape commune. Les étapes simples se réordonnent par glissement CDK ou avec Monter/Descendre. Les raccordements partagés complexes restent des renvois explicites et les blocs détachés sont conservés séparément. Vérifier le flow demande la validation backend des connexions, composants/version et configurations sans exécuter les scripts. Les positions historiques restent des métadonnées de compatibilité et ne déterminent plus l’affichage.

## Blocs de référence

Le bloc structuré est le gabarit partagé. Déclarer inline: true sur un champ du schéma pour l’afficher dans le bloc ; les champs sont rendus dans leur ordre de déclaration. Les nombres peuvent déclarer suggestions pour l’édition par liste. Le panneau détaillé permet de saisir les autres valeurs valides. Les contrôles directs et le panneau partagent la validation. Voir docs/architecture/domains/plugins.md et docs/architecture/domains/ui.md pour le contrat et la frontière avec le futur backend des plugins.
