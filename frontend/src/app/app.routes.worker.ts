import { Routes } from '@angular/router';
import { authGuard, guestGuard } from './core/auth/auth.guard';
import { unsavedChangesGuard } from './shared/guards/unsaved-changes-guard';

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
  { path: 'worker', pathMatch: 'full', redirectTo: 'workers' },
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
        keywords: ['tâches', 'exécutions', 'file', 'historique'],
      },
      description: 'Suivez les exécutions attribuées par le central.',
    },
  },
  { path: 'tasks', pathMatch: 'full', redirectTo: 'activity' },
  {
    path: 'workers',
    loadComponent: () =>
      import('./features/workers/worker-local-page.component').then(
        (module) => module.WorkerLocalPageComponent,
      ),
    data: {
      title: 'Workers',
      navigation: {
        group: 'Infrastructure',
        label: 'Workers',
        glyph: '▣',
        order: 1,
        keywords: ['workers', 'serveurs'],
      },
      description: 'Consultez l’état, la capacité et le lien avec le central de ce worker.',
    },
  },
  {
    path: 'plugins',
    loadComponent: () =>
      import('./features/plugins/worker-capabilities-page.component').then(
        (module) => module.WorkerCapabilitiesPageComponent,
      ),
    data: {
      title: 'Capacités',
      navigation: {
        group: 'Infrastructure',
        label: 'Capacités',
        glyph: '✣',
        order: 2,
        keywords: ['plugins', 'outils', 'ffmpeg', 'mkvtoolnix', 'extensions'],
      },
      description: 'Consultez les outils détectés, les plugins reçus et la synchronisation de ce worker.',
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
      description: 'Consultez les paramètres accessibles depuis ce worker.',
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
      import('./features/setup/worker-setup-page.component').then(
        (module) => module.WorkerSetupPageComponent,
      ),
    data: { title: 'Rattacher le worker', authPage: 'setup' },
  },
];

export const routes: Routes = [
  ...publicRoutes,
  ...applicationRoutes.map((route) =>
    route.redirectTo !== undefined
      ? route
      : { ...route, canActivate: [authGuard, ...(route.canActivate ?? [])] },
  ),
  { path: '**', redirectTo: '' },
];
