import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  inject,
  input,
  model,
  output,
} from '@angular/core';

export type SearchFieldVariant = 'bar' | 'filled' | 'compact' | 'global';

let nextPanelId = 0;

/**
 * Champ de recherche Material 3 : barre (catalogues), rempli (panneaux), compact (barres d'outils), global (shell).
 * Avec `filterCount`, un bouton « Filtres » ouvre un panneau soudé à la barre (contenu projeté
 * via `[search-filters]`).
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-search-field',
  host: {
    '[class.has-filters-open]': 'filtersOpen()',
    '(document:click)': 'onDocumentClick($event)',
    '(keydown.escape)': 'closeFilters()',
  },
  template: `
    <div [class]="'search-field search-field_' + variant()">
      <div class="search-field-control">
        <svg class="search-field-icon" viewBox="0 0 24 24" aria-hidden="true">
          <circle cx="10.8" cy="10.8" r="6.3" />
          <path d="m15.5 15.5 5 5" />
        </svg>
        <input
          #field
          type="search"
          [attr.role]="inputRole()"
          [attr.aria-autocomplete]="ariaAutocomplete()"
          [attr.aria-controls]="ariaControls()"
          [attr.aria-expanded]="ariaExpanded()"
          [attr.aria-activedescendant]="activeDescendant()"
          autocomplete="off"
          [attr.name]="name() || null"
          [attr.aria-label]="label()"
          [attr.placeholder]="placeholder() || label()"
          [value]="value()"
          (focus)="focused.emit()"
          (input)="valueChange.emit(field.value)"
          (keydown.escape)="onEscape($event, field)"
        />
        @if (hasValue()) {
          <button
            type="button"
            class="search-field-clear"
            aria-label="Effacer la recherche"
            (click)="clear(field)"
          >
            <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M6 6l12 12M18 6 6 18" /></svg>
          </button>
        }
      </div>
      <ng-content select="[search-actions]" />
      @if (filterCount() !== null) {
        <button
          type="button"
          class="search-field-filters"
          [class.has-previous-segment]="filterHasPreviousSegment()"
          [attr.aria-controls]="filterExternallyManaged() ? null : panelId"
          [attr.aria-expanded]="filterExternallyManaged() ? null : filtersOpen()"
          (click)="onFilterButtonClick()"
        >
          @if (variant() === 'global') {
            <svg viewBox="0 0 24 24" aria-hidden="true">
              <path d="M4 6h16M7 12h10m-7 6h4" />
            </svg>
          }
          <span>{{ filterLabel() }}</span>
          @if (filterCount()) {
            <b>{{ filterCount() }}</b>
          }
        </button>
      }
    </div>
    @if (filterCount() !== null && !filterExternallyManaged()) {
      <div
        [id]="panelId"
        class="search-field-panel"
        role="group"
        aria-label="Filtres"
        [class.is-open]="filtersOpen()"
        [attr.inert]="filtersOpen() ? null : ''"
      >
        <ng-content select="[search-filters]" />
      </div>
    }
  `,
  styles: `
    :host {
      display: block;
      position: relative;
      min-width: 0;
    }
    .search-field {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      width: 100%;
      box-sizing: border-box;
      color: var(--text-muted);
      cursor: text;
      transition:
        background-color 0.15s ease,
        box-shadow 0.15s ease,
        color 0.15s ease;
    }
    .search-field-control {
      min-width: 0;
      flex: 1 1 auto;
      display: flex;
      align-items: center;
      gap: var(--space-2);
    }
    .search-field svg {
      flex: none;
      width: 22px;
      height: 22px;
      fill: none;
      stroke: currentColor;
      stroke-width: 2;
      stroke-linecap: round;
    }
    input {
      flex: 1;
      min-width: 0;
      border: 0;
      outline: 0;
      background: transparent;
      color: var(--text-primary);
      font: inherit;
    }
    input::placeholder {
      color: var(--text-muted);
    }
    input::-webkit-search-cancel-button {
      display: none;
    }
    .search-field-clear {
      display: grid;
      flex: none;
      width: 36px;
      height: 36px;
      place-items: center;
      border: 0;
      border-radius: 50%;
      background: transparent;
      color: inherit;
      cursor: pointer;
    }
    .search-field-clear:hover {
      background: var(--surface-container-highest);
    }
    .search-field-clear:focus-visible {
      outline: 2px solid var(--accent);
    }
    .search-field-clear svg {
      width: 18px;
      height: 18px;
    }

    .search-field_bar {
      min-height: 56px;
      padding: 0 8px 0 16px;
      border-radius: 28px;
      background: var(--surface-container-high);
      font-size: 16px;
    }
    .search-field_bar:hover {
      background: var(--surface-container-highest);
    }
    .search-field_bar:focus-within {
      background: var(--surface-container-high);
      box-shadow: 0 1px 3px rgba(0, 0, 0, 0.25);
    }

    .search-field-filters {
      display: inline-flex;
      flex: none;
      align-items: center;
      gap: 6px;
      margin-inline-end: 2px;
      padding: 8px 14px;
      border: 0;
      border-radius: 20px;
      background: var(--surface-container-highest);
      color: var(--text-primary);
      font: inherit;
      font-size: 14px;
      cursor: pointer;
    }
    .search-field-filters:focus-visible {
      outline: 2px solid var(--accent);
    }
    .search-field-filters b {
      padding: 0 7px;
      border-radius: 99px;
      background: var(--accent);
      color: var(--surface);
      font-size: 12px;
    }
    .search-field-panel {
      position: absolute;
      z-index: 20;
      top: 100%;
      right: 0;
      left: 0;
      box-sizing: border-box;
      max-height: 0;
      padding: 0 18px;
      overflow: hidden;
      border: 1px solid var(--accent);
      border-top: 0;
      border-radius: 0 0 28px 28px;
      background: var(--surface);
      box-shadow: 0 24px 40px rgba(0, 0, 0, 0.25);
      opacity: 0;
      pointer-events: none;
      transform: translateY(-8px);
      visibility: hidden;
      transition:
        max-height 220ms cubic-bezier(0.2, 0, 0, 1),
        opacity 160ms ease,
        transform 220ms cubic-bezier(0.2, 0, 0, 1),
        padding 220ms ease,
        visibility 0s linear 220ms;
    }
    .search-field-panel.is-open {
      max-height: min(70dvh, 420px);
      padding: 14px 18px 18px;
      overflow: auto;
      opacity: 1;
      pointer-events: auto;
      transform: none;
      visibility: visible;
      transition-delay: 0s;
    }
    :host(.has-filters-open) .search-field_bar {
      border-bottom-left-radius: 0;
      border-bottom-right-radius: 0;
      box-shadow: inset 0 0 0 1px var(--accent);
    }
    @media (prefers-reduced-motion: reduce) {
      .search-field-panel,
      .search-field {
        transition: none;
      }
    }

    .search-field_filled {
      min-height: 48px;
      padding: 0 6px 0 14px;
      border-bottom: 2px solid transparent;
      border-radius: 12px;
      background: var(--surface-container);
      font-size: 15px;
    }
    .search-field_filled:focus-within {
      border-bottom-color: var(--accent);
      background: var(--surface-container-high);
    }

    .search-field_compact {
      min-height: 40px;
      padding: 0 4px 0 12px;
      border-radius: 20px;
      background: var(--surface-container);
      font-size: 14px;
    }
    .search-field_compact svg {
      width: 20px;
      height: 20px;
    }
    .search-field_compact .search-field-clear {
      width: 30px;
      height: 30px;
    }
    .search-field_compact:focus-within {
      background: var(--accent-soft);
      color: var(--accent);
    }

    .search-field_global {
      min-height: 50px;
      padding: 0;
      border-radius: 0;
      background: transparent;
      font-size: var(--font-body);
    }
    .search-field_global:hover,
    .search-field_global:focus-within {
      background: transparent;
      box-shadow: none;
    }
    .search-field_global .search-field-icon {
      width: 20px;
      height: 20px;
      margin-left: var(--space-4);
      color: var(--accent-strong);
    }
    .search-field_global .search-field-filters {
      min-height: 50px;
      margin: 0;
      padding: 0 var(--space-3);
      border-left: 0;
      border-radius: 0;
      background: transparent;
      color: var(--text-secondary);
      font: inherit;
      font-weight: 650;
    }
    .search-field_global .search-field-filters:hover {
      background: color-mix(in srgb, var(--accent) 10%, transparent);
      color: var(--accent-strong);
    }
    .search-field_global .search-field-filters.has-previous-segment {
      border-left: 1px solid var(--border-subtle);
    }
    .search-field_global .search-field-filters:focus-visible {
      outline: 2px solid var(--accent-strong);
      outline-offset: -3px;
    }
    .search-field_global .search-field-filters svg {
      width: 19px;
      height: 19px;
      fill: none;
      stroke: currentColor;
      stroke-width: 1.8;
      stroke-linecap: round;
      stroke-linejoin: round;
    }
    .search-field_global .search-field-filters b {
      min-width: 20px;
      height: 20px;
      padding-inline: 5px;
      display: inline-grid;
      place-items: center;
      border-radius: var(--radius-pill);
      background: var(--accent-soft);
      color: var(--accent-strong);
      font-size: var(--font-label);
      font-variant-numeric: tabular-nums;
    }
    @media (max-width: 760px) {
      .search-field_global {
        flex-wrap: wrap;
        gap: 0;
      }
      .search-field_global .search-field-control {
        min-height: 46px;
        flex: 1 0 100%;
        border-bottom: 1px solid var(--border-subtle);
      }
      .search-field_global .search-action-button,
      .search-field_global .search-field-filters {
        min-height: 44px;
        min-width: 0;
        flex: 1 1 auto;
      }
      .search-field_global .search-field-filters {
        border-left: 0;
      }
      .search-field_global .search-action-button.has-divider + .search-action-button.has-divider,
      .search-field_global .search-field-filters.has-previous-segment {
        border-left: 1px solid var(--border-subtle);
      }
    }
  `,
})
export class SearchFieldComponent {
  readonly value = input('');
  readonly label = input.required<string>();
  readonly placeholder = input('');
  readonly name = input('');
  readonly variant = input<SearchFieldVariant>('filled');
  /** `null` : pas de bouton Filtres. Un nombre (0 compris) l'affiche, avec un badge au-dessus de 0. */
  readonly filterCount = input<number | null>(null);
  readonly filterLabel = input('Filtres');
  readonly inputRole = input<string | null>(null);
  readonly ariaAutocomplete = input<string | null>(null);
  readonly ariaControls = input<string | null>(null);
  readonly ariaExpanded = input<boolean | null>(null);
  readonly activeDescendant = input<string | null>(null);
  readonly filterExternallyManaged = input(false);
  readonly filterHasPreviousSegment = input(false);
  readonly filtersOpen = model(false);
  readonly focused = output<void>();
  readonly filterAction = output<void>();
  readonly valueChange = output<string>();

  protected readonly panelId = `search-field-panel-${nextPanelId++}`;
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  protected readonly hasValue = computed(() => this.value().length > 0);

  protected clear(field: HTMLInputElement): void {
    field.value = '';
    this.valueChange.emit('');
    field.focus();
  }

  focusSearch(): void {
    this.host.nativeElement.querySelector<HTMLInputElement>('input')?.focus();
  }

  protected onFilterButtonClick(): void {
    if (this.filterExternallyManaged()) {
      this.filterAction.emit();
      return;
    }
    this.filtersOpen.update((open) => !open);
  }

  protected onEscape(event: Event, field: HTMLInputElement): void {
    if (!field.value) return;
    event.stopPropagation();
    this.clear(field);
  }

  protected closeFilters(): void {
    if (this.filtersOpen()) this.filtersOpen.set(false);
  }

  protected onDocumentClick(event: Event): void {
    const target = event.target as Element;
    // Les listes déroulantes Material vivent dans l'overlay, hors du composant.
    if (
      this.filtersOpen() &&
      !this.host.nativeElement.contains(target) &&
      !target.closest?.('.cdk-overlay-container')
    ) {
      this.filtersOpen.set(false);
    }
  }
}
