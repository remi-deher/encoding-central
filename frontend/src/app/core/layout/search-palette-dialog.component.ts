import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { AuthService } from '../auth/auth.service';
import {
  getSearchableNavigationItems,
  NavigationItem,
} from '../navigation/primary-navigation.component';

@Component({
  selector: 'app-search-palette-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatDialogModule],
  template: `
    <div class="palette-surface" role="search" aria-label="Recherche rapide">
      <div class="palette-input-bar">
        <svg viewBox="0 0 24 24" class="palette-search-icon" aria-hidden="true">
          <circle cx="10.8" cy="10.8" r="6.8" />
          <path d="m16 16 5 5" />
        </svg>
        <input
          type="search"
          role="combobox"
          aria-label="Rechercher une destination"
          aria-autocomplete="list"
          aria-controls="palette-results-listbox"
          [attr.aria-expanded]="results().length > 0"
          [attr.aria-activedescendant]="activeItemId()"
          autocomplete="off"
          placeholder="Rechercher une page, une librairie ou une section…"
          [value]="query()"
          (input)="onInput($event)"
          (keydown)="onKeydown($event)"
        />
        <button
          type="button"
          class="palette-esc-badge"
          aria-label="Fermer la recherche (Échap)"
          (click)="close()"
        >
          Échap
        </button>
      </div>

      <div
        id="palette-results-listbox"
        class="palette-results"
        role="listbox"
        aria-label="Destinations disponibles"
      >
        @for (item of results(); track item.path; let index = $index) {
          <button
            type="button"
            class="palette-result-item"
            role="option"
            [id]="'palette-option-' + index"
            [class.is-active]="index === activeIndex()"
            [attr.aria-selected]="index === activeIndex()"
            (mouseenter)="activeIndex.set(index)"
            (click)="navigateTo(item)"
          >
            <span class="palette-result-glyph" aria-hidden="true">{{ item.glyph }}</span>
            <span class="palette-result-main">
              <span class="palette-result-label">{{ item.label }}</span>
              @if (item.group) {
                <span class="palette-result-group">{{ item.group }}</span>
              }
            </span>
            <span class="palette-result-enter" aria-hidden="true">↵</span>
          </button>
        } @empty {
          <p class="palette-empty" role="status">
            Aucune destination ne correspond à « {{ query() }} ».
          </p>
        }
      </div>
    </div>
  `,
  styles: `
    .palette-surface {
      display: flex;
      flex-direction: column;
      background: var(--surface-container-lowest);
      color: var(--text-primary);
      overflow: hidden;
    }

    .palette-input-bar {
      display: flex;
      align-items: center;
      gap: 12px;
      padding: 16px 20px;
      background: var(--surface-container-low);
      border-bottom: 1px solid var(--border-subtle);

      svg {
        width: 20px;
        height: 20px;
        flex: 0 0 20px;
        stroke: var(--accent-strong);
        fill: none;
        stroke-width: 2;
      }

      input {
        flex: 1;
        min-width: 0;
        border: none;
        outline: none;
        background: transparent;
        font-size: var(--font-title-sm);
        font-family: inherit;
        color: var(--text-primary);

        &::placeholder {
          color: var(--text-muted);
        }
      }
    }

    .palette-esc-badge {
      min-height: 28px;
      padding: 3px 9px;
      border: 1px solid var(--border-subtle);
      border-radius: var(--radius-sm);
      background: var(--surface-container);
      font-family: inherit;
      font-size: var(--font-label);
      font-weight: 600;
      color: var(--text-secondary);
      cursor: pointer;
      transition:
        background-color 140ms ease,
        color 140ms ease;

      &:hover,
      &:focus-visible {
        background: var(--surface-container-high);
        color: var(--text-primary);
        outline: 2px solid var(--accent);
        outline-offset: 1px;
      }
    }

    .palette-results {
      max-height: 360px;
      overflow-y: auto;
      padding: 8px;
      display: flex;
      flex-direction: column;
      gap: 3px;
    }

    .palette-result-item {
      width: 100%;
      box-sizing: border-box;
      display: flex;
      align-items: center;
      gap: 12px;
      padding: 10px 14px;
      border: none;
      border-radius: var(--radius-lg);
      background: transparent;
      text-align: left;
      font-family: inherit;
      color: var(--text-primary);
      cursor: pointer;
      transition: background-color 140ms var(--motion-spring-fast);

      &.is-active,
      &:hover {
        background: var(--accent-soft);
        color: var(--accent-strong);
      }
    }

    .palette-result-glyph {
      width: 30px;
      height: 30px;
      flex: 0 0 30px;
      display: grid;
      place-items: center;
      border-radius: var(--radius-md);
      background: var(--surface-container);
      color: var(--accent-strong);
      font-size: 15px;
    }

    .palette-result-item.is-active .palette-result-glyph {
      background: var(--surface-container-lowest);
    }

    .palette-result-main {
      min-width: 0;
      flex: 1;
      display: flex;
      align-items: baseline;
      gap: 8px;
    }

    .palette-result-label {
      font-size: var(--font-body);
      font-weight: 650;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    .palette-result-group {
      font-size: var(--font-label);
      color: var(--text-muted);
      white-space: nowrap;
    }

    .palette-result-enter {
      font-size: 13px;
      color: var(--text-muted);
    }

    .palette-empty {
      margin: 0;
      padding: 28px 20px;
      text-align: center;
      color: var(--text-muted);
      font-size: var(--font-body);
    }
  `,
})
export class SearchPaletteDialogComponent {
  private readonly router = inject(Router);
  private readonly auth = inject(AuthService);
  private readonly dialogRef = inject(MatDialogRef<SearchPaletteDialogComponent>);

  protected readonly query = signal('');
  protected readonly activeIndex = signal(0);

  private readonly allItems = () =>
    getSearchableNavigationItems(this.router.config).filter(
      (item) => !item.adminOnly || this.auth.isAdmin(),
    );

  protected readonly results = () => {
    const normalized = this.normalize(this.query().trim());
    const items = this.allItems();
    if (!normalized) {
      return items.slice(0, 8);
    }
    return items
      .filter((item) =>
        this.normalize([item.label, item.group ?? '', ...(item.keywords ?? [])].join(' ')).includes(
          normalized,
        ),
      )
      .slice(0, 8);
  };

  protected readonly activeItemId = () => {
    const list = this.results();
    const idx = this.activeIndex();
    return list.length > 0 && idx >= 0 && idx < list.length ? `palette-option-${idx}` : null;
  };

  protected onInput(event: Event): void {
    this.query.set((event.target as HTMLInputElement).value);
    this.activeIndex.set(0);
  }

  protected onKeydown(event: KeyboardEvent): void {
    const list = this.results();
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      if (list.length) {
        this.activeIndex.update((i) => (i + 1) % list.length);
      }
      return;
    }
    if (event.key === 'ArrowUp') {
      event.preventDefault();
      if (list.length) {
        this.activeIndex.update((i) => (i - 1 + list.length) % list.length);
      }
      return;
    }
    if (event.key === 'Enter') {
      const target = list[this.activeIndex()] ?? list[0];
      if (target) {
        event.preventDefault();
        this.navigateTo(target);
      }
    }
  }

  protected navigateTo(item: NavigationItem): void {
    this.dialogRef.close();
    void this.router.navigateByUrl(item.path);
  }

  protected close(): void {
    this.dialogRef.close();
  }

  private normalize(value: string): string {
    return value
      .normalize('NFD')
      .replace(/[\u0300-\u036f]/g, '')
      .toLocaleLowerCase('fr');
  }
}
