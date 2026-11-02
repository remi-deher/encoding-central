import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { RecordFormComponent } from './record-form.component';

/** Boutons d'un `RecordFormComponent` embarqué, à placer dans le pied du tiroir. */
@Component({
  selector: 'app-record-form-actions',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule],
  host: { style: 'display: contents' },
  template: `
    @if (form(); as current) {
      @if (current.secondaryLabel(); as label) {
        <button matButton="text" type="button" (click)="current.secondary()">{{ label }}</button>
      }
      <button
        matButton="filled"
        type="button"
        [disabled]="current.busy()"
        (click)="current.primary()"
      >
        {{ current.primaryLabel() }}
      </button>
    }
  `,
})
export class RecordFormActionsComponent {
  readonly form = input<RecordFormComponent | undefined>(undefined);
}
