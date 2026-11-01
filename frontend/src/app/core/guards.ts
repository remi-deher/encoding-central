import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth.service';

export const sessionGuard: CanActivateFn = async () => {
  const auth = inject(AuthService);
  const router = inject(Router);
  await auth.ensureKnown();
  if (auth.setupRequired()) return router.parseUrl('/mise-en-service');
  if (!auth.user()) return router.parseUrl('/connexion');
  return true;
};

export const guestGuard: CanActivateFn = async () => {
  const auth = inject(AuthService);
  const router = inject(Router);
  await auth.ensureKnown();
  if (auth.setupRequired()) return router.parseUrl('/mise-en-service');
  if (auth.user()) return router.parseUrl('/');
  return true;
};

export const setupGuard: CanActivateFn = async () => {
  const auth = inject(AuthService);
  const router = inject(Router);
  await auth.ensureKnown();
  if (!auth.setupRequired()) return router.parseUrl(auth.user() ? '/' : '/connexion');
  return true;
};
