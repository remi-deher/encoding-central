import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import {
  MAT_DIALOG_DATA,
  MatDialogActions,
  MatDialogContent,
  MatDialogRef,
} from '@angular/material/dialog';
import { DialogFrameComponent } from '../../shared/ui/dialog-frame.component';

export interface FlowDeleteDialogData {
  readonly name: string;
  readonly libraries: readonly {
    readonly libraryId: string;
    readonly libraryName: string;
    readonly version: number;
    readonly isActive: boolean;
  }[];
}

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-flow-delete-dialog',
  imports: [
    DialogFrameComponent,
    MatButtonModule,
    MatCheckboxModule,
    MatDialogActions,
    MatDialogContent,
  ],
  template: `
    <app-dialog-frame
      variant="danger"
      eyebrow="Confirmation"
      [heading]="'Supprimer le flow « ' + data.name + ' » ?'"
    >
      <mat-dialog-content class="app-dialog-body">
        <p class="message">
          Le flow et son brouillon seront retirés du catalogue. Les versions publiées et
          l’historique des exécutions sont conservés. Une exécution en cours ou en attente empêche
          la suppression.
        </p>
        @if (data.libraries.length) {
          <p class="message">
            Les bibliothèques ci-dessous n’auront plus de flow : leur traitement automatique
            s’arrêtera jusqu’à l’affectation d’un autre flow.
          </p>
          <table class="usage-table">
            <caption class="visually-hidden">
              Bibliothèques utilisant ce flow
            </caption>
            <thead>
              <tr>
                <th scope="col">Bibliothèque</th>
                <th scope="col">Version</th>
                <th scope="col">État</th>
              </tr>
            </thead>
            <tbody>
              @for (library of data.libraries; track library.libraryId) {
                <tr>
                  <th scope="row">{{ library.libraryName }}</th>
                  <td>v{{ library.version }}</td>
                  <td>{{ library.isActive ? 'Active' : 'Inactive' }}</td>
                </tr>
              }
            </tbody>
          </table>
        } @else {
          <p class="message">Aucune bibliothèque n’utilise ce flow.</p>
        }
        <mat-checkbox [checked]="confirmed()" (change)="confirmed.set($event.checked)">
          @if (data.libraries.length) {
            Je confirme la suppression et le détachement de
            {{ data.libraries.length }}
            {{ data.libraries.length === 1 ? 'bibliothèque' : 'bibliothèques' }}
          } @else {
            Je confirme la suppression de ce flow
          }
        </mat-checkbox>
      </mat-dialog-content>

      <mat-dialog-actions class="app-dialog-footer" align="end">
        <button matButton="text" type="button" (click)="dialogRef.close(false)">Annuler</button>
        <button
          matButton="filled"
          type="button"
          [disabled]="!confirmed()"
          (click)="dialogRef.close(true)"
        >
          Supprimer
        </button>
      </mat-dialog-actions>
    </app-dialog-frame>
  `,
  styles: `
    .message {
      margin: 0 0 var(--space-3);
      color: var(--text-secondary);
      line-height: 1.6;
    }

    .usage-table {
      width: 100%;
      margin: 0 0 var(--space-3);
      border-collapse: collapse;
      border: 1px solid var(--border-subtle);
    }

    .usage-table th,
    .usage-table td {
      padding: var(--space-2) var(--space-3);
      border-bottom: 1px solid var(--border-subtle);
      text-align: left;
      overflow-wrap: anywhere;
    }

    .usage-table thead th {
      background: var(--surface-muted);
      color: var(--text-secondary);
    }

    .usage-table tbody th {
      color: var(--text-primary);
      font-weight: 600;
    }

    .visually-hidden {
      position: absolute;
      width: 1px;
      height: 1px;
      overflow: hidden;
      clip-path: inset(50%);
    }
  `,
})
export class FlowDeleteDialogComponent {
  protected readonly data = inject<FlowDeleteDialogData>(MAT_DIALOG_DATA);
  protected readonly dialogRef = inject(MatDialogRef<FlowDeleteDialogComponent, boolean>);
  protected readonly confirmed = signal(false);

  constructor() {
    this.dialogRef.addPanelClass(['app-dialog-panel', 'confirmation-dialog-panel']);
    this.dialogRef.updateSize('560px');
  }
}
