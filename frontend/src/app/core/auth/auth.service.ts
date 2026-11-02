import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { computed, inject, Injectable, signal } from '@angular/core';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthStatus, CurrentUser, safeReturnUrl } from '../../shared/models/auth';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);
  private readonly state = signal<AuthStatus | null>(null);
  private loading: Promise<AuthStatus> | null = null;

  readonly user = computed<CurrentUser | null>(() => this.state()?.user ?? null);
  readonly isAdmin = computed(() => this.user()?.role === 'admin');
  readonly setupRequired = computed(() => this.state()?.setupRequired ?? false);

  /** Charge l’état d’authentification une fois ; `force` relit le serveur. */
  ensureLoaded(force = false): Promise<AuthStatus> {
    const known = this.state();
    if (known && !force) return Promise.resolve(known);
    this.loading ??= firstValueFrom(this.http.get<AuthStatus>('/api/v1/auth/status'))
      .then((status) => {
        this.state.set(status);
        return status;
      })
      .finally(() => (this.loading = null));
    return this.loading;
  }

  async login(username: string, password: string): Promise<void> {
    const user = await firstValueFrom(
      this.http.post<CurrentUser>('/api/v1/auth/login', { username, password }),
    );
    this.state.set({ setupRequired: false, user });
  }

  async setup(
    optionsOrUsername?:
      | {
          nodeDisplayName?: string;
          mode?: 'standalone' | 'createCluster' | 'joinCluster';
          clusterName?: string;
          username?: string;
          password?: string;
        }
      | string,
    password?: string,
    setupToken?: string,
    legacyDisplayName = '',
  ): Promise<void> {
    const payload =
      typeof optionsOrUsername === 'object' && optionsOrUsername !== null
        ? {
            nodeDisplayName: optionsOrUsername.nodeDisplayName?.trim() || null,
            mode: optionsOrUsername.mode ?? null,
            clusterName: optionsOrUsername.clusterName?.trim() || null,
            username: optionsOrUsername.username?.trim() || null,
            password: optionsOrUsername.password || null,
          }
        : {
            username: optionsOrUsername ?? null,
            password: password ?? null,
            setupToken: setupToken || null,
            nodeDisplayName: legacyDisplayName.trim() || null,
          };
    const user = await firstValueFrom(this.http.post<CurrentUser>('/api/v1/auth/setup', payload));
    this.state.set({ setupRequired: false, user });
  }

  async logout(): Promise<void> {
    try {
      await firstValueFrom(this.http.post<void>('/api/v1/auth/logout', {}));
    } finally {
      this.state.set({ setupRequired: false, user: null });
      await this.router.navigate(['/login']);
    }
  }

  changePassword(currentPassword: string, newPassword: string): Promise<void> {
    return firstValueFrom(
      this.http.put<void>('/api/v1/auth/password', { currentPassword, newPassword }),
    );
  }

  /** Appelé quand le serveur refuse une session que l’interface croyait valide. */
  sessionExpired(): void {
    if (!this.user()) return;
    this.state.set({ setupRequired: false, user: null });
    void this.router.navigate(['/login'], {
      queryParams: { returnUrl: safeReturnUrl(this.router.url) },
    });
  }

  /** Message lisible pour une erreur d’API (problème de validation ou titre). */
  static errorMessage(error: unknown, fallback: string): string {
    if (!(error instanceof HttpErrorResponse)) return fallback;
    const body = error.error as
      { title?: string; errors?: Record<string, readonly string[]> } | null | undefined;
    const first = body?.errors ? Object.values(body.errors).flat()[0] : undefined;
    return first ?? body?.title ?? fallback;
  }
}
