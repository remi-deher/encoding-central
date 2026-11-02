import { inject, Injectable, signal } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { workerShellAllows } from '../../shared/models/worker-shell';

/** Sur l'image worker, une adresse hors des six pages revient à la vue d'ensemble. */
export const workerShellGuard: CanActivateFn = (_, state) =>
  inject(ShellConfig).allows(state.url) ? true : inject(Router).parseUrl('/');

export type AppShell = 'central' | 'worker';

/** Le fichier `ui-config.json` est posé par l'image. Sans fichier, le shell est celui du central. */
@Injectable({ providedIn: 'root' })
export class ShellConfig {
  readonly shell = signal<AppShell>('central');

  worker(): boolean {
    return this.shell() === 'worker';
  }

  allows(url: string): boolean {
    return this.shell() !== 'worker' || workerShellAllows(url);
  }

  async load(): Promise<void> {
    try {
      const response = await fetch('/ui-config.json', { cache: 'no-store' });
      if (!response.ok) return;
      const body = (await response.json()) as { shell?: string };
      if (body.shell === 'worker') this.shell.set('worker');
    } catch {
      // Le central n'a pas besoin de ce fichier pour montrer toutes les pages.
    }
  }
}
