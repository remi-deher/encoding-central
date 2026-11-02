import { selectionNavigationIndex } from './selection-navigation';
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

export interface SubnavOption {
  readonly value: string;
  readonly label: string;
  /** Requis en mode `links` ; absent pour les vues locales de la page. */
  readonly route?: string;
  readonly count?: number;
}

/** Navigation de second niveau commune : liens entre routes ou vues locales. */
@Component({
  selector: 'app-subnav',
  host: {
    '[class.subnav-capsule]': "presentation() === 'capsule'",
  },
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    <div class="page-subnav">
      @if (variant() === 'links') {
        <nav class="subnav-items" [attr.aria-label]="label()">
          @for (option of options(); track option.value; let index = $index) {
            @if (option.route) {
              <a
                #subnavItem
                class="subnav-item"
                [class.is-active]="option.value === selected()"
                [routerLink]="option.route"
                [attr.aria-current]="option.value === selected() ? 'page' : null"
              >
                <span>{{ option.label }}</span>
                @if (option.count !== undefined) {
                  <span class="subnav-count">{{ option.count }}</span>
                }
              </a>
            }
          }
        </nav>
      } @else {
        <div
          class="subnav-items"
          role="tablist"
          [attr.aria-label]="label()"
          (keydown)="onKeydown($event, focusedIndex($event))"
        >
          @for (option of options(); track option.value; let index = $index) {
            <button
              #subnavItem
              type="button"
              class="subnav-item"
              role="tab"
              [id]="panelId() ? tabId(option.value) : null"
              [attr.aria-controls]="panelId() || null"
              [class.is-active]="option.value === selected()"
              [attr.aria-selected]="option.value === selected()"
              [attr.tabindex]="option.value === selected() ? 0 : -1"
              (click)="selectedChange.emit(option.value)"
            >
              <span>{{ option.label }}</span>
              @if (option.count !== undefined) {
                <span class="subnav-count">{{ option.count }}</span>
              }
            </button>
          }
        </div>
      }
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

  protected focusedIndex(event: KeyboardEvent): number {
    return this.items().findIndex((item) => item.nativeElement === event.target);
  }

  protected onKeydown(event: KeyboardEvent, index: number): void {
    const options = this.options();
    if (index < 0 || !options.length) return;

    const nextIndex = selectionNavigationIndex(event.key, index, options.length);

    if (nextIndex >= 0) {
      event.preventDefault();
      const option = options[nextIndex];
      if (option) {
        this.selectedChange.emit(option.value);
        this.items()[nextIndex]?.nativeElement.focus();
      }
    }
  }
}
