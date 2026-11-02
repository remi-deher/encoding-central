import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import {
  MatDialogActions,
  MatDialogContent,
  MatDialogRef,
  MAT_DIALOG_DATA,
} from '@angular/material/dialog';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { DialogFrameComponent } from './dialog-frame.component';
import { NodeMode, NodeModeDialogData } from '../models/node-mode';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-node-mode-dialog',
  imports: [
    DialogFrameComponent,
    MatButtonModule,
    MatDialogActions,
    MatDialogContent,
    MatSlideToggleModule,
  ],
  template: `
    <app-dialog-frame
      eyebrow="Fonctions du nœud"
      icon="◈"
      [heading]="
        data.remoteNodeName ? 'Mode de « ' + data.remoteNodeName + ' »' : 'Mode de ce nœud'
      "
    >
      <mat-dialog-content class="app-dialog-body">
        @if (data.remoteNodeName) {
          <p class="note" role="note">
            Le nœud applique ce mode à son prochain signal de présence, à condition d’être géré par
            l’image unique. Il garde son mode actuel tant qu’il est hors ligne.
          </p>
        } @else if (!data.managed) {
          <p class="note" role="note">
            Ce nœud n’est pas géré par l’image unique : son mode se règle par la variable
            <code>Node__Roles</code> du déploiement.
          </p>
        }
        <mat-slide-toggle
          [checked]="server()"
          [disabled]="!data.managed"
          (change)="server.set($event.checked)"
        >
          Fonctions serveur
        </mat-slide-toggle>
        <p class="hint">Interface web, API, configuration partagée et orchestration.</p>
        <mat-slide-toggle
          [checked]="worker()"
          [disabled]="!data.managed"
          (change)="worker.set($event.checked)"
        >
          Fonctions worker
        </mat-slide-toggle>
        <p class="hint">Exécution des tâches de traitement avec les outils de ce nœud.</p>
        @if (!valid()) {
          <p class="error" role="alert">
            Gardez au moins une fonction : sans serveur ni worker, le nœud ne ferait plus partie du
            cluster.
          </p>
        }
        @if (data.managed && !data.remoteNodeName && data.server && !server()) {
          <p class="note" role="note">
            Le serveur de ce nœud s’arrêtera : cette interface cessera de répondre. Rétablissez-le
            depuis la page de ce nœud.
          </p>
        }
      </mat-dialog-content>
      <mat-dialog-actions class="app-dialog-footer" align="end">
        <button matButton="text" type="button" (click)="dialogRef.close()">Annuler</button>
        <button
          matButton="filled"
          type="button"
          [disabled]="!data.managed || !valid() || !changed()"
          (click)="dialogRef.close(selection())"
        >
          Appliquer
        </button>
      </mat-dialog-actions>
    </app-dialog-frame>
  `,
  styles: `
    mat-dialog-content {
      display: grid;
      gap: var(--space-1);
      color: var(--text-secondary);
      line-height: 1.55;
    }
    .hint {
      margin: 0 0 var(--space-3) var(--space-6);
      font-size: var(--font-body);
    }
    .note {
      margin: 0 0 var(--space-3);
      padding: var(--space-3);
      border-radius: var(--radius-md);
      background: var(--surface-muted);
    }
    .error {
      margin: 0;
      color: var(--danger-strong);
    }
  `,
})
export class NodeModeDialogComponent {
  protected readonly data = inject<NodeModeDialogData>(MAT_DIALOG_DATA);
  protected readonly dialogRef = inject(MatDialogRef<NodeModeDialogComponent, NodeMode>);
  protected readonly server = signal(this.data.server);
  protected readonly worker = signal(this.data.worker);
  protected readonly selection = computed<NodeMode>(() => ({
    server: this.server(),
    worker: this.worker(),
  }));
  protected readonly valid = computed(() => this.server() || this.worker());
  protected readonly changed = computed(
    () => this.server() !== this.data.server || this.worker() !== this.data.worker,
  );

  constructor() {
    this.dialogRef.addPanelClass('app-dialog-panel');
    this.dialogRef.updateSize('480px');
  }
}
