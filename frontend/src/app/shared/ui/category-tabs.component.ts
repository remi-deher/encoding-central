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

export interface CategoryTabOption {
  readonly value: string;
  readonly label: string;
  readonly count?: number;
}

@Component({
  selector: 'app-category-tabs',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [],
  template: `
    <div
      class="category-tabs"
      [class.is-underline]="variant() === 'underline'"
      [attr.aria-label]="label()"
      role="group"
    >
      @for (option of options(); track option.value; let index = $index) {
        <button
          #tabButton
          type="button"
          class="category-tab-pill"
          [class.is-active]="option.value === selected()"
          [attr.aria-pressed]="option.value === selected()"
          (click)="selectedChange.emit(option.value)"
          (keydown)="onKeydown($event, index)"
        >
          <span class="tab-label">{{ option.label }}</span>
          @if (option.count !== undefined) {
            <span class="option-count">{{ option.count }}</span>
          }
        </button>
      }
    </div>
  `,
  styleUrl: './category-tabs.component.scss',
})
export class CategoryTabsComponent {
  readonly label = input.required<string>();
  readonly options = input.required<readonly CategoryTabOption[]>();
  readonly selected = input.required<string>();
  readonly variant = input<'pill' | 'underline'>('pill');
  readonly selectedChange = output<string>();

  private readonly tabButtons = viewChildren<ElementRef<HTMLButtonElement>>('tabButton');

  constructor() {
    effect(() => {
      const selected = this.selected();
      const index = this.options().findIndex((option) => option.value === selected);
      const activeTab = this.tabButtons()[index]?.nativeElement;
      if (activeTab && typeof window !== 'undefined') {
        requestAnimationFrame(() =>
          activeTab.scrollIntoView?.({ block: 'nearest', inline: 'nearest', behavior: 'auto' }),
        );
      }
    });
  }

  protected onKeydown(event: KeyboardEvent, index: number): void {
    const list = this.options();
    if (!list.length) return;

    const nextIndex = selectionNavigationIndex(event.key, index, list.length);

    if (nextIndex >= 0) {
      event.preventDefault();
      const target = list[nextIndex];
      if (target) {
        this.selectedChange.emit(target.value);
        this.tabButtons()[nextIndex]?.nativeElement.focus();
      }
    }
  }
}
