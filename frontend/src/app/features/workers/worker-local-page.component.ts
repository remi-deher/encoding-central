import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { CentralMaintenanceTemplateComponent } from '../../shared/templates/central-maintenance-template.component';
import { LocalWorkerController } from '../../shared/services/local-worker-controller';
import { LocalWorkerStoreStateService } from '../../shared/services/local-worker-store-state';
import { centralLinkSections } from '../../shared/models/local-worker-views';
import { CategoryTabOption } from '../../shared/ui/category-tabs.component';
import { ResourceSheetComponent } from '../../shared/ui/resource-sheet.component';
import { WorkerDataStore } from './worker-data.store';
import { WorkerMembersStore } from './worker-members.store';
import { WorkerDetailStore } from './worker-detail.store';
import { WorkerProcessingStore } from './worker-processing.store';
import { WorkerLocalTabComponent } from './components/worker-local-tab.component';

const LOCAL_TABS: readonly CategoryTabOption[] = [
  { value: 'node', label: 'Ce worker' },
  { value: 'central', label: 'Lien avec le central' },
];

/**
 * Page Workers de l'image worker (`app.routes.worker.ts`) : uniquement l'état de CE worker
 * (traitements, matériel) et son lien avec le central. Ni cluster, ni groupes, ni administration.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-worker-local-page',
  providers: [
    LocalWorkerController,
    WorkerDataStore,
    WorkerMembersStore,
    WorkerDetailStore,
    WorkerProcessingStore,
    LocalWorkerStoreStateService,
  ],
  imports: [CentralMaintenanceTemplateComponent, ResourceSheetComponent, WorkerLocalTabComponent],
  template: `
    <app-central-maintenance-template
      [title]="title()"
      navigationLabel="Ce worker"
      [navigationOptions]="tabs"
      [navigationSelected]="tab()"
      (navigationSelectedChange)="tab.set($event)"
      [showToolbar]="false"
      [catalogState]="pageState()"
      catalogMessage="Chargement de l’état de ce worker…"
    >
      <div catalogue-table>
        @if (tab() === 'node') {
          <app-worker-local-tab [showLibraries]="false" />
        } @else {
          <app-resource-sheet
            ariaLabel="Lien de ce worker avec le central"
            [title]="data.local.status()?.centralName ?? 'Aucun central'"
            subtitle="Rattachement et synchronisation de ce worker"
            [sections]="linkSections()"
          />
        }
      </div>
    </app-central-maintenance-template>
  `,
})
export class WorkerLocalPageComponent {
  protected readonly data = inject(WorkerDataStore);
  private readonly store = inject(LocalWorkerStoreStateService);

  protected readonly tabs = LOCAL_TABS;
  protected readonly tab = signal('node');
  protected readonly title = computed(() => this.data.localName());
  protected readonly pageState = computed(() => (this.data.local.loading() ? 'loading' : 'ready'));
  protected readonly linkSections = computed(() =>
    centralLinkSections(
      this.data.local.status(),
      this.store.state(),
      this.data.local.agentUnavailable(),
    ),
  );

  constructor() {
    effect(() => this.store.setWorkerId(this.data.local.status()?.workerId));
  }
}
