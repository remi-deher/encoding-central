import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { MatDialogTitle } from '@angular/material/dialog';
import { IconComponent, IconName } from './icon.component';

export type DialogVariant = 'neutral' | 'danger' | 'info';

const VARIANT_ICONS: Record<DialogVariant, IconName> = {
  neutral: 'diamond',
  danger: 'error',
  info: 'info',
};

/**
 * Gabarit commun des modales : bandeau sémantique (icône, intitulé, titre), corps projeté et
 * actions projetées avec `dialogActions`. Le style vit dans `styles.scss` (`.app-dialog*`).
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-dialog-frame',
  imports: [IconComponent, MatDialogTitle],
  host: { class: 'app-dialog-frame', style: 'display: contents' },
  template: `
    <section
      class="app-dialog"
      [class.app-dialog_danger]="variant() === 'danger'"
      [class.app-dialog_info]="variant() === 'info'"
    >
      <header class="app-dialog-header">
        <span class="app-dialog-icon" aria-hidden="true">
          <app-icon [name]="glyph()" />
        </span>
        <div class="app-dialog-heading">
          @if (eyebrow()) {
            <p class="app-dialog-eyebrow">{{ eyebrow() }}</p>
          }
          <h2 mat-dialog-title>{{ heading() }}</h2>
        </div>
      </header>
      <ng-content />
    </section>
  `,
})
export class DialogFrameComponent {
  readonly heading = input.required<string>();
  readonly variant = input<DialogVariant>('neutral');
  readonly eyebrow = input<string>();
  /** Icône du registre `app-icon` ; par défaut celle de la variante. */
  readonly icon = input<IconName>();
  protected readonly glyph = computed(() => this.icon() ?? VARIANT_ICONS[this.variant()]);
}
