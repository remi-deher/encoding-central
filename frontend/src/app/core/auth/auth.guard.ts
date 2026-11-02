import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AccessDecision, decideAccess, decideGuestAccess } from '../../shared/models/auth';
import { AuthService } from './auth.service';

function toResult(router: Router, decision: AccessDecision) {
  if (decision.kind === 'allow') return true;
  return router.createUrlTree(
    [decision.path],
    decision.returnUrl ? { queryParams: { returnUrl: decision.returnUrl } } : {},
  );
}

/** Protège les routes de gestion ; `data.adminOnly` les réserve aux administrateurs. */
export const authGuard: CanActivateFn = async (route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  const status = await auth.ensureLoaded();
  return toResult(router, decideAccess(status, route.data['adminOnly'] === true, state.url));
};

/** Garde des pages de connexion et d’initialisation. */
export const guestGuard: CanActivateFn = async (route) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  const status = await auth.ensureLoaded();
  return toResult(router, decideGuestAccess(status, route.data['authPage']));
};
