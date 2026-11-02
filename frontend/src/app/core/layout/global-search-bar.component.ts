import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { OverlayModule, ConnectedPosition } from '@angular/cdk/overlay';
import { MatButtonModule } from '@angular/material/button';
import { AuthService } from '../auth/auth.service';
import { IconComponent } from '../../shared/ui/icon.component';
import {
  getSearchableNavigationItems,
  NavigationItem,
} from '../navigation/primary-navigation.component';
import { PageShellContextService, PageShellSearchResult } from './page-shell-context';
import { Router } from '@angular/router';
import { SearchFieldComponent } from '../../shared/ui/search-field.component';

interface SearchEntry {
  readonly id: string;
  readonly label: string;
  readonly description?: string;
  readonly group?: string;
  readonly glyph?: string;
  readonly path?: string;
  readonly run?: () => void;
}

@Component({
  selector: 'app-global-search-bar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent, MatButtonModule, OverlayModule, SearchFieldComponent],
  template: `
    <div class="global-search-frame">
      <div
        #searchCapsule
        class="global-search-capsule"
        [class.is-open]="panelOpen()"
        [class.has-context-controls]="visibleActions().length > 0 || filterButtonCount() !== null"
        cdkOverlayOrigin
        #searchOrigin="cdkOverlayOrigin"
      >
        <app-search-field
          class="global-search-field"
          variant="global"
          [label]="context()?.searchPlaceholder || 'Rechercher dans Encoding'"
          [placeholder]="context()?.searchPlaceholder || 'Rechercher dans Encoding…'"
          [value]="query()"
          [inputRole]="'combobox'"
          [ariaAutocomplete]="'list'"
          [ariaControls]="panelOpen() ? 'global-search-results' : null"
          [ariaExpanded]="panelOpen()"
          [activeDescendant]="activeItemId()"
          [filterCount]="filterButtonCount()"
          [filterLabel]="context()?.filter?.label || 'Filtres'"
          [filterExternallyManaged]="true"
          [filterHasPreviousSegment]="visibleActions().length > 0"
          (focused)="openPanel()"
          (valueChange)="updateQuery($event)"
          (filterAction)="openFilters()"
          (keydown)="onSearchKeydown($event)"
        >
          @for (action of visibleActions(); track action.id) {
            <button
              search-actions
              mat-button
              type="button"
              class="search-action-button has-divider"
              [attr.aria-label]="action.label"
              (click)="runAction(action.run)"
            >
              @if (action.icon) {
                <app-icon [name]="action.icon" />
              }
              <span>{{ action.label }}</span>
            </button>
          }
        </app-search-field>
      </div>
    </div>

    <ng-template
      cdkConnectedOverlay
      [cdkConnectedOverlayOrigin]="searchOrigin"
      [cdkConnectedOverlayOpen]="panelOpen()"
      [cdkConnectedOverlayHasBackdrop]="true"
      cdkConnectedOverlayBackdropClass="global-search-backdrop"
      [cdkConnectedOverlayPanelClass]="panelClass"
      [cdkConnectedOverlayPositions]="panelPositions"
      [cdkConnectedOverlayWidth]="panelWidth()"
      [cdkConnectedOverlayPush]="true"
      [cdkConnectedOverlayViewportMargin]="12"
      (backdropClick)="closePanel()"
      (overlayKeydown)="onOverlayKeydown($event)"
    >
      <section class="global-search-sheet" aria-label="Résultats de recherche">
        <div
          id="global-search-results"
          class="global-search-results"
          role="listbox"
          aria-label="Résultats de recherche"
        >
          @if (context()?.searchResults && query().trim()) {
            <section class="global-search-group" role="group" aria-label="Dans cette page">
              <h2>Dans cette page</h2>
              @for (item of pageResults(); track item.id; let index = $index) {
                <button
                  type="button"
                  role="option"
                  class="global-search-result"
                  tabindex="-1"
                  [id]="'global-search-option-' + index"
                  [class.is-active]="activeIndex() === index"
                  [attr.aria-selected]="activeIndex() === index"
                  (mouseenter)="activeIndex.set(index)"
                  (click)="activate(item)"
                >
                  <span class="global-search-result-glyph" aria-hidden="true">{{
                    item.glyph || '⌕'
                  }}</span>
                  <span class="global-search-result-copy">
                    <span class="global-search-result-label">{{ item.label }}</span>
                    @if (item.description || item.group) {
                      <span class="global-search-result-description">
                        {{ item.description || item.group }}
                      </span>
                    }
                  </span>
                  <span class="global-search-result-enter" aria-hidden="true">↵</span>
                </button>
              } @empty {
                <p class="global-search-empty">Aucun résultat dans cette page.</p>
              }
            </section>
          }

          @if (globalResults().length) {
            <section
              class="global-search-group"
              role="group"
              aria-label="Partout dans Encoding"
              [class.has-divider]="
                pageResults().length > 0 || !!(context()?.searchResults && query().trim())
              "
            >
              <h2>Partout dans Encoding</h2>
              @for (item of globalResults(); track item.id; let index = $index) {
                <button
                  type="button"
                  role="option"
                  class="global-search-result"
                  tabindex="-1"
                  [id]="'global-search-option-' + (pageResults().length + index)"
                  [class.is-active]="activeIndex() === pageResults().length + index"
                  [attr.aria-selected]="activeIndex() === pageResults().length + index"
                  (mouseenter)="activeIndex.set(pageResults().length + index)"
                  (click)="activate(item)"
                >
                  <span class="global-search-result-glyph" aria-hidden="true">{{
                    item.glyph || '◇'
                  }}</span>
                  <span class="global-search-result-copy">
                    <span class="global-search-result-label">{{ item.label }}</span>
                    @if (item.description || item.group) {
                      <span class="global-search-result-description">
                        {{ item.description || item.group }}
                      </span>
                    }
                  </span>
                  <span class="global-search-result-enter" aria-hidden="true">↵</span>
                </button>
              }
            </section>
          } @else if (!pageResults().length) {
            <p class="global-search-empty" role="status">
              Aucun résultat ne correspond à « {{ query() }} ».
            </p>
          }
        </div>
      </section>
    </ng-template>
  `,
  styleUrl: './global-search-bar.component.scss',
  host: {
    '(window:resize)': 'measureCapsule()',
  },
})
export class GlobalSearchBarComponent {
  private readonly router = inject(Router);
  private readonly auth = inject(AuthService);
  private readonly pageShell = inject(PageShellContextService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly capsule = viewChild<ElementRef<HTMLDivElement>>('searchCapsule');
  private readonly searchField = viewChild(SearchFieldComponent);

  protected readonly context = this.pageShell.current;
  private readonly localQuery = signal('');
  protected readonly query = computed(() => this.context()?.query?.() ?? this.localQuery());
  protected readonly panelOpen = signal(false);
  protected readonly activeIndex = signal(0);
  protected readonly panelWidth = signal<number | string>(880);
  protected readonly panelClass = 'global-search-sheet-pane';
  protected readonly panelPositions: ConnectedPosition[] = [
    { originX: 'start', originY: 'bottom', overlayX: 'start', overlayY: 'top' },
    { originX: 'end', originY: 'bottom', overlayX: 'end', overlayY: 'top' },
  ];

  protected readonly visibleActions = computed(
    () =>
      this.context()
        ?.actions?.filter((action) => !action.visibleWhen || action.visibleWhen())
        .slice(0, 2) ?? [],
  );
  protected readonly filterButtonCount = computed(() =>
    this.context()?.filter &&
    (!this.context()?.filter?.visibleWhen || this.context()?.filter?.visibleWhen?.())
      ? (this.context()?.filter?.activeCount?.() ?? 0)
      : null,
  );
  protected readonly pageResults = computed<SearchEntry[]>(() => {
    const context = this.context();
    const query = this.query().trim();
    if (!context?.searchResults) return [];
    return context.searchResults(query).map((item: PageShellSearchResult) => ({ ...item }));
  });
  protected readonly globalResults = computed<SearchEntry[]>(() => {
    const query = this.normalize(this.query().trim());
    const items = getSearchableNavigationItems(this.router.config).filter(
      (item) => !item.adminOnly || this.auth.isAdmin(),
    );
    const matching = query
      ? items.filter((item) =>
          this.normalize(
            [item.label, item.group ?? '', ...(item.keywords ?? [])].join(' '),
          ).includes(query),
        )
      : items;
    return matching.slice(0, 6).map((item: NavigationItem) => ({
      id: `navigation:${item.path}`,
      label: item.label,
      group: item.group,
      glyph: item.glyph,
      path: item.path,
    }));
  });
  private readonly allResults = computed(() => [...this.pageResults(), ...this.globalResults()]);

  constructor() {
    effect(() => {
      const context = this.context();
      this.localQuery.set('');
      context?.onSearchQueryChange?.('');
    });
    afterNextRender(() => {
      this.measureCapsule();
      const element = this.capsule()?.nativeElement;
      if (!element || typeof ResizeObserver === 'undefined') return;
      const observer = new ResizeObserver(() => this.measureCapsule());
      observer.observe(element);
      this.destroyRef.onDestroy(() => observer.disconnect());
    });
  }

  focusSearch(): void {
    this.searchField()?.focusSearch();
  }

  openPanel(): void {
    this.panelOpen.set(true);
    this.activeIndex.set(0);
    this.measureCapsule();
  }

  closePanel(): void {
    this.panelOpen.set(false);
  }

  hasOpenPanel(): boolean {
    return this.panelOpen();
  }

  protected updateQuery(value: string): void {
    this.localQuery.set(value);
    this.context()?.onSearchQueryChange?.(value);
    this.activeIndex.set(0);
    this.openPanel();
  }

  protected runAction(run: () => void): void {
    this.closePanel();
    run();
  }

  protected openFilters(): void {
    this.closePanel();
    const filter = this.context()?.filter;
    if (filter && (!filter.visibleWhen || filter.visibleWhen())) filter.toggle();
  }

  protected activeItemId(): string | null {
    return this.panelOpen() &&
      this.allResults().length &&
      this.activeIndex() >= 0 &&
      this.activeIndex() < this.allResults().length
      ? `global-search-option-${this.activeIndex()}`
      : null;
  }

  protected onSearchKeydown(event: KeyboardEvent): void {
    if (event.key === 'Tab' && this.panelOpen()) {
      this.closePanel();
      return;
    }
    if (event.key === 'Escape' && this.panelOpen()) {
      event.preventDefault();
      this.closePanel();
      return;
    }
    this.moveActiveResult(event);
  }

  protected onOverlayKeydown(event: KeyboardEvent): void {
    if (event.key === 'Tab') {
      this.closePanel();
      return;
    }
    if (event.key === 'Escape') {
      event.preventDefault();
      this.closePanel();
      this.focusSearch();
      return;
    }
    this.moveActiveResult(event);
  }

  private moveActiveResult(event: KeyboardEvent): void {
    const count = this.allResults().length;
    if (!count) return;
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      this.activeIndex.update((index) => (index + 1) % count);
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      this.activeIndex.update((index) => (index - 1 + count) % count);
    } else if (event.key === 'Enter') {
      const item = this.allResults()[this.activeIndex()];
      if (!item) return;
      event.preventDefault();
      void this.activate(item);
    }
  }

  protected async activate(item: SearchEntry): Promise<void> {
    this.closePanel();
    if (item.run) {
      item.run();
      return;
    }
    if (item.path) await this.router.navigateByUrl(item.path);
  }

  protected measureCapsule(): void {
    const element = this.capsule()?.nativeElement;
    if (!element) return;
    const rect = element.getBoundingClientRect();
    this.panelWidth.set(rect.width);
    if (this.panelOpen()) {
      document.documentElement.style.setProperty(
        '--global-search-sheet-top',
        `${Math.round(rect.bottom)}px`,
      );
    }
  }

  private normalize(value: string): string {
    return value
      .normalize('NFD')
      .replace(/[\u0300-\u036f]/g, '')
      .toLocaleLowerCase('fr-FR');
  }
}
