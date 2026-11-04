import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  output,
  viewChildren,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { RecordFormComponent } from '../../../shared/ui/record-form.component';
import { GuidedSetupComponent } from '../../../shared/ui/guided-setup.component';
import { IconComponent } from '../../../shared/ui/icon.component';
import { LibraryWorkerAccessFormComponent } from '../../../shared/ui/library-worker-access-form.component';
import { DataTableComponent } from '../../../shared/ui/data-table.component';
import {
  centralAddressForm,
  centralJoinSteps,
} from '../../../shared/models/worker-management';
import { AuthService } from '../../../core/auth/auth.service';
import { WorkerDataStore } from '../worker-data.store';
import { WorkerDetailStore } from '../worker-detail.store';
import { WorkerEnrollmentStore } from '../worker-enrollment.store';
import { WorkerGroupsStore } from '../worker-groups.store';
import { WorkerGroupEditorComponent } from './worker-group-editor.component';
import { CREATE_LIBRARY_FORM_DATA } from '../worker-catalog.utils';

/**
 * Contenu du tiroir d’action de la page Workers : éditeur de groupe, assistant
 * « rejoindre un central », formulaires d’identité/accès/création et vues d’enrôlement.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-worker-action-drawer',
  imports: [
    MatButtonModule,
    DatePipe,
    IconComponent,
    DataTableComponent,
    GuidedSetupComponent,
    RecordFormComponent,
    LibraryWorkerAccessFormComponent,
    WorkerGroupEditorComponent,
  ],
  templateUrl: './worker-action-drawer.component.html',
  styleUrl: './worker-action-drawer.component.scss',
})
export class WorkerActionDrawerComponent {
  /** Mode « Groupes de workers » : le tiroir porte l’éditeur de groupe. */
  readonly groupsMode = input(false);
  readonly data = inject(WorkerDataStore);
  readonly detail = inject(WorkerDetailStore);
  readonly enrollment = inject(WorkerEnrollmentStore);
  readonly groups = inject(WorkerGroupsStore);
  protected readonly local = this.data.local;
  protected readonly isAdmin = inject(AuthService).isAdmin;

  /** Fermeture de l’assistant « rejoindre un central » terminée : l’onglet passe à « Central ». */
  readonly setupFinished = output<void>();

  readonly createLibraryFormData = CREATE_LIBRARY_FORM_DATA;

  private readonly recordForms = viewChildren(RecordFormComponent);
  readonly actionRecordForm = computed(() =>
    this.recordForms().find((form) => form.embedded()),
  );
  readonly actionStep = computed(() => {
    const form = this.actionRecordForm();
    return form ? { current: form.stepIndex() + 1, total: form.stepCount() } : null;
  });

  protected readonly addressForm = computed(() => centralAddressForm(this.local.address()));
  protected readonly joinSteps = computed(() =>
    centralJoinSteps({
      addressForm: this.addressForm(),
      target: this.local.target(),
      status: this.local.status(),
      pairing: this.local.pairing(),
      clusters: this.local.joinableClusters(),
      localServerActive: this.local.status()?.roles.includes('server') ?? false,
      canChoose: this.local.canJoin(),
    }),
  );

  protected onSetupAction(id: string): void {
    if (id === 'back') this.local.back();
    if (id === 'request') void this.local.sendRequest();
    if (id === 'later') this.local.closeFlow();
    if (id === 'cancel') void this.local.leave();
    if (id === 'finish') {
      this.local.closeFlow();
      this.setupFinished.emit();
    }
  }
}
