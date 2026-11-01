import { Routes } from '@angular/router';
import { guestGuard, sessionGuard, setupGuard } from './core/guards';
import { LoginPage } from './pages/login-page';
import { OverviewPage } from './pages/overview-page';
import { SettingsPage } from './pages/settings-page';
import { SetupPage } from './pages/setup-page';

export const routes: Routes = [
  { path: 'mise-en-service', component: SetupPage, canActivate: [setupGuard] },
  { path: 'connexion', component: LoginPage, canActivate: [guestGuard] },
  { path: '', component: OverviewPage, canActivate: [sessionGuard] },
  { path: 'parametres', component: SettingsPage, canActivate: [sessionGuard] },
  { path: '**', redirectTo: '' },
];
