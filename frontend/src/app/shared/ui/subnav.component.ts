import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  effect,
  input,
  output,
  viewChildren,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatTabLink, MatTabNav } from '@angular/material/tabs';

export interface SubnavOption {
  readonly value: string;
  readonly label: string;
  /** Requis en mode `links` ; absent pour les vues locales de la page. */
  readonly route?: string;
  readonly count?: number;
}

/**
 * Navigation de second niveau commune : liens entre routes ou vues locales,
 * rendus en `mat-tab-nav-bar` (navigation clavier native de Material).
 */
@Component({
  selector: 'app-subnav',
  host: {
    '[class.subnav-capsule]': "presentation() === 'capsule'",
  },
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, MatTabNav, MatTabLink],
  template: `
    <div class="page-subnav">
      <nav mat-tab-nav-bar class="subnav-items" [attr.aria-label]="label()">
        @for (option of options(); track option.value) {
          @if (variant() === 'links') {
            @if (option.route) {
              <a
                #subnavItem
                mat-tab-link
                class="subnav-item"
                [routerLink]="option.route"
                [active]="option.value === selected()"
                [attr.aria-current]="option.value === selected() ? 'page' : null"
              >
                <span>{{ option.label }}</span>
                @if (option.count !== undefined) {
                  <span class="subnav-count">{{ option.count }}</span>
                }
              </a>
            }
          } @else {
            <button
              #subnavItem
              type="button"
              mat-tab-link
              class="subnav-item"
              [id]="panelId() ? tabId(option.value) : undefined"
              [attr.aria-controls]="panelId() || null"
              [active]="option.value === selected()"
              [attr.aria-selected]="option.value === selected()"
              (click)="selectedChange.emit(option.value)"
            >
              <span>{{ option.label }}</span>
              @if (option.count !== undefined) {
                <span class="subnav-count">{{ option.count }}</span>
              }
            </button>
          }
        }
      </nav>
    </div>
  `,
  styleUrl: './subnav.component.scss',
})
export class SubnavComponent {
  readonly label = input.required<string>();
  readonly panelId = input('');
  tabId(value: string): string {
    return this.panelId() + '-tab-' + value;
  }
  readonly options = input.required<readonly SubnavOption[]>();
  readonly selected = input.required<string>();
  readonly variant = input<'links' | 'tabs'>('tabs');
  /** Présentation capsule centrée, utile pour les vues locales intégrées à une page. */
  readonly presentation = input<'default' | 'capsule'>('default');
  readonly selectedChange = output<string>();

  private readonly items = viewChildren<ElementRef<HTMLElement>>('subnavItem');

  constructor() {
    effect(() => {
      const index = this.options().findIndex((option) => option.value === this.selected());
      const active = this.items()[index]?.nativeElement;
      if (active && typeof window !== 'undefined') {
        requestAnimationFrame(() =>
          active.scrollIntoView?.({ block: 'nearest', inline: 'nearest' }),
        );
      }
    });
  }
}
