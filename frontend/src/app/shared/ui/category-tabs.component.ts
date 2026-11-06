import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  effect,
  input,
  output,
  viewChildren,
} from '@angular/core';
import { MatChipListboxChange, MatChipsModule } from '@angular/material/chips';

export interface CategoryTabOption {
  readonly value: string;
  readonly label: string;
  readonly count?: number;
}

/** Filtres par catégorie, rendus en `mat-chip-listbox` (sélection unique). */
@Component({
  selector: 'app-category-tabs',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatChipsModule],
  template: `
    <mat-chip-listbox
      class="category-tabs"
      [class.is-underline]="variant() === 'underline'"
      [attr.aria-label]="label()"
      [value]="selected()"
      hideSingleSelectionIndicator
      (change)="onSelectionChange($event)"
    >
      @for (option of options(); track option.value) {
        <mat-chip-option #tabButton class="category-tab-pill" [value]="option.value">
          <span class="tab-label">{{ option.label }}</span>
          @if (option.count !== undefined) {
            <span class="option-count">{{ option.count }}</span>
          }
        </mat-chip-option>
      }
    </mat-chip-listbox>
  `,
  styleUrl: './category-tabs.component.scss',
})
export class CategoryTabsComponent {
  readonly label = input.required<string>();
  readonly options = input.required<readonly CategoryTabOption[]>();
  readonly selected = input.required<string>();
  readonly variant = input<'pill' | 'underline'>('pill');
  readonly selectedChange = output<string>();

  private readonly tabButtons = viewChildren<ElementRef<HTMLElement>>('tabButton', {
    read: ElementRef,
  });

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

  protected onSelectionChange(event: MatChipListboxChange): void {
    if (event.value !== this.selected() && event.value != null) {
      this.selectedChange.emit(event.value);
    }
  }
}
