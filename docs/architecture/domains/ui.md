---
domain: ui
updated: 2026-10-30
legacy_adr:
  [
    0008,
    0010,
    0011,
    0035,
    0044,
    0046,
    0047,
    0048,
    0050,
    0052,
    0053,
    0073,
    0077,
    0078,
    0079,
    0080,
    0081,
  ]
code:
  - frontend/src/styles.scss
  - frontend/src/app/core/layout/
  - frontend/src/app/core/theme/
  - frontend/src/app/shared/ui/
  - frontend/src/app/shared/templates/subnav-template.component.ts
  - frontend/src/app/shared/models/
  - frontend/src/app/features/
  - docs/design/
tags:
  [
    angular,
    material,
    thème,
    jetons,
    tableau,
    cartes,
    tiroir,
    éditeur de flow,
    canvas,
    page nœuds,
    shell,
    recherche contextuelle,
  ]
---

# Interface (Angular, Material 3)

Règles obligatoires : [UI_RULES.md](../../../UI_RULES.md). Ce fichier consigne l'état et les décisions durables ; maquettes et briefs dans `docs/design/`.

## Socle

- Angular + Angular Material/CDK uniquement. `frontend/src/styles.scss` : jetons `--space-1..6`, `--radius-*`, `--font-*`, `--elevation-*`, couleurs sémantiques ; thème clair/sombre (`ThemeService`, `data-theme`, suit `prefers-color-scheme`) ; **jamais de couleur ni de valeur hors échelle en dur**. Boutons Material en pilule ; un seul bouton rempli par vue, tonal pour l'alternative, contour pour le reste, destruction en rouge avec confirmation commune. Contrastes ≥ 4,5:1, focus visible, `prefers-reduced-motion`.
- Composants communs (toute évolution examine ses usages) : `DataTable` (`layout="table|cards"`, tri, sélection, actions de ligne/liste, confirmations), `RecordForm`/formulaire de schéma, `detail-drawer` (`size standard|wide`, `modal`, corps défilant ou rempli, pied fixe), `app-resource-card`, `app-resource-sheet` (présentation standard ou aperçu en cartes), `app-summary-tiles`/`app-metric-card`, `app-context-banner`, `PageHeader`, `GuidedSetup` (stepper), `SubnavTemplateComponent`, `MonitorTemplateComponent`, `ResourceCatalogTemplateComponent`, `LibrarySectionTabsComponent`, `FlowWorkspaceTemplateComponent`, `AssistantWorkspaceTemplateComponent` et `PlatformAdminTemplateComponent`. `SubnavTemplateComponent` aligne sur une ligne les informations contextuelles facultatives à gauche, la capsule de sous-navigation au centre et les actions de la section à droite ; il reste collé sous l’en-tête global au défilement. Toute page avec `app-subnav` passe par ce gabarit ; les onglets `CategoryTabs` qui servent de filtres ne sont pas concernés. `MonitorTemplateComponent` cadre le pilotage et ses zones responsives ; `SummaryTiles` et `MetricCard` restent informatifs par défaut et acceptent une destination lorsque la mesure oriente vers une prise en charge. Les catalogues Construire partagent le gabarit et les lignes `ResourceList` ; l’espace de conception partage son cadre entre création et modification tout en gardant le graphe et ses outils spécialisés. L’assistant partage son shell d’historique, de conversation et de contexte avec son panneau compact ; les sources, citations et actions restent fournies par la feature IA. Paramètres partage son en-tête, sa navigation par domaine et son tiroir protégé, tandis que les pages de domaine gardent la portée, les formulaires et les règles d’enregistrement. Les tableaux imbriqués et le suivi d'exécutions restent en table.
- Les modèles et niveaux de raisonnement IA, les presets d'encodeurs et les catalogues métier **viennent du backend**, pas du frontend. Distinguer composants de flow, composants UI et nœuds de cluster dans le code.

## Shell adaptatif et apparence

- Le shell standard réserve une navigation persistante CSS de 248 px, repliable en rail de 88 px. Ces dimensions sont définies par `.application-shell` et restent identiques sur toutes les routes ; aucun gabarit métier ne les redéfinit. Le rail s’active automatiquement entre 761 et 1 100 px. Sous 761 px, la navigation basse et son menu « Plus » conservent les destinations et les droits. Le graphe conserve son atelier plein écran et les pages d’accès leur cadre distinct.
- La barre système garde l’état du cluster et des tâches à droite. Le pied de navigation regroupe le repli, le profil et l’unique contrôle d’apparence ; l’icône et le nom de marque ne sont pas répétés dans la barre latérale. Les pages d’accès conservent le contrôle d’apparence dans leur en-tête. Le contenu standard est centré sur 1 360 px.
- Quatre palettes (Forêt, Océan, Iris, Rose) partagent les rôles système Material avec les composants maison. Le mode clair/sombre (`encoding.theme`) et la palette (`encoding.palette`) sont indépendants, locaux au navigateur et appliqués avant le démarrage Angular. Le contrôle Apparence du pied de navigation réunit ces deux choix ; les tons succès/avertissement gardent leur sens.
- `GlobalSearchBarComponent` réutilise `SearchFieldComponent`. À viewport et état de navigation identiques, elle garde la même largeur et le même centrage sur toutes les routes ; sa largeur s’adapte au viewport et aux deux états de la sidebar. Elle intègre la recherche, jusqu’à deux actions de page et le filtre à droite lorsque la page en a besoin. Sans action ou filtre déclaré, ni le bouton ni son séparateur ne sont rendus. La feuille de recherche s’accole à la barre, conserve sa largeur et son alignement ; son voile commence sous le fin séparateur inférieur.
- `PageShellContextService` permet à une page de fournir ses résultats, ses actions et son filtre. Les résultats de la page précèdent les destinations globales ; données, état des filtres et règles métier restent dans leur feature. Le raccordement des pages se fait au fil des évolutions métier ; celles sans contexte affichent uniquement les destinations globales.
- La Vue d’ensemble, Flows, Activité, Bibliothèques, Nœuds, Plugins & Outils et Paramètres masquent leur titre H1 visuel tout en le conservant accessible. Les actions, la sous-navigation et le contenu métier restent en place. `SubnavTemplateComponent` donne à toutes les pages avec sous-navigation une capsule centrée, un emplacement d’information facultatif à gauche et des actions contextuelles à droite ; les zones vides n’ajoutent ni séparateur ni contenu. La ligne reste collée sous l’en-tête du shell pendant le défilement et son panneau associé défile en dessous. La vue IA place « Ouvrir l’assistant » dans les actions à droite et conserve le nom accessible de son panneau sans répéter un en-tête de section. Le shell expose `--shell-header-sticky-offset` pour tenir compte des hauteurs desktop et mobile de la barre globale.
- Le corps du document est fixe et la région de contenu du shell constitue la zone de défilement des pages. Les espaces de travail bornés, comme l’assistant, gardent le défilement nécessaire dans leurs panneaux sans provoquer de débordement de page. Une route peut déclarer `contentWidth: 'full'` pour occuper toute la largeur disponible en conservant les marges du shell ; l’Assistant l’utilise afin que l’écart avec la navigation reste stable quand celle-ci se replie. La recherche globale reste visible au défilement, transmet la saisie au filtre de la page active et présente d’abord ses résultats contextuels ; Activité et les catalogues Flows, Packs, Profils d’encodage et Ressources l’utilisent sans champ de recherche local. Le catalogue Flows expose « Créer un flow » et « Importer » dans cette barre ; l’action Importer devient Exporter dès qu’au moins un flow est sélectionné et exporte la sélection en ZIP. Profils d’encodage expose « Nouveau profil » et Ressources « Ajouter des polices » dans cette même barre. Le graphe conserve son atelier plein écran et les pages d’accès leur cadre distinct.
- Le sélecteur de ressource dans le contexte de l’assistant utilise des libellés Material toujours flottants : le libellé vide ne couvre pas la cible de clic dans le panneau compact, y compris à 320 px.
- `AssistantHostComponent` porte le panneau et le lanceur IA ; les vues de conversation et de proposition de flow sont différées et conservent leur état après fermeture. Le shell ne porte plus leur orchestration.
- Les filtres `CategoryTabs` sont des groupes de boutons pressés, distincts des onglets `Subnav` reliés à leur panneau ; ils partagent le calcul de navigation clavier.
- La géométrie des tiroirs appartient à `DetailDrawer` (feuille basse sous 700 px). Son bandeau et son pied d’actions restent fixes ; le corps défile seul. Le mode de corps `fill` garde la navigation à onglets visible et réserve le défilement aux panneaux actifs, pour les fiches qui en ont besoin. Le voile modal couvre l’écran entier. Un registre gère les propriétaires du verrou modal. `closeGuarded` / `closeRequested` permet à la feature de confirmer avant fermeture ; les scripts utilisent ce contrat par le catalogue partagé.
- Empilement du shell : un `DetailDrawer` fermé reste sous l’en-tête sticky et le lanceur flottant de l’assistant. Un tiroir ouvert hors du mode `fill` élève son conteneur au-dessus de ces deux éléments ; le voile et le panneau gardent leur ordre interne. Ainsi, les contenus qui défilent ne masquent pas la recherche ni l’assistant.
- Décisions : [ADR 0018](../adr/0018-shell-adaptatif-et-palettes.md), [ADR 0019](../adr/0019-recherche-contextuelle-du-shell.md) et [ADR 0020](../adr/0020-gabarit-partage-de-sous-navigation.md). Analyse et validation : [audit](../../design/audit-ui-2026-10-26/audit.md), [mise en œuvre](../../design/audit-ui-2026-10-26/implementation.md).

## Éditeur de flow

- Un seul graphe métier, une vue **Canvas** dans `FlowGraphEditor` : placement libre, panoramique, zoom Ctrl+molette, minicarte et disposition verticale explicite et annulable. Les packs peuvent être dépliés dans le Canvas pour modifier leurs composants internes, puis repliés depuis sa barre d’outils. Les positions Canvas sont des préférences **locales au navigateur** (par flow), jamais dans la définition.
- Cartes Canvas adaptatives : hauteur 64 px ; largeur intrinsèque de 200 à 336 px pour le texte, élargie si les sorties exigent davantage de place (40 px par port). Icône de domaine, titre tronqué à la borne maximale, plugin et type ; résumé de trois paramètres scalaires `inline` au plus en infobulle au survol/focus du titre, jamais de script ni de secret. Couleurs communes par jetons clair/sombre, condition en vert ; sélection avec anneau et élévation, simulation en pastille compacte dont le détail est accessible au focus/clic.
- Entrée centrée en haut ; sorties en bas avec cibles de 32 px. Icônes uniquement pour les identifiants/libellés exacts reconnus (oui/succès, non, erreur, suite, ignoré, attente, médias), numéro pour les sorties inconnues ; « non » en ambre distinct d’une erreur technique en rouge. Libellé et description accessibles par ARIA/infobulle ; ajout `＋` au survol/focus du port (ou sélection tactile), bouton dédié et menu de liaison. La palette et la validation restent pilotées par le catalogue et `flow-graph.ts` : les icônes ne modifient jamais le sens d’exécution.
- Les mesures réelles (`ResizeObserver`) pilotent ancres SVG, obstacles, minicarte, centrage et placement automatique ; les estimations initiales restent compactes. Palette et poignée d’icône en glisser-déposer CDK ; Entrée ou double-clic pour configurer, Tab pour les ports ; minicarte inactive pendant la création d’une liaison pour ne pas intercepter un port situé dessous ; menu contextuel conservé (clic droit, Maj+F10, appui long 550 ms).
- Configuration : formulaire de schéma commun avec `importance` (basic/advanced), `inline`, `section`, visibilité conditionnelle (masquer garde la valeur), champs de référence qui conservent une référence inconnue, divulgation progressive dans l'éditeur seulement.
- Palette de composants : catégories fonctionnelles déclarées par `FlowComponentDescriptor.category` ; le serveur valide l'identifiant, l'éditeur fournit le libellé, une catégorie inconnue est rejetée et une catégorie absente (plugin tiers historique) apparaît sous « Utilitaires ». Les catégories ne dépendent ni du type de média ni du nom du plugin ; recherche globale et filtre par plugin restent indépendants.
- Assistant IA contextuel unique et testeur de flow : voir [ai.md](ai.md) et [flows.md](flows.md). Annuler/Rétablir porte sur le graphe, pas sur le runtime.
- Fiche du catalogue : aperçu lecture seule (même rendu, clé de placement distincte), versions, utilisation, validation, sauvegardes ; suppression avec tableau des bibliothèques détachées.

## Page Nœuds

Voir [cluster.md](cluster.md) : onglets **Ce nœud** / **Cluster**, aperçu en cartes pour les fonctions, l’activité, le matériel et les capacités du nœud local ; synthèse compacte de l’identité et de l’état du cluster ; membres affichés en cartes uniquement. Les demandes de rattachement restent un tableau d’approbation. Actions groupées, assistant de rattachement guidé et tiroir de fiche sont conservés. Pas de charge CPU/GPU affichée tant qu'aucune source réelle n'existe. Un accélérateur seulement détecté (non vérifié par mire) n'est jamais présenté comme utilisable.

## Bibliothèques et page Activité

La liste des bibliothèques suit le catalogue Flows : fiche de détail dans un tiroir, lignes de ressources avec sélection et barre d'actions groupées. Les lignes sont regroupées en « À configurer », « Prêtes » et « Désactivées » ; le nom ouvre la fiche et les actions de ligne restent dans le menu en haut à droite. La recherche est celle du shell. « Ajouter une bibliothèque » reste toujours dans la barre globale ; « Importer » y est remplacé par « Exporter » dès qu’une sélection existe. Le format d’export/import porte les noms et l’état actif ; les affectations de flow et accès par nœud sont à reconfigurer à l’import. Les bibliothèques peuvent être activées, désactivées ou supprimées ; elles ne sont pas publiables.

Les catalogues Ressources, Modèles de flow et Packs de composants gardent leur liste visible lorsqu’une fiche est ouverte et utilisent le `DetailDrawer` partagé. La sélection est portée par l’URL ; fermer le tiroir retire le paramètre correspondant. Les onglets entre types de catalogue restent une navigation entre routes.

Route `/activity` : cinq états centrés sur les fichiers découverts : **Non traité**, **En cours de traitement**, **Déjà traité**, **Échec du traitement** et **Traitement désactivé**. Les éléments actifs peuvent être annulés ; la relance d’un échec est proposée dans le tiroir après confirmation. Le tiroir réutilise `DetailDrawer` et expose les détails du fichier et, lorsqu’une exécution existe, son pipeline, ses événements et ses résultats. Le pipeline propose une console brute stdout/stderr actualisée avec le sondage de l’exécution (3 s lorsqu’elle est ouverte), en plus des vues simplifiée et détaillée ; le défilement des étapes et l’export/copier des logs restent disponibles. La recherche du shell filtre la page et propose ces fichiers avant les résultats globaux ; le filtre du shell propose bibliothèque, flow et nœud. Les anciens paramètres `view=active|errors|history|queue` sont traduits vers les états correspondants.

- L’API `GET /api/v1/activity/files` retourne l’inventaire découvert, les compteurs par état, la recherche, le filtre de bibliothèque et la pagination. L’état est basé sur l’exécution la plus récente du flow actuellement attribué, rapprochée par bibliothèque et chemin média ; une bibliothèque inactive place ses fichiers non traités dans **Traitement désactivé**. La page charge les fichiers par pages de 50 et conserve les états du traitement et du shell alignés.
- L'indicateur « jobs en cours » de l'en-tête compte uniquement les traitements locaux confirmés par un bail worker valide sous propriété active. Le panneau CDK ancré sous le bouton montre ces mêmes jobs : média, bibliothèque, flow, nœud, étape et progression. Les annulations en attente et les tâches sans bail restent consultables dans Activité, mais ne gonflent pas ce compteur. Requêtes filtrées uniquement pendant son ouverture ; fermeture par Échap, clic extérieur ou bouton, focus restauré. Lien vers `/activity?view=active` ; une progression à 100 % conserve l'état serveur « En cours ».
- Suivi partagé : EventSource conserve sa reconnexion native, le sondage HTTP de secours intervient après 15 s sans événement ; une réponse HTTP ancienne ou en erreur ne remplace pas un événement récent. Les listes paginées sont sondées sans requêtes qui se chevauchent, avec délai maximal de 20 s et conservation des données connues. Les demandes IA sont consultées uniquement par les administrateurs dans **Diagnostiquer**, hors filtre « Traitements » ; elles ne sont pas rechargées dans **Exploiter**. Le filtre « Assistant IA » ne charge pas les listes d'exécutions. Les véritables indisponibilités sont signalées sans vider le journal ni annoncer un historique vide.

## Autres pages

Assistant (`/assistant`) : historique groupé par période, création accessible depuis le panneau d’historique, composeur unifié pour le message, les pièces jointes et le modèle. La recherche globale affiche les conversations puis les correspondances dans le contenu des tours, avec ouverture directe sur le message trouvé.

Initialisation et accès (`/login`, `/setup`) : `AccessTemplateComponent` fournit le cadre public commun, la marque, l’introduction, l’indicateur de progression et le responsive. La connexion garde son formulaire compact ; le premier démarrage garde la fiche locale et le parcours de création de compte et de rattachement. Le setup conserve un fond clair uni et un halo en thème sombre. La fiche locale liste toutes les adresses IPv4/IPv6 avec leur interface, y compris boucle locale et interfaces virtuelles. L'« Accès au nœud » est distinct : URL publique/annoncée configurée, sinon origine du navigateur (hôte et port publié conservés en Docker bridge). Aucune adresse de passerelle n'est présentée comme IP LAN de l'hôte Docker.

Jonction depuis le premier démarrage : bouton explicite « Créer le compte et continuer » avant la recherche ou la vérification d’un cluster. Les informations du compte et le mode sont ensuite confirmés ; la vérification, l’envoi et les nouvelles tentatives restent dans l’assistant avec la même session. Le code se compare sur le cluster d’accueil. L’ouverture de l’application suit l’approbation ; une demande encore en attente propose « Suivre la demande dans Nœuds ».

Console de logs système (filtres de sévérité, recherche, pause, auto-scroll intelligent, export) ; scripts (liste en cartes, éditeur en tiroir, restauration dans le brouillon) ; IA & fournisseurs ; Plugins & Outils (plugins regroupés par famille, inventaire média remonté par le cluster, Store et dépôts). Le nom d'un plugin ouvre sa fiche dans la Mat Drawer ; ses switchs et ceux des outils règlent leur activation à l'échelle du cluster. Les outils gardent un seul switch d'activation. La recherche de cette page filtre la sous-page active ; les actions d'ajout de dépôt et d'import d'archive apparaissent dans la barre globale sur Store & Dépôts.

## Mise à jour

La page `/updates`, présente dans la navigation Administration et accessible aux deux rôles, affiche la version installée, les notes embarquées et celles de la dernière release publique si elle est joignable. Les états « à jour », « nouvelle version disponible », développement et vérification indisponible sont distincts ; le texte des notes est interpolé comme texte, sans interprétation HTML. La vérification se relance automatiquement toutes les 15 minutes et conserve les données affichées en cas d’erreur. Le cache serveur dure 10 minutes. L’installation se fait par mise à jour de l’image Docker.

## Limites connues

- Thème sombre non vérifié visuellement partout (éditeur de flow, blocs de plugins) ; Nodes redesign non vérifié en navigateur réel (mobile, E2E complets).
- `DetailDrawer` traite la croix, Escape et le voile comme la même demande de fermeture ; `closeGuard` permet de confirmer l’abandon d’une saisie avant de fermer.
- Le rôle lecture masque Paramètres et l'enrôlement direct ; les autres boutons d'écriture répondent par un refus.

## Assistant de création de flow

Le mode « Assistant vidéo » de la création ouvre `/flows/guided` avec le formulaire déclaratif commun et une protection contre la perte de saisie. Général, vidéo, pistes, résultat puis récapitulatif : la génération backend est validée avant l'enregistrement d'un brouillon et l'ouverture de l'éditeur. Une erreur conserve les choix. Les modèles et la création avancée restent accessibles.

Les formulaires guidés communs utilisent un stepper vertical sur écran étroit hors tiroir ; les tiroirs conservent leur avancement dans le bandeau et leur pied d’actions.

Les gabarits de bibliothèques transmettent explicitement les templates des actions au catalogue de ressources (contenu, pied et en-tête). Le catalogue retire le contenu et le pied du tiroir lorsque la fiche et les actions sont fermées, afin de détruire les formulaires inactifs.

Le bandeau du cluster présente les projections bloquées (table, cause sûre, date et action attendue) et les prérequis manquants d’attribution, dont le rattrapage sortant non confirmé d’un pair. Une présence réseau ou zéro projection en attente ne suffit pas à annoncer l’attribution disponible.
