import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import {
  MAT_DIALOG_DATA,
  MatDialogActions,
  MatDialogContent,
  MatDialogRef,
} from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { DialogFrameComponent, DialogVariant } from './dialog-frame.component';

export interface ConfirmationDialogData {
  readonly title: string;
  readonly eyebrow?: string;
  readonly message?: string;
  readonly confirmLabel?: string;
  /** `danger` pour une action destructive ou irréversible ; `info` est déduit du mode d'information. */
  readonly variant?: DialogVariant;
  /** Présentation resserrée pour les confirmations liées aux interrupteurs. */
  readonly compact?: boolean;
  readonly mode?: 'confirmation' | 'information';
  readonly closeLabel?: string;
  readonly items?: readonly { readonly label: string; readonly detail?: string }[];
  readonly emptyMessage?: string;
}

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-confirmation-dialog',
  imports: [DialogFrameComponent, MatButtonModule, MatDialogActions, MatDialogContent],
  template: `
    <app-dialog-frame
      [variant]="variant"
      [eyebrow]="data.eyebrow ?? (data.mode === 'information' ? 'Information' : 'Confirmation')"
      [heading]="data.title"
      [icon]="data.mode === 'information' ? 'i' : variant === 'danger' ? '!' : '?'"
    >
      <mat-dialog-content class="app-dialog-body confirmation-dialog-content">
        @if (data.message) {
          <p class="dialog-message">{{ data.message }}</p>
        }
        @if (data.items?.length) {
          <ul class="information-list">
            @for (item of data.items; track $index) {
              <li>
                <strong>{{ item.label }}</strong>
                @if (item.detail) {
                  <span>{{ item.detail }}</span>
                }
              </li>
            }
          </ul>
        } @else if (data.emptyMessage) {
          <p class="empty-message">{{ data.emptyMessage }}</p>
        }
      </mat-dialog-content>

      <mat-dialog-actions class="app-dialog-footer" align="end">
        @if (data.mode === 'information') {
          <button matButton="filled" type="button" (click)="dialogRef.close(true)">
            {{ data.closeLabel ?? 'Fermer' }}
          </button>
        } @else {
          <button matButton="text" type="button" (click)="dialogRef.close(false)">Annuler</button>
          <button matButton="filled" type="button" (click)="dialogRef.close(true)">
            {{ data.confirmLabel ?? 'Confirmer' }}
          </button>
        }
      </mat-dialog-actions>
    </app-dialog-frame>
  `,
  styles: `
    .confirmation-dialog-content {
      line-height: 1.6;
    }

    .dialog-message {
      margin: 0;
      color: var(--text-secondary);
    }

    .information-list {
      margin: var(--space-3) 0 0;
      padding: 0;
      display: grid;
      gap: var(--space-2);
      list-style: none;
    }

    .information-list li {
      padding: 10px var(--space-3);
      display: grid;
      gap: 3px;
      border: 1px solid var(--border-subtle);
      border-radius: var(--radius-md);
      background: var(--surface-muted);
    }

    .information-list strong {
      color: var(--text-primary);
    }

    .information-list span,
    .empty-message {
      color: var(--text-secondary);
    }

    .information-list span {
      overflow-wrap: anywhere;
    }

    .empty-message {
      margin: 0;
    }
  `,
})
export class ConfirmationDialogComponent {
  protected readonly data = inject<ConfirmationDialogData>(MAT_DIALOG_DATA);
  protected readonly dialogRef = inject(MatDialogRef<ConfirmationDialogComponent, boolean>);
  protected readonly variant: DialogVariant =
    this.data.variant ?? (this.data.mode === 'information' ? 'info' : 'neutral');

  constructor() {
    this.dialogRef.addPanelClass([
      'app-dialog-panel',
      'confirmation-dialog-panel',
      ...(this.data.compact ? ['compact-confirmation-dialog-panel'] : []),
    ]);
    this.dialogRef.updateSize(this.data.compact ? '400px' : '480px');
  }
}
