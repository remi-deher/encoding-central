import { unsavedChangesGuard } from './shared/guards/unsaved-changes-guard';
import { Routes } from '@angular/router';
import { authGuard, guestGuard } from './core/auth/auth.guard';

const applicationRoutes: Routes = [
  {
    path: '',
    pathMatch: 'full',
    loadComponent: () =>
      import('./features/home/home-page.component').then((module) => module.HomePageComponent),
    data: {
      title: 'Vue d’ensemble',
      navigation: {
        group: 'Vue générale',
        label: 'Vue d’ensemble',
        glyph: '▦',
        exact: true,
        keywords: ['accueil', 'dashboard'],
      },
    },
  },
  {
    path: 'worker',
    pathMatch: 'full',
    redirectTo: 'nodes',
  },
  {
    path: 'activity',
    loadComponent: () =>
      import('./features/activity/activity-page.component').then(
        (module) => module.ActivityPageComponent,
      ),
    data: {
      title: 'Activité',
      navigation: {
        group: 'Traitements',
        label: 'Activité',
        glyph: '≡',
        order: 2,
        keywords: ['tâches', 'exécutions', 'file', 'historique', 'demandes', 'assistant'],
      },
      description:
        'Suivez les exécutions de flows et les demandes de l’assistant IA : en cours, à traiter, en erreur ou terminées.',
    },
  },
  { path: 'tasks', pathMatch: 'full', redirectTo: 'activity' },
  { path: 'ai/activity', pathMatch: 'full', redirectTo: 'activity?type=ai' },
  {
    path: 'ai/assistant',
    loadComponent: () =>
      import('./features/ai/ai-assistant-page.component').then(
        (module) => module.AiAssistantPageComponent,
      ),
    data: {
      title: 'Assistant IA',
      contentWidth: 'full',
      navigation: {
        group: 'Intelligence artificielle',
        label: 'Assistant',
        glyph: '✦',
        order: 2,
        exact: false,
        keywords: ['conversation', 'question', 'flow'],
      },
      description: 'Posez des questions sur un flow publié avec une source explicite.',
    },
  },
  {
    path: 'ai',
    pathMatch: 'full',
    redirectTo: '/?view=ai',
  },
  {
    path: 'libraries/encoding-profiles',
    loadComponent: () =>
      import('./features/libraries/encoding-profile-library-page.component').then(
        (module) => module.EncodingProfileLibraryPageComponent,
      ),
    data: {
      title: 'Profils d’encodage',
      navigation: {
        group: 'Ressources',
        label: 'Profils d’encodage',
        glyph: '◫',
        order: 5,
        visible: false,
        keywords: ['qualité', 'encodeur'],
      },
      description: 'Gérez les profils de qualité et leurs réglages par encodeur.',
    },
  },
  {
    path: 'libraries/scripts',
    canDeactivate: [unsavedChangesGuard],
    loadComponent: () =>
      import('./features/libraries/script-library-page.component').then(
        (module) => module.ScriptLibraryPageComponent,
      ),
    data: {
      title: 'Scripts',
      navigation: {
        group: 'Ressources',
        label: 'Scripts',
        glyph: '≋',
        order: 3,
        visible: false,
        keywords: ['code'],
      },
      description: 'Gérez les scripts réutilisables dans vos flows.',
    },
  },
  {
    path: 'libraries/packs',
    loadComponent: () =>
      import('./features/libraries/component-pack-library-page.component').then(
        (module) => module.ComponentPackLibraryPageComponent,
      ),
    data: {
      title: 'Packs de composants',
      navigation: {
        group: 'Ressources',
        label: 'Packs de composants',
        glyph: '⊞',
        order: 4,
        visible: false,
        keywords: ['plugins'],
      },
      description: 'Parcourez les packs de composants réutilisables dans vos flows.',
    },
  },
  {
    path: 'libraries/resources',
    loadComponent: () =>
      import('./features/libraries/resource-library-page.component').then(
        (module) => module.ResourceLibraryPageComponent,
      ),
    data: {
      title: 'Ressources',
      navigation: {
        group: 'Ressources',
        label: 'Ressources',
        glyph: '◫',
        order: 5,
        visible: false,
        keywords: ['polices', 'fonts', 'fichiers'],
      },
      description: 'Partagez des polices avec tous les nœuds du cluster.',
    },
  },
  {
    path: 'libraries/templates',
    loadComponent: () =>
      import('./features/libraries/flow-template-library-page.component').then(
        (module) => module.FlowTemplateLibraryPageComponent,
      ),
    data: {
      title: 'Modèles de flow',
      navigation: {
        group: 'Ressources',
        label: 'Modèles',
        glyph: '▤',
        order: 2,
        visible: false,
        keywords: ['templates'],
      },
      description: 'Parcourez les modèles de flow réutilisables.',
    },
  },
  {
    path: 'libraries',
    loadComponent: () =>
      import('./features/libraries/library-catalog-page.component').then(
        (module) => module.LibraryCatalogPageComponent,
      ),
    data: {
      title: 'Bibliothèques',
      navigation: {
        group: 'Ressources',
        label: 'Bibliothèques',
        glyph: '◇',
        order: 1,
        keywords: ['médias', 'bibliothèques'],
      },
      description: 'Gérez les bibliothèques multimédias et les points d’accès des nœuds.',
    },
  },
  {
    path: 'flows',
    loadComponent: () =>
      import('./features/flows/flow-catalog-page.component').then(
        (module) => module.FlowCatalogPageComponent,
      ),
    data: {
      title: 'Flows',
      navigation: {
        group: 'Traitements',
        label: 'Flows',
        glyph: '⋈',
        order: 1,
        keywords: ['traitements', 'workflows'],
      },
      description: 'Composez et gérez les traitements définis pour vos médias.',
    },
  },
  {
    path: 'flows/guided',
    canDeactivate: [unsavedChangesGuard],
    loadComponent: () =>
      import('./features/flows/flow-guided-page.component').then(
        (module) => module.FlowGuidedPageComponent,
      ),
    data: { title: 'Créer un flow guidé' },
  },
  {
    path: 'flows/new',
    canDeactivate: [unsavedChangesGuard],
    loadComponent: () =>
      import('./features/flows/flow-editor-page.component').then(
        (module) => module.FlowEditorPageComponent,
      ),
    data: {
      title: 'Nouveau flow',
      layout: 'workspace',
      description: 'Assemblez les composants dans l’ordre de traitement souhaité.',
    },
  },
  {
    path: 'flows/:flowId/edit',
    canDeactivate: [unsavedChangesGuard],
    loadComponent: () =>
      import('./features/flows/flow-editor-page.component').then(
        (module) => module.FlowEditorPageComponent,
      ),
    data: {
      title: 'Modifier un flow',
      layout: 'workspace',
      description: 'Modifiez les composants et leur ordre de traitement.',
    },
  },
  {
    path: 'nodes',
    loadComponent: () =>
      import('./features/nodes/node-catalog-page.component').then(
        (module) => module.NodeCatalogPageComponent,
      ),
    data: {
      title: 'Nœuds',
      navigation: {
        group: 'Infrastructure',
        label: 'Nœuds',
        glyph: '▣',
        order: 1,
        keywords: ['workers', 'serveurs'],
      },
      description: 'Consultez les rôles, capacités et états des installations du cluster.',
    },
  },
  {
    path: 'node-groups',
    redirectTo: () => '/nodes?tab=groups',
  },
  {
    path: 'plugins',
    loadComponent: () =>
      import('./features/plugins/plugin-catalog-page.component').then(
        (module) => module.PluginCatalogPageComponent,
      ),
    data: {
      title: 'Plugins & Outils',
      navigation: {
        group: 'Infrastructure',
        label: 'Plugins & Outils',
        glyph: '✣',
        order: 2,
        keywords: [
          'plugins',
          'outils',
          'ffmpeg',
          'mkvtoolnix',
          'store',
          'extensions',
          'intégrations',
        ],
      },
      description: 'Gérez les plugins, les outils média et les dépôts configurés pour le cluster.',
    },
  },
  {
    path: 'updates',
    loadComponent: () =>
      import('./features/updates/updates-page.component').then(
        (module) => module.UpdatesPageComponent,
      ),
    data: {
      title: 'Mise à jour',
      navigation: { group: 'Administration', label: 'Mise à jour', glyph: '↻', order: 2 },
      description: 'Version installée, nouvelles versions et notes de version.',
    },
  },
  {
    path: 'settings',
    runGuardsAndResolvers: 'paramsOrQueryParamsChange',
    canDeactivate: [unsavedChangesGuard],
    loadComponent: () =>
      import('./features/settings/settings-page.component').then(
        (module) => module.SettingsPageComponent,
      ),
    data: {
      title: 'Paramètres',
      navigation: {
        group: 'Administration',
        label: 'Paramètres',
        glyph: '⚙',
        order: 1,
        adminOnly: true,
      },
      adminOnly: true,
      description:
        'Gérez les comptes utilisateurs, les jetons d’API pour l’IA, le serveur MCP et le système.',
    },
  },
];

const publicRoutes: Routes = [
  {
    path: 'login',
    canActivate: [guestGuard],
    loadComponent: () =>
      import('./features/auth/login-page.component').then((module) => module.LoginPageComponent),
    data: { title: 'Connexion', authPage: 'login' },
  },
  {
    path: 'setup',
    canActivate: [guestGuard],
    loadComponent: () =>
      import('./features/setup/setup-wizard-page.component').then(
        (module) => module.SetupWizardPageComponent,
      ),
    data: { title: 'Initialisation', authPage: 'setup' },
  },
];

/** Toute route de gestion exige une session ; l'ancienne console redirige vers la page Nœuds protégée. */
export const routes: Routes = [
  ...publicRoutes,
  ...applicationRoutes.map((route) =>
    route.redirectTo !== undefined
      ? route
      : { ...route, canActivate: [authGuard, ...(route.canActivate ?? [])] },
  ),
  { path: '**', redirectTo: '' },
];
