export type UserRole = 'admin' | 'viewer';

export interface CurrentUser {
  readonly id: string;
  readonly username: string;
  readonly role: UserRole;
}

export interface AuthStatus {
  readonly setupRequired: boolean;
  readonly user: CurrentUser | null;
}

export interface UserAccount {
  readonly id: string;
  readonly username: string;
  readonly role: UserRole;
  readonly isActive: boolean;
  readonly createdAt: string;
}

export interface EnrollmentWindow {
  readonly isOpen: boolean;
  readonly openUntil: string | null;
}

/** Mode « accepter la première liaison » : à usage unique, il s'éteint seul (architecture 0040). */
export interface FirstLinkAcceptance {
  readonly isActive: boolean;
  readonly expiresAt: string | null;
}

export const USER_ROLE_LABELS: Readonly<Record<UserRole, string>> = {
  admin: 'Administrateur',
  viewer: 'Lecture seule',
};

export const MINIMUM_PASSWORD_LENGTH = 12;

export type AccessDecision =
  | { readonly kind: 'allow' }
  | { readonly kind: 'redirect'; readonly path: string; readonly returnUrl?: string };

/** Décide si une route de gestion est accessible ; le serveur reste l’autorité, ceci évite seulement un aller-retour inutile. */
export function decideAccess(
  status: AuthStatus,
  requireAdmin: boolean,
  url: string,
): AccessDecision {
  if (status.setupRequired) return { kind: 'redirect', path: '/setup' };
  if (!status.user) return { kind: 'redirect', path: '/login', returnUrl: safeReturnUrl(url) };
  if (requireAdmin && status.user.role !== 'admin') return { kind: 'redirect', path: '/' };
  return { kind: 'allow' };
}

/** Décide si l’une des pages publiques d’authentification est utile à l’utilisateur courant. */
export function decideGuestAccess(status: AuthStatus, page: 'login' | 'setup'): AccessDecision {
  if (status.setupRequired)
    return page === 'setup' ? { kind: 'allow' } : { kind: 'redirect', path: '/setup' };
  if (status.user) return { kind: 'redirect', path: '/' };
  if (page === 'setup') return { kind: 'redirect', path: '/login' };
  return { kind: 'allow' };
}

/** N’accepte qu’un chemin interne à l’application pour la redirection après connexion. */
export function safeReturnUrl(url: string | null | undefined): string {
  if (!url || !url.startsWith('/') || url.startsWith('//') || url.includes('\\')) return '/';
  if (url === '/login' || url.startsWith('/login?') || url === '/setup') return '/';
  return url;
}

/** Les appels d’authentification gèrent eux-mêmes leurs 401 (identifiants refusés, session absente). */
export function isAuthEndpoint(url: string): boolean {
  return url.startsWith('/api/v1/auth/');
}

export function isStateChangingMethod(method: string): boolean {
  return !['GET', 'HEAD', 'OPTIONS'].includes(method.toUpperCase());
}
