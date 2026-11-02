# Audit Angular / UI / Material 3 — 26 octobre 2026

> Audit initial conservé comme référence. La mise en œuvre et les vérifications ultérieures sont décrites dans [implementation.md](implementation.md).

## Avis d’ensemble

Le socle est solide et mérite d’être conservé : Angular 22.2, Material/CDK 22.2, composants standalone, réactivité largement fondée sur les signals, 123 composants déclarant explicitement OnPush, routes métier chargées par `loadComponent`, gabarits partagés, thèmes clair/sombre, focus global et réduction des animations.

La conformité est **partielle** : quelques composants contournent les conventions, le thème applicatif et le thème Material constituent deux sources de vérité, et le shell dépend fortement de surcharges CSS des conteneurs Material. Le risque principal est la dérive progressive entre pages, davantage que le choix des technologies.

Recommandation : corriger les problèmes de lisibilité et de responsive, consolider les primitives existantes, puis faire évoluer le shell. Une réécriture générale ferait perdre des comportements utiles déjà testés.

## Périmètre et niveau de preuve

Audit statique transversal de `frontend/src`, approfondi sur le shell, la navigation, le thème, les tiroirs, les catalogues, les paramètres, les onglets et l’éditeur de code. Lecture des règles du dépôt et de la fiche UI. Inventaire automatisé des composants et recherche des usages legacy, styles internes et couleurs hors jetons.

Vérification navigateur de parcours existants avec API simulée : accueil, activité, recherche et navigation vers les profils ; accueil avec données, panne initiale, échec de rafraîchissement et intégration IA. Les tests s’exécutent en Chromium sur ordinateur et profil Pixel 7. Ce n’est pas une recette visuelle exhaustive de chaque écran et état : les pages avec données de plugins, les tiroirs complexes, le graphe, les thèmes sombres de chaque feature, Firefox et Safari restent à exercer. Les constats tirés du CSS sont distingués des validations navigateur.

## Constats priorisés

P1 : lisibilité ou usage essentiel compromis. P2 : cohérence, robustesse ou maintenance. P3 : optimisation et finition.

| Priorité | Constat et preuve | Conséquence | Recommandation |
| --- | --- | --- | --- |
| P1 | `features/plugins/plugin-catalog-page.component.scss:51` emploie `--on-surface`, absent du thème, avec repli `#ffffff`. Autres occurrences aux lignes 112, 137, 164. | Le CSS impose du texte blanc sur les surfaces claires des cartes et dépôts. Défaut établi par les styles ; rendu peuplé à reproduire. | Employer `--text-primary` et `--text-secondary`, ou des rôles Material correctement exposés ; tester les cartes avec données dans les deux thèmes. |
| P1 | Même fichier, ligne 4 : colonnes `minmax(340px, 1fr)`, sans adaptation mobile. | Une colonne de 340 px ne peut pas tenir dans un contenu plus étroit, notamment écran de 320 px avec marges. | `minmax(min(100%, 340px), 1fr)` ; vérifier 320, 390 et 760 px avec contenu réel. |
| P2 | Même fichier : badges locaux en 11 px, couleurs vert/jaune/violet en dur et ombres très sombres. | Les états ne suivent ni les jetons sémantiques ni le minimum typographique de 12 px fixé par le dépôt. Contrastes à mesurer sur les cartes peuplées. | Réutiliser `StatusBadge`, les surfaces et élévations communes ; conserver les libellés métier. |
| P2 | `shared/ui/code-editor.component.ts:73–79` : cinq `@Input`, un `@Output`, un `@ViewChild`. | Écart explicite à UI_RULES ; ce sont toutefois des API Angular encore valides, pas une erreur Angular. | Migrer vers `input`, `output`, `viewChild`, avec synchronisation CodeMirror et destruction de la vue préservées. |
| P2 | Même composant, ligne 37 : focus via `var(--focus-ring, var(--primary))`, deux jetons absents du thème global. `aria-label` posé sur le div hôte et non configuré sur le contenu éditable CodeMirror. | Règle locale de focus invalide ; nom accessible de l’éditeur réel à contrôler. Le focus global peut encore fournir un indicateur. | Jeton `--accent-strong` ; `EditorView.contentAttributes` pour le nom accessible, vérification clavier dans l’éditeur généré. |
| P2 | `core/layout/app-shell.component.scss:204–237` : position, transform, visibility, display, marges et backdrop Material forcés avec `!important`. | Le shell neutralise une partie du mécanisme de `MatSidenav` ; dépendance aux détails DOM/CSS de Material. | S’appuyer sur les API publiques de sidenav, ou un scaffold CSS explicite pour la navigation persistante ; conserver CDK pour les overlays. |
| P2 | `src/styles.scss:548–569,681` et `shared/ui/detail-drawer/detail-drawer.ts:134–149,310–326` : géométrie des tiroirs définie dans plusieurs couches, avec valeurs différentes. | Desktop 80vw/82vw, rayons et mobile plein écran/feuille basse peuvent se contredire selon la spécificité. | Une seule couche propriétaire de la géométrie et des breakpoints ; garder dans le global uniquement les styles d’overlay nécessaires. |
| P2 | `detail-drawer.ts:406–415` : chaque instance ajoute/retire seule `body.detail-drawer-open`. | Si plusieurs instances coexistent, la fermeture ou destruction de l’une peut retirer le verrou d’une autre. Risque statique, scénario à reproduire avant correction. | Centraliser la gestion des overlays actifs ou garantir une seule instance active ; vérifier l’arbre réel de chaque gabarit. |
| P2 | `category-tabs.component.ts` et `subnav.component.ts` : mêmes mécanismes de sélection, clavier, défilement ; rôles tablist/tab sans contrat explicite `aria-controls`/tabpanel. | Deux implémentations à maintenir ; relation onglet/panneau incomplète dans ces primitives. | Partager le comportement, garder liens et onglets distincts sémantiquement ; ajouter des identifiants stables et panneaux associés, ou utiliser `MatTabGroup` selon le besoin. |
| P2 | `features/nodes/node-catalog-page.component.ts` : 2 205 lignes ; bibliothèques : 1 867 ; flow editor : 1 512 ; assistant : 1 379. | L’orchestration reste volumineuse malgré les gabarits ; isolation et revue plus difficiles. La taille seule n’établit pas un bug. | Extraire des contrôleurs et composants focalisés par responsabilité, sans déporter les règles métier dans shared/ui. |
| P2 | `styles.scss` définit une palette Material via `mat.define-theme` puis un autre jeu hexadécimal de rôles applicatifs ; police Inter sur root sans configuration typographique correspondante dans `define-theme`. | Contrôles Material et composants maison peuvent diverger en couleur et police. Inter n’est pas nécessairement disponible sur le poste. | Une source de vérité pour couleurs et typographie, adaptée aux rôles Material ; configurer la famille réellement livrée ou le même fallback système. |
| P2 | `styles.scss:247–248` : `--mat-outlined-button-*`, alors que les boutons de cette version emploient la famille `--mat-button-outlined-*`. | Overrides susceptibles d’être sans effet ; dérive silencieuse. | Utiliser les mixins d’override publiques de la version installée, puis vérifier les styles calculés. |
| P3 | `resource-card.component.scss:59–67`, styles Nœuds et boutons de tiroir : couleurs fixes restantes. Navigation mobile : compteur en 10 px. | Thème et échelle typographique non homogènes. | Rôles sémantiques et jetons dédiés pour catégories OS ; minimum 12 px selon les règles locales. Une couleur de catégorie reste distincte d’un état de santé. |
| P3 | `::ng-deep` dans CodeEditor, RecordForm, GuidedSetup, DataTable. | Couplage aux descendants internes ; fragilité lors des mises à jour. | API de thème pour Material ; `EditorView.theme` pour CodeMirror ; exceptions ciblées et documentées si aucune API publique ne convient. |
| P3 | Budget initial 1,23 MB pour 1,20 MB ; DataTable SCSS 8,68 kB pour 8 kB. | La compilation réussit mais la marge de performance se dégrade. | Examiner le graphe de bundles et les dépendances de l’assistant importées dans le shell ; éviter d’attribuer un coût précis sans analyse du bundle. Réduire les styles redondants du tableau. |

## Angular : ce qui est respecté et ce qui doit évoluer

- **Respecté** : OnPush sur les 123 composants inventoriés, approche standalone, chargement des vues métier par route, signals et `computed` largement utilisés, `@if`/`@for` dans les composants examinés, suivi de navigation réactif, protection de plusieurs éditeurs par `canDeactivate`.
- **À harmoniser** : CodeEditor legacy, pages très volumineuses, dépendance du shell à des composants de feature IA, comportement commun des onglets dupliqué.
- **Nuance** : RxJS, `subscribe`, `ngOnDestroy`, les décorateurs historiques et `*ngTemplateOutlet` ne sont pas des preuves de non-conformité Angular à eux seuls. RxJS reste adapté aux flux HTTP et événements. Vérifier la durée de vie de chaque abonnement ; ne pas remplacer mécaniquement tout abonnement par un signal.
- **Limite métier déjà documentée** : fermeture du tiroir de script non interceptée. À vérifier pour croix, clic extérieur, Échap et navigation ; la protection doit précéder la fermeture, pas seulement réagir à `closed`.

## Material 3 : évaluation

Le projet utilise bien le système de thème M3 de Material (`define-theme`, palettes tonales, clair/sombre, densité). Pilules, surfaces tonales, rayons contrastés et hiérarchie des titres vont dans la direction attendue. L’option `density: -1` sur pointeur fin et retour à 0 sur pointeur grossier est une bonne intention ; vérifier aussi les contrôles maison.

Material 3 Expressive n’est pas une certification acquise par la présence de gros arrondis ou d’une courbe spring. Angular Material n’implémente pas automatiquement toutes les recommandations Expressive. Les valeurs de rayons, la règle d’un seul bouton rempli et le minimum typographique de 12 px sont ici des conventions du dépôt ; ne pas les présenter comme des obligations universelles Angular.

Le point à corriger en premier est la cohérence des rôles : surface/on-surface, primary/on-primary, error/on-error. Les couleurs de feedback doivent évoluer ensemble en clair et sombre. Éviter les sélecteurs internes `.mdc-*` et `.mat-*` lorsque les mixins de thème publiques suffisent.

Accessibilité : focus visible et réduction des mouvements sont présents. La navigation utilise des liens, `aria-current`, des libellés et un piège de focus pour le menu mobile. Les onglets maison prévoient flèches/Home/End. Cela ne dispense pas de vérifier panneaux associés, restauration du focus et contenus dynamiques. Une cible de 48 px est un objectif tactile M3 ; le minimum WCAG 2.2 AA de 24 px comporte des exceptions et ne doit pas être confondu avec cet objectif.

## Factorisations proposées

| Zone | Réutiliser / faire évoluer | Frontière à respecter |
| --- | --- | --- |
| Outils et Store | ResourceCard/ResourceList, StatusBadge, ContextBanner, états de catalogue existants | Garder les capacités d’outil, dépôts et installation dans la feature Plugins. |
| Onglets et sous-navigation | Un comportement clavier partagé, styles communs ; liens pour changer de route, tabs pour panneaux locaux | Ne pas transformer toute navigation en tablist. |
| Tiroirs | DetailDrawer comme propriétaire de forme, taille, focus, backdrop et responsive ; RecordFormActions en pied | La feature décide de la validation et des modifications non enregistrées. |
| Shell | Navigation, barre système, contenu routé et hôte d’assistant séparés | Le shell ne doit pas orchestrer les propositions métier du flow. |
| Pages Nœuds / Bibliothèques | Contrôleur de page et sous-composants de sections cohérentes | Les composants shared/UI restent agnostiques des endpoints et règles métier. |
| Thème | Rôles partagés et mixins Material publics ; typographie cohérente | Les tons de catégories OS/média ne deviennent pas des états de succès/erreur. |
| Gabarits | Conserver Monitor, ResourceCatalog, PlatformAdmin, FlowWorkspace et Access | Ne pas créer un super-gabarit à dizaines d’options supplémentaires. ResourceCatalog délègue déjà de nombreuses entrées à ManagementCatalog : préférer progressivement des slots cohérents aux booléens. |

## Proposition de shell : « contenu d’abord »

Maquette autonome : `shell-proposition.html`. Aperçus : `shell-desktop.png`, `shell-sombre.png`, `shell-mobile.png`. Il s’agit d’une proposition visuelle avec interactions de démonstration, pas d’une implémentation Angular prête à fusionner. Les destinations simplifiées sont des ancres ; les données sont des emplacements marqués par des tirets.

1. **Navigation stable et calme** : panneau de 248 px sur grand écran ; rail de 88 px entre 761 et 1100 px ; navigation basse sous 761 px, avec entrée « Plus ». Regroupements courts, état actif tonal. Préserver les routes, rôles, recherche et accès aux destinations secondaires existants.
2. **Barre système unique de 64–72 px** : contexte à gauche, recherche, préférences et compte à droite. Un seul emplacement pour les actions système ; ne pas les répéter dans chaque en-tête. Représenter le nœud courant seulement depuis une source réelle et ne pas inventer un statut de disponibilité.
3. **En-tête de page éditorial** : titre 30–36 px, description courte, une action principale. Séparer cette zone des filtres et du contenu par 24–40 px, puis employer des espacements internes plus resserrés.
4. **Contenu fluide** : largeur maximale de 1 360 px pour la supervision ; largeur adaptée à la lecture pour les paramètres ; pleine largeur pour tableaux et atelier. Un grand écran ne doit pas étirer inutilement les textes ni plafonner le graphe.
5. **Surfaces peu nombreuses** : fond neutre, sections tonales, cartes seulement quand elles représentent un objet ou une information autonome. Éviter l’empilement carte dans carte et les ombres lourdes. Les métriques conduisent aux listes réelles.
6. **Atelier distinct** : garder le mode plein écran du graphe, sa barre de commandes et ses outils contextuels. Le nouveau shell standard ne doit pas réduire son canevas.
7. **Assistant discret** : conserver la conversation plein écran et l’assistant contextuel du flow, avec un point d’entrée stable. Une version ancrée peut réduire le bruit et la logique de positionnement ; le déplacement libre actuel reste un choix produit à arbitrer.
8. **Mobile** : action principale visible après le titre, contenu en une colonne, filtres pouvant défiler, pied tenant compte de safe-area, fiche détaillée en feuille basse cohérente. Pas de grille à largeur minimale supérieure au viewport disponible.

Architecture cible possible : AppShell → PrimaryNavigation + SystemBar + RouterOutlet + AssistantHost. `PageHeader` et les gabarits de feature composent le contenu. Pas de décision structurante appliquée dans cet audit ; une migration retenue mérite son ADR et la mise à jour de la fiche UI.

## Plan de correction

**Lot 1 — Lisibilité et accès** : jetons Plugins, grille mobile, badges, focus et nom accessible CodeMirror ; contrat des onglets. Validation avec cartes peuplées, clavier, 320/390 px et clair/sombre.

**Lot 2 — Consolidation** : thème unique, API Material publiques, géométrie des tiroirs et fermeture protégée, contrôles tactiles. Tester tout appelant des composants partagés et les interactions de superposition.

**Lot 3 — Shell** : expérimenter le cadre proposé sur Vue d’ensemble et Bibliothèques, puis Paramètres. Préserver routes, droits, scroll, filtres et focus. Étendre seulement après validation aux autres vues ; garder l’atelier spécialisé.

**Lot 4 — Maintenance et performance** : extractions des grosses pages par responsabilité, séparation AssistantHost, analyse des chunks et réduction des styles de tableau.

## Vérifications exécutées

- `npm run lint` : réussi.
- `npm run build` : réussi, deux avertissements de budget ci-dessus.
- `PLAYWRIGHT_CHROMIUM_EXECUTABLE=/usr/bin/chromium npx playwright test e2e/overview.spec.ts e2e/smoke.spec.ts --grep 'overview|primary navigation|search command' --workers=2` : **16 tests réussis**, 37 s. Comprend des audits axe ciblés sur l’accueil ; aucune attestation globale WCAG de l’application.
- Aucun changement au code applicatif, aucune correction présentée comme livrée. Les artefacts de ce dossier constituent le résultat de l’audit et la proposition.
- Tests unitaires complets, tous les E2E, recette backend réelle, matrice visuelle de tous les écrans : non exécutés.
