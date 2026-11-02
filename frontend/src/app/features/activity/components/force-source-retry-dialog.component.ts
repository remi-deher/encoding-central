import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import {
  MAT_DIALOG_DATA,
  MatDialogActions,
  MatDialogContent,
  MatDialogRef,
} from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { DialogFrameComponent } from '../../../shared/ui/dialog-frame.component';

export interface ForceSourceRetryDialogData {
  readonly media: string;
}

export interface ForceSourceRetryAttestation {
  readonly confirmedStopped: true;
  readonly reason: string;
}

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-force-source-retry-dialog',
  imports: [
    DialogFrameComponent,
    FormsModule,
    MatButtonModule,
    MatCheckboxModule,
    MatDialogActions,
    MatDialogContent,
    MatFormFieldModule,
    MatInputModule,
  ],
  template: `
    <app-dialog-frame
      variant="danger"
      eyebrow="Décision administrateur"
      heading="Forcer la reprise depuis la source ?"
    >
      <mat-dialog-content class="app-dialog-body">
        <p>
          Une nouvelle tentative pour « {{ data.media }} » repartira du fichier source après le
          délai de grâce de 135 secondes à compter de l’attestation. Cette décision neutralise le
          serveur propriétaire pour tout le cluster.
        </p>
        <p>
          N’utilisez cette action que si l’installation distante et tous ses workers sont réellement
          arrêtés. Si une publication a pu avoir lieu ou si son résultat est incertain, vérifiez le
          média avant de continuer.
        </p>
        <mat-checkbox
          [checked]="confirmedStopped()"
          (change)="confirmedStopped.set($event.checked)"
        >
          J’atteste que le serveur distant et tous ses workers sont arrêtés.
        </mat-checkbox>
        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label>Motif de la neutralisation</mat-label>
          <textarea
            matInput
            rows="3"
            maxlength="1000"
            [ngModel]="reason()"
            (ngModelChange)="reason.set($event)"
          ></textarea>
          <mat-hint
            >8 caractères minimum. Ce motif sera conservé dans le journal du cluster.</mat-hint
          >
        </mat-form-field>
      </mat-dialog-content>

      <mat-dialog-actions class="app-dialog-footer" align="end">
        <button matButton="text" type="button" (click)="dialogRef.close()">Annuler</button>
        <button matButton="filled" type="button" [disabled]="!canConfirm()" (click)="confirm()">
          Neutraliser et remettre en file
        </button>
      </mat-dialog-actions>
    </app-dialog-frame>
  `,
  styles: `
    mat-dialog-content {
      display: grid;
      gap: var(--space-3);
      line-height: 1.6;
    }

    p {
      margin: 0;
      color: var(--text-secondary);
    }

    mat-form-field {
      width: 100%;
    }
  `,
})
export class ForceSourceRetryDialogComponent {
  protected readonly data = inject<ForceSourceRetryDialogData>(MAT_DIALOG_DATA);
  protected readonly dialogRef = inject(
    MatDialogRef<ForceSourceRetryDialogComponent, ForceSourceRetryAttestation | undefined>,
  );
  protected readonly confirmedStopped = signal(false);
  protected readonly reason = signal('');
  protected readonly canConfirm = computed(() => {
    const length = this.reason().trim().length;
    return this.confirmedStopped() && length >= 8 && length <= 1000;
  });

  constructor() {
    this.dialogRef.addPanelClass(['app-dialog-panel', 'confirmation-dialog-panel']);
    this.dialogRef.updateSize('560px');
  }

  protected confirm(): void {
    if (!this.canConfirm()) return;
    this.dialogRef.close({ confirmedStopped: true, reason: this.reason().trim() });
  }
}
