# Mise en œuvre — shell et apparence

## Résultat

Le shell proposé est intégré à l’application Angular. Les routes, les droits, la navigation basse, la recherche clavier, l’atelier de flow et les conversations existantes sont conservés.

- Navigation persistante de 248 px sur grand écran ; rail de 88 px sur tablette ou après repli manuel. Le choix de repli manuel est mémorisé séparément de l’adaptation tablette.
- Barre supérieure commune avec recherche accessible au clic et par Ctrl/Cmd K, état réel, traitements, apparence et compte.
- Titres visibles et hiérarchie d’actions dans les gabarits de supervision, catalogues, paramètres et activité. Contenu standard centré sur 1 360 px, atelier pleine largeur. Espacement accru entre navigation locale et contenu, surfaces tonales pour les métriques.
- Quatre palettes **Forêt, Océan, Iris, Rose**, chacune en clair et sombre. Sélection immédiate dans la barre supérieure et Paramètres → Interface ; préférence locale persistée, indépendante de la luminosité.
- Les couleurs des composants Material et des composants maison proviennent des mêmes rôles système M3. Une seule génération des styles de composants, puis huit jeux de variables de couleur. Famille typographique commune et API publique d’override des boutons.
- L’assistant est isolé dans `AssistantHostComponent`. Les vues de conversation et de proposition de flow sont chargées à la demande ; leurs brouillons et conversations restent montés après fermeture.

## Corrections de l’audit livrées

| Zone | Changement |
| --- | --- |
| Plugins et Store | Textes reliés aux jetons existants, grilles sans minimum supérieur au contenu, entêtes et dépôts adaptatifs, badges partagés, élévations communes, vrai bouton accessible d’import d’archive. |
| CodeEditor | API signal, destruction CodeMirror conservée, styles via `EditorView.theme`, nom accessible sur le contenu éditable, focus visible, état désactivé également en lecture seule. |
| Tiroirs | Géométrie centralisée dans le composant, feuille basse mobile avec hauteur limitée à l’écran disponible ; registre de propriétaires pour préserver le verrou d’une autre instance. |
| Scripts | Confirmation avant fermeture par croix, Échap ou voile ; protection du retour navigateur ; actions dans le pied fixe partagé ; activation/désactivation par l’API du formulaire réactif. |
| Navigation locale | Calcul clavier partagé ; filtres en groupes de boutons pressés ; vrais onglets reliés à leur panneau dans les gabarits et l’activité. Les liens entre catalogues restent des liens de navigation. |
| Tableau partagé | Actions Material, suppression des styles de boutons redondants, correction des noms de variables d’override et suppression du `::ng-deep` de sélection. |
| Thème | Couleurs OS en jetons, texte destructif avec rôle sémantique, compteur mobile au minimum de 12 px. Les règles générales de réduction des mouvements sont conservées. |

## Validation

- Lint TypeScript : réussi.
- Tests Angular/Vitest : **57 fichiers, 229 tests réussis**.
- Tests de modèles Node : réussis pendant l’itération.
- Compilation de production : réussie **sans avertissement de budget**. Bundle initial **926,15 kB**, contre environ 1,23 MB à l’audit ; transfert estimé autour de 201,5 kB contre 253,7 kB.
- Formatage `format:flows` et `format:settings` : réussi.
- `git diff --check` : réussi.
- **70 scénarios navigateur distincts validés** sur Chromium ordinateur et profil mobile, au fil d’exécutions ciblées : accueil, recherche, navigation, six domaines des paramètres, protection des formulaires, tiroirs de flows, assistant flottant et contextuel, scripts, palettes, cartes d’outils et Store. Après intégration sur la dernière branche `dev`, les 229 tests unitaires, le lint, la compilation et le formatage ont été rejoués avec succès. La sélection navigateur a validé 69/70 tests ; le test géométrique restant mesurait pendant l’animation d’ouverture. Sa mesure attend désormais la fin du déplacement via une assertion répétée, sans délai arbitraire ; les quatre tests de scripts ont ensuite tous réussi sur ordinateur et mobile.
- Axe : accueil, paramètres, outils dans les huit combinaisons palette/mode et Store clair/sombre. Aucun constat dans les audits exécutés ; cela ne constitue pas une certification globale WCAG.
- Vérification mobile à 320 px sur Outils/Store et à 390 px sur le shell ; assertion géométrique du pied du tiroir de script à l’intérieur du viewport. Retour du focus et mémorisation de la palette vérifiés en navigateur.

Commandes de référence, depuis `frontend` :

```sh
npm run lint
npx ng test --watch=false
npm run build
npm run format:flows
npm run format:settings
PLAYWRIGHT_CHROMIUM_EXECUTABLE=/usr/bin/chromium npx playwright test e2e/appearance.spec.ts e2e/script-editor.spec.ts --workers=2
PLAYWRIGHT_CHROMIUM_EXECUTABLE=/usr/bin/chromium npx playwright test e2e/flow-drawer.spec.ts e2e/script-editor.spec.ts --workers=2
```

Les parcours existants de `overview`, `smoke`, `settings-redesign` et les scénarios ciblés de `flow-editor`/`ai-models` complètent cette sélection. Les E2E utilisent des API simulées et aucun média personnel.

## Aperçus

Captures de l’application implémentée, avec données de démonstration injectées par interception réseau :

- [Forêt](interface-forest.png), [Océan](interface-ocean.png), [Iris](interface-iris.png), [Rose](interface-rose.png).
- [Mode sombre](interface-sombre.png), [mobile](interface-mobile.png).

La maquette HTML et les images `shell-*` restent la proposition initiale ; les images `interface-*` montrent le rendu Angular livré.

## Limites et suite de maintenance

Les grandes pages métier Nœuds et Bibliothèques ne sont pas entièrement redécoupées en contrôleurs ; leur orchestration reste à faire évoluer progressivement. Les surcharges internes restantes des steppers ne sont pas remplacées sans équivalent public vérifié. Ces travaux ne sont pas présentés comme terminés.

Firefox, Safari, les interactions de tous les écrans avec backend réel, les GPU réels et les scénarios multi-nœuds restent hors validation locale. Aucune publication ni modification du backend n’est effectuée par ce changement.

Décision : [ADR 0018](../../architecture/adr/0018-shell-adaptatif-et-palettes.md). État de référence : [fiche UI](../../architecture/domains/ui.md).
