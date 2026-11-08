// Prototype d'évaluation (#30) : même périmètre que les prototypes Leptos et Dioxus, en Angular +
// Material/CDK, pour comparer le coût du framework à fonctionnalités égales (et non l'appli complète).
import { HttpClient, provideHttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTableModule } from '@angular/material/table';
import { bootstrapApplication } from '@angular/platform-browser';
import { catchError, of, startWith, Subject, switchMap } from 'rxjs';

interface Inventory { operatingSystem: string; architecture: string; processorCount: number; memoryMb: number; processorModel?: string | null; }
interface Worker { id: string; name: string; displayName?: string | null; status: string; lifecycle?: string | null; maxConcurrentTasks: number; lastSeenAt: string; inventory?: Inventory | null; }
interface Pending { id: string; name: string; expiresAt: string; verificationCode?: string | null; }

const label = (w: Worker) => w.displayName?.trim() || w.name;
const state = (w: Worker) => w.lifecycle ?? w.status;

@Component({
  selector: 'app-pairing-dialog',
  imports: [MatDialogModule, MatFormFieldModule, MatInputModule, MatButtonModule, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Rattacher un worker</h2>
    <mat-dialog-content>
      <mat-form-field>
        <mat-label>Clé de jumelage</mat-label>
        <input matInput required autocomplete="off" placeholder="ABCD-EFGH" [(ngModel)]="key" />
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Annuler</button>
      <button mat-flat-button [disabled]="data.busy()" (click)="ref.close(key)">Approuver</button>
    </mat-dialog-actions>`,
})
class PairingDialog {
  readonly ref = inject(MatDialogRef<PairingDialog, string>);
  readonly data = inject<{ busy: () => boolean }>(MAT_DIALOG_DATA);
  key = '';
}

@Component({
  selector: 'app-root',
  imports: [MatTableModule, MatButtonModule, MatCardModule, MatChipsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="page">
      <header class="page-header">
        <h1>Workers</h1>
        <button mat-flat-button (click)="openPairing()">Rattacher un worker</button>
      </header>
      <section aria-labelledby="workers-title">
        <h2 id="workers-title">Membres du central</h2>
        @if (workers(); as list) {
          <table mat-table [dataSource]="list" aria-label="Workers du central">
            <ng-container matColumnDef="name"><th mat-header-cell *matHeaderCellDef>Nom</th><td mat-cell *matCellDef="let w">{{ label(w) }}</td></ng-container>
            <ng-container matColumnDef="state"><th mat-header-cell *matHeaderCellDef>État</th><td mat-cell *matCellDef="let w"><mat-chip>{{ state(w) }}</mat-chip></td></ng-container>
            <ng-container matColumnDef="system"><th mat-header-cell *matHeaderCellDef>Système</th><td mat-cell *matCellDef="let w">{{ w.inventory ? w.inventory.operatingSystem + ' ' + w.inventory.architecture : '—' }}</td></ng-container>
            <ng-container matColumnDef="tasks"><th mat-header-cell *matHeaderCellDef>Tâches max</th><td mat-cell *matCellDef="let w">{{ w.maxConcurrentTasks }}</td></ng-container>
            <ng-container matColumnDef="action"><th mat-header-cell *matHeaderCellDef>Action</th><td mat-cell *matCellDef="let w"><button mat-button [attr.aria-label]="'Voir le détail de ' + label(w)" (click)="selected.set(w)">Détail</button></td></ng-container>
            <tr mat-header-row *matHeaderRowDef="columns"></tr>
            <tr mat-row *matRowDef="let row; columns: columns"></tr>
          </table>
        } @else { <p role="status">Chargement des workers…</p> }
      </section>
      <section class="detail" aria-labelledby="detail-title">
        <h2 id="detail-title">Détail</h2>
        @if (selected(); as w) {
          <mat-card><mat-card-header><mat-card-title>{{ label(w) }}</mat-card-title></mat-card-header>
            <mat-card-content><dl>
              <dt>Identifiant</dt><dd>{{ w.id }}</dd><dt>État</dt><dd>{{ state(w) }}</dd>
              <dt>Vu pour la dernière fois</dt><dd>{{ w.lastSeenAt }}</dd>
              <dt>Processeur</dt><dd>{{ w.inventory?.processorCount }} × {{ w.inventory?.processorModel ?? 'inconnu' }}</dd>
              <dt>Mémoire</dt><dd>{{ w.inventory?.memoryMb }} Mo</dd>
            </dl></mat-card-content></mat-card>
        } @else { <p>Sélectionnez un worker.</p> }
      </section>
      <section aria-labelledby="pending-title">
        <h2 id="pending-title">Demandes de rattachement</h2>
        <ul>
          @for (p of pending() ?? []; track p.id) {
            <li>{{ p.name }} — code {{ p.verificationCode ?? '—' }} — expire {{ p.expiresAt }}</li>
          } @empty { <li>Aucune demande en attente.</li> }
        </ul>
      </section>
    </main>`,
})
class App {
  private readonly http = inject(HttpClient);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);
  private readonly reload = new Subject<void>();
  readonly columns = ['name', 'state', 'system', 'tasks', 'action'];
  readonly label = label;
  readonly state = state;
  readonly selected = signal<Worker | null>(null);
  readonly busy = signal(false);
  readonly workers = toSignal(this.reload.pipe(startWith(undefined), switchMap(() => this.http.get<Worker[]>('/api/v1/workers'))));
  readonly pending = toSignal(this.reload.pipe(startWith(undefined), switchMap(() => this.http.get<Pending[]>('/api/v1/workers/enrollment-requests'))));

  openPairing(): void {
    this.dialog.open(PairingDialog, { data: { busy: this.busy } }).afterClosed().subscribe((key?: string) => {
      const pairingKey = key?.trim().toUpperCase();
      if (!pairingKey) return;
      this.busy.set(true);
      this.http.post<Worker>('/api/v1/workers/enrollment-requests/approve', { pairingKey })
        .pipe(catchError((e: { status?: number }) => {
          this.snack.open(e.status === 404 ? 'Cette clé est invalide, expirée ou déjà utilisée. Générez-en une nouvelle depuis le worker.' : 'Le worker n’a pas pu être accepté. Vérifiez que le serveur est disponible.', 'OK', { duration: 5000 });
          return of(null);
        }))
        .subscribe((w) => {
          this.busy.set(false);
          if (!w) return;
          this.snack.open(`Le worker « ${label(w)} » a rejoint le central.`, 'OK', { duration: 5000 });
          this.reload.next();
        });
    });
  }
}

bootstrapApplication(App, { providers: [provideHttpClient()] });
