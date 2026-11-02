import { Injectable, Signal, computed, signal } from '@angular/core';
import { IconName } from '../../shared/ui/icon.component';

export interface PageShellAction {
  readonly id: string;
  readonly label: string;
  readonly icon?: IconName;
  /** Masque l'action selon l'état métier courant de la page. */
  readonly visibleWhen?: () => boolean;
  readonly run: () => void;
}

export interface PageShellSearchResult {
  readonly id: string;
  readonly label: string;
  readonly description?: string;
  readonly group?: string;
  readonly glyph?: string;
  readonly path?: string;
  readonly run?: () => void;
}

export interface PageShellFilterAction {
  readonly label?: string;
  readonly activeCount?: () => number;
  readonly visibleWhen?: () => boolean;
  readonly toggle: () => void;
}

/**
 * Optional page-owned data rendered by the global shell search.
 * Pages retain ownership of search behavior, actions, and filter state.
 */
export interface PageShellContext {
  readonly searchPlaceholder?: string;
  /** Expose la même valeur à la barre du shell et au filtre de recherche propre à la page. */
  readonly query?: Signal<string>;
  /** Keeps the shell input and the current page's own filter in sync. */
  readonly onSearchQueryChange?: (query: string) => void;
  readonly searchResults?: (query: string) => readonly PageShellSearchResult[];
  readonly actions?: readonly PageShellAction[];
  readonly filter?: PageShellFilterAction;
}

@Injectable({ providedIn: 'root' })
export class PageShellContextService {
  private readonly registration = signal<{
    readonly owner: symbol;
    readonly context: PageShellContext;
  } | null>(null);

  readonly current = computed(() => this.registration()?.context ?? null);

  register(context: PageShellContext): () => void {
    const owner = Symbol('page-shell-context');
    this.registration.set({ owner, context });
    return () => {
      if (this.registration()?.owner === owner) this.registration.set(null);
    };
  }
}
