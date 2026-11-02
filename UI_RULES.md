# Règles d’architecture UI / UX — Angular 22 & Material Design 3 Expressive

Ce document définit les principes de conception, d’ergonomie et d’ingénierie frontend du projet. Il remplace les anciennes règles rigides « tout-tableau / tout-tiroir » par une architecture **Angular 22 moderne** et le langage visuel **Material Design 3 Expressive (M3 Expressive)**. La déclinaison détaillée pour les listes et fiches de ressources est décrite dans [Ressources et mise en scène M3 Expressive](docs/design/interface-resource-layout.md).

---

## 1. Intention utilisateur et clarté des parcours

Avant de concevoir une vue ou un composant, identifier la question principale à laquelle il répond :
- **Surveiller** : *Est-ce que le cluster et les traitements fonctionnent normalement ?* (Vue d’ensemble, métriques héroïques, alertes).
- **Gérer & Explorer** : *Quel est l’état de mes ressources (nœuds, bibliothèques, flows, plugins) et comment agir dessus ?*
- **Concevoir** : *Comment construire et tester mon pipeline de traitement ?* (Éditeur de flow).
- **Suivre & Comprendre** : *Où en sont les opérations en cours et que s’est-il passé ?* (Suivi temps réel, journaux de pipeline).
- **Configurer** : *Comment régler un service, un profil ou un accès ?*

### Choisir le bon conteneur d’interaction
- **Vue principale / Sections structurées** : Les informations essentielles (état, capacité, matériel, flow actif, activité) sont lisibles immédiatement sans obliger l’utilisateur à ouvrir une série de petites boîtes ou de sous-fenêtres.
- **Tiroir latéral (`DetailDrawer`)** : Réservé à l’inspection détaillée d’un élément, à la configuration d’un bloc de flow ou à une édition contextuelle ciblée. Ne jamais empiler plusieurs tiroirs dans le DOM et éviter d’enfermer toute une application dans un tiroir.
  - **Gabarit** : bandeau teinté (`variant` : neutre, `info`, `danger`, `success`) avec pastille (`icon`), sur-titre (`eyebrow`), titre et sous-titre (`description`) ; avancement d’un parcours par `step` (« Étape 1 sur 2 ») plutôt que par un stepper dans le corps ; pied d’actions toujours collé en bas (`footerActions`), indépendant du défilement du corps ; voile modal couvrant tout l’écran ; feuille basse avec poignée sous 700 px. Le corps défile par défaut. Pour une fiche à onglets, `bodyLayout="fill"` réserve la hauteur restante aux onglets et à leur contenu : la liste d’onglets reste fixe et le panneau actif porte son propre défilement. Un formulaire `RecordForm` placé dans un tiroir s’utilise avec `embedded` : il ne dessine ni boutons ni description et expose `primaryLabel`, `secondaryLabel`, `primary()`, `secondary()`, `stepIndex` et `stepCount` pour le pied du tiroir (voir l’accueil, « Créer un flow »). `RecordFormActionsComponent` dessine les boutons dans le pied ; `ManagementCatalogTemplateComponent` l’accepte via `#actionFooterActions` et `actionStep`. Ne pas répéter dans le corps la description déjà portée par l’en-tête.
- **Modale (`MatDialog`)** : Réservée aux confirmations critiques, aux codes de vérification d’appairage et aux décisions courtes qui requièrent l’attention immédiate.

---

## 2. Langage visuel : Material Design 3 Expressive

L’interface adopte les principes de **Material 3 Expressive** pour offrir une hiérarchie visuelle claire, chaleureuse et immédiatement scannable en thème clair comme en thème sombre.

### 2.1. Surfaces tonales plutôt que bordures omniprésentes
- Structurer la profondeur par **contraste de luminance des conteneurs M3** plutôt que par des grilles de bordures `1px solid` partout :
  - `--surface-container-lowest` / `--page-background` : toile de fond de l’application.
  - `--surface-container-low` : grands ensembles et sections de regroupement.
  - `--surface-container` : cartes de ressources et panneaux au repos.
  - `--surface-container-high` : cartes survolées, barres d’outils contextuelles, tiroirs.
  - `--surface-container-highest` : puces interactives, contrôles segmentés, jauges.
  - Conteneurs tonaux sémantiques : `--accent-soft` (`primary-container`), `--tertiary-soft` (`tertiary-container`), `--success-soft`, `--warning-soft`, `--danger-soft`.
- Réserver les bordures visibles aux champs de saisie (`outline`), aux séparateurs légers de listes denses et aux états sélectionnés ou mis en exergue.

### 2.2. Formes expressives, groupes connectés et « Shape Morphing »
- Jouer sur le contraste des rayons de bordure :
  - Conteneurs principaux et cartes héroïques : `--radius-xl` (`20px`–`24px`) à `--radius-2xl` (`28px`).
  - Cartes de ressources et panneaux : `--radius-lg` (`16px`).
  - Éléments internes et puces : `--radius-md` (`12px`) ou `--radius-sm` (`8px`).
  - Boutons d’action et badges d’état : `--radius-pill` (`999px`).
- **Conteneurs groupés M3 (*Connected Containers*)** : Dans une pile verticale de ressources ou de sections liées (`ResourceSheet`), les blocs forment un ensemble cohérent avec des coins extérieurs généreux (`20px`–`24px`), des coins intérieurs adoucis (`6px`–`8px`) et un espacement fin (`2px`–`4px`) qui remplace les filets de bordure traditionnels.
- **Morphing de forme à l’interaction** : Les éléments sélectionnés ou actifs marquent leur état par une transition fluide de couleur tonale, d’élévation et de rayon.

### 2.3. Typographie éditoriale et « Hero Metrics »
- **Contraste d’échelle franc** :
  - Utiliser les jetons d’affichage (`--font-display-sm: 28px`, `--font-display-md: 36px`, `--font-display-lg: 44px`) avec `font-variant-numeric: tabular-nums` et un poids fort (`700–800`) pour les mesures clés (workers actifs, tâches en cours, occupation).
  - Titres de page et d’en-tête expressifs (`--font-headline`, `--font-title-lg`), distincts des libellés de section (`--font-label` en capitales aérées ou `--font-title-sm`).
  - Jamais de taille de police inférieure à `--font-label` (`12px`). Sur mobile, les champs de saisie utilisent au minimum `14px`–`16px`.
- **Polices spécialisées** : Police sans-serif géométrique/humaniste pour les titres et l’interface, police monospace (`ui-monospace`, `JetBrains Mono`, `Consolas`) pour les identifiants techniques, chemins de fichiers, commandes et journaux.

### 2.4. Physique du mouvement (« Spring Physics »)
- Remplacer les transitions linéaires ternes par les courbes d’accélération M3 Expressive :
  - `--motion-spring-fast` (`cubic-bezier(0.42, 1.67, 0.21, 0.90)`) pour les micro-interactions, bascules, puces et boutons.
  - `--motion-spring-standard` (`cubic-bezier(0.27, 1.06, 0.18, 1.00)`) pour l’ouverture de panneaux, jauges et cartes.
  - `--motion-emphasized` (`cubic-bezier(0.2, 0, 0, 1)`) pour les transitions de page et de tiroir.
- Toujours respecter `@media (prefers-reduced-motion: reduce)`.

---

## 3. Architecture Angular 22 Moderne

### 3.1. Réactivité par Signals et performance `OnPush`
- **Signals partout** : Utiliser exclusivement `input()`, `output()`, `model()`, `signal()`, `computed()`, `linkedSignal()`, `viewChild()` et `viewChildren()`.
- **Aucun décorateur legacy** : Ne pas utiliser `@Input()`, `@Output()`, `@ViewChild()`, `@ViewChildren()`, `@HostBinding()` ou `@HostListener()`. Déclarer les liaisons d’hôte dans `host: { ... }` du décorateur `@Component`.
- **Détection de changements** : Privilégier `ChangeDetectionStrategy.OnPush` sur les composants UI et les vues afin d’éviter tout cycle de rendu superflu lors des rafraîchissements périodiques (polling).
- **Flux de contrôle natif** : Utiliser `@if`, `@for` (avec `track` explicite et stable), `@switch` et `@let`.

### 3.2. Découpage modulaire : Socle partagé + Composants de Feature
- **Socle partagé (`shared/ui/`, `shared/templates/`)** : Héberge le design system M3 Expressive (en-têtes, cartes de ressources, fiches `ResourceSheet`, tuiles de métriques expressives, tableaux de données, tiroirs, modales, badges, éditeur de graphe). Ces composants sont purs et agnostiques du métier.
- **Composants de feature (`features/<domaine>/`)** : Contrairement à l’ancienne règle qui interdisait tout sous-composant propre à une fonctionnalité et produisait des fichiers monolithiques de près de 2 000 lignes, **une page complexe doit être découpée en sous-composants et contrôleurs focalisés** (par exemple un sous-composant ou contrôleur pour la gestion du cluster, pour l’accès aux bibliothèques ou pour l’inspecteur de flow).
- **Contrôleurs / Services de feature** : L’orchestration HTTP, le polling et les transformations métier riches vivent dans des services/contrôleurs dédiés (sur le modèle de `LocalNodeController`) plutôt que dans le template ou le corps d’un composant géant.

### 3.3. Formulaires et actions
- **Formulaires typés vs Schémas déclaratifs** :
  - Utiliser les composants de schéma déclaratif (`RecordFormComponent`, `ComponentConfigurationFormComponent`, `GuidedSetupComponent`) pour les assistants standardisés et les schémas dynamiques fournis par le backend/plugins.
  - Pour les éditeurs métier spécialisés (scripts, profils d’encodage, planification hebdomadaire, arbre de conditions), utiliser des formulaires Angular typés et ergonomiques avec protection contre la perte de modifications non enregistrées.
- **Placement et hiérarchie des actions** :
  - Sur une carte de ressource en liste : **une seule action principale** directe (clic sur le titre ou bouton d’ouverture tonal/texte) et un menu contextuel `⋮` (`MatMenu`) pour les actions secondaires. Ne jamais aligner 4 boutons en bas de chaque carte.
  - Dans une fiche ouverte : les actions globales sur la ressource vivent dans l’en-tête.
  - Tous les retours d’action (succès, erreur) passent par `NotificationService`.

### Gabarit des Paramètres
`PlatformAdminTemplateComponent` (`shared/templates/platform-admin-template.component.ts`) fournit l’en-tête, la navigation par domaine, le panneau de contenu et le tiroir modal d’édition protégé. La page feature reste propriétaire de la portée des réglages, des paramètres d’URL, de la confirmation de navigation et des données. Tous les domaines utilisent ensuite les mêmes briques de section (`shared/ui/configuration-section.component.ts` et `shared/ui/settings/`) :
- `app-configuration-section` : pastille (`icon`), titre h2, description, puce d’état (`badge`) et actions d’en-tête (`section-actions`, boutons tonals).
- `app-setting-group` + `app-setting-row` : pile de lignes groupées ; libellé et aide à gauche, contrôle à droite ; `stacked` pour un champ large, `dense` pour une liste, `row-lead` pour une pastille.
- `app-settings-notice` : seul bloc d’aide ou d’avertissement.
- `app-settings-save-bar` : « Annuler / Enregistrer » collés en bas, visibles seulement s’il y a des modifications. Un réglage sans conséquence (thème, interrupteur d’un compte) s’applique immédiatement, sans barre.
- Les réglages rarement utiles tiennent dans un seul dépliant « Options avancées » en bas de la section ; l’édition d’un élément se fait dans le tiroir au gabarit commun.

---

## 4. Intégrité des métriques, Accessibilité et Mobile

1. **Vérité des données** :
   - N’afficher que des métriques adossées à une source réelle. Ne jamais inventer une tuile ou une jauge fictive.
   - Une progression à `100 %` ne prouve pas la terminaison d’une tâche : seul l’état serveur fait foi.
   - Distinguer visuellement : **état actif observé**, **état souhaité en attente**, **valeur indisponible** et **dernière valeur connue d’un nœud hors ligne** (jauge atténuée/hachurée `is-stale`).
2. **Accessibilité (WCAG AA) et Clavier** :
   - Tout état communiqué par la couleur (succès, avertissement, erreur, accélérateur matériel) comporte un libellé explicite.
   - Les contrôles interactifs respectent une cible minimale accessible et possèdent un `aria-label` explicite lorsque leur libellé est iconographique.
   - Les rafraîchissements périodiques (polling) préservent la sélection, le focus et les saisies en cours.
3. **Adaptation Mobile et Tablette** :
   - Aucune page ne provoque de défilement horizontal global.
   - Les grilles de métriques et les sections de `ResourceCard` / `ResourceSheet` se replient harmonieusement sur 1 ou 2 colonnes sur écran étroit.
   - Supprimer systématiquement le code, les styles et les composants devenus obsolètes après vérification de leurs usages.
