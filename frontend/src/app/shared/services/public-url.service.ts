import { inject, Injectable, signal } from '@angular/core';
import { NetworkSettingsService } from './network-settings.service';

/**
 * Adresse à afficher pour joindre ce nœud : l'URL publique déclarée dans Paramètres > Réseau,
 * sinon l'adresse du navigateur (qui peut être une adresse interne derrière un reverse proxy).
 */
@Injectable({ providedIn: 'root' })
export class PublicUrlService {
  private readonly browserOrigin = typeof window !== 'undefined' ? window.location.origin : '';
  private readonly declared = signal<string | null>(null);

  /** Base des adresses affichées, sans « / » final. */
  readonly origin = () => this.declared() ?? this.browserOrigin;
  readonly isDeclared = () => this.declared() !== null;

  constructor() {
    // Réglage réservé aux administrateurs : en cas de refus, on garde l'adresse du navigateur.
    inject(NetworkSettingsService)
      .get()
      .subscribe({
        next: (settings) => this.set(settings.publicUrl),
        error: () => undefined,
      });
  }

  set(value: string | null | undefined): void {
    this.declared.set(value?.trim() ? value.trim().replace(/\/+$/, '') : null);
  }
}
