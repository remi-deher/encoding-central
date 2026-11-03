import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { AuthService } from '../../../core/auth/auth.service';
import { DataTableComponent } from '../../../shared/ui/data-table.component';
import { WorkerDataStore } from '../worker-data.store';
import { WorkerMembersStore } from '../worker-members.store';
import { WorkerEnrollmentStore } from '../worker-enrollment.store';
import { WorkerOverviewCardComponent } from '../worker-overview-card.component';

/** Onglet « Central » : demandes de rattachement, sélection et cartes des membres. */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-worker-cluster-tab',
  imports: [MatButtonModule, DataTableComponent, WorkerOverviewCardComponent],
  template: `
    @if (isAdmin() && enrollment.enrollmentRows().length) {
      <section id="node-enrollment-requests" class="node-enrollment-requests">
        <header class="node-enrollment-heading">
          <h2>Demandes de rattachement</h2>
          <p>Comparez le code affiché sur le worker demandeur avant de l’approuver.</p>
        </header>
        <app-data-table
          [columns]="enrollment.enrollmentColumns"
          [rows]="enrollment.enrollmentRows()"
          layout="resources"
          [layoutOptions]="enrollment.enrollmentLayoutOptions"
          [rowOpenable]="true"
          rowOpenLabel="Examiner la demande"
          (rowOpen)="enrollment.onEnrollmentOpen($event)"
        />
      </section>
    }
    @if (members.selectedMemberIds().size && isAdmin()) {
      <div class="member-selection" role="group" aria-label="Actions sur la sélection">
        <span>{{ members.selectedMemberIds().size }} worker(s) sélectionné(s)</span>
        <button
          matButton="outlined"
          type="button"
          [disabled]="members.bulkBusy()"
          (click)="members.runMemberBulk('drain')"
        >
          Vidanger
        </button>
        <button
          matButton="outlined"
          type="button"
          [disabled]="members.bulkBusy()"
          (click)="members.runMemberBulk('resume')"
        >
          Reprendre le travail
        </button>
        <button
          matButton="outlined"
          type="button"
          [disabled]="members.bulkBusy()"
          (click)="members.runMemberBulk('delete')"
        >
          Supprimer
        </button>
        <button
          matButton="text"
          type="button"
          (click)="members.selectedMemberIds.set(emptySelection)"
        >
          Tout désélectionner
        </button>
      </div>
    }
    @if (members.memberCards().length) {
      <div class="member-cards">
        @for (card of members.memberCards(); track card.id) {
          <app-worker-overview-card
            [ariaLabel]="'Carte du worker ' + card.title"
            [isMaster]="card.isMaster"
            [title]="card.title"
            [subtitle]="card.subtitle"
            [hardware]="card.hardware"
            [statusLabel]="card.statusLabel"
            [statusTone]="card.statusTone"
            [nodeId]="card.id"
            [roles]="card.roles"
            [admissionEnabled]="card.admissionEnabled"
            [admissionDisabled]="card.admissionDisabled"
            [taskCapacity]="card.taskCapacity"
            [meta]="card.meta"
            [sections]="card.sections"
            [libraries]="card.libraries"
            [libraryAccessState]="data.libraryAccessState()"
            [actions]="members.memberActions()"
            [selectable]="isAdmin() && !members.bulkBusy()"
            [selected]="members.selectedMemberIds().has(card.id)"
            [active]="data.selectedNode()?.id === card.id"
            [openable]="true"
            (selectedChange)="members.toggleMember(card.id, $event)"
            (actionSelected)="members.onMemberCardAction(card.id, $event)"
            (admissionChange)="members.setMemberAdmission(card.id, $event)"
            (open)="data.openNodeCard(card.id)"
          />
        }
      </div>
    } @else {
      <p class="member-empty">Aucun worker ne correspond à ces filtres.</p>
    }
  `,
  styleUrl: './worker-cluster-tab.component.scss',
})
export class WorkerClusterTabComponent {
  readonly data = inject(WorkerDataStore);
  readonly members = inject(WorkerMembersStore);
  readonly enrollment = inject(WorkerEnrollmentStore);
  protected readonly isAdmin = inject(AuthService).isAdmin;
  protected readonly emptySelection: ReadonlySet<string> = new Set();
}
