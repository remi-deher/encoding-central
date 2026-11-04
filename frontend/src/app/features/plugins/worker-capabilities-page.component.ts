import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { CentralMaintenanceTemplateComponent } from '../../shared/templates/central-maintenance-template.component';
import { LocalWorkerController } from '../../shared/services/local-worker-controller';
import { LocalWorkerStoreStateService } from '../../shared/services/local-worker-store-state';
import {
  centralLinkSections,
  detectedToolSections,
  receivedPluginItems,
  receivedPluginSections,
} from '../../shared/models/local-worker-views';
import { CategoryTabOption } from '../../shared/ui/category-tabs.component';
import { ResourceSheetComponent } from '../../shared/ui/resource-sheet.component';

/**
 * Capacités de l'image worker (`app.routes.worker.ts`), en lecture seule : outils détectés sur ce
 * worker, plugins reçus du central et état de synchronisation. Aucune activation globale, aucun
 * Store, dépôt, installation ni suppression : tout cela relève du central.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-worker-capabilities-page',
  providers: [LocalWorkerController, LocalWorkerStoreStateService],
  imports: [CentralMaintenanceTemplateComponent, ResourceSheetComponent],
  template: `
    <app-central-maintenance-template
      title="Capacités"
      navigationLabel="Sections des capacités"
      [navigationOptions]="tabs()"
      [navigationSelected]="tab()"
      (navigationSelectedChange)="tab.set($event)"
      [showToolbar]="false"
      [catalogState]="local.loading() ? 'loading' : 'ready'"
      catalogMessage="Chargement des capacités de ce worker…"
    >
      <div catalogue-table>
        @switch (tab()) {
          @case ('tools') {
            <app-resource-sheet
              ariaLabel="Outils détectés sur ce worker"
              title="Outils"
              subtitle="Détectés localement par l’agent de ce worker"
              [sections]="toolSections()"
            />
          }
          @case ('plugins') {
            <app-resource-sheet
              ariaLabel="Plugins reçus du central"
              title="Plugins"
              subtitle="Fournis par le central, en lecture seule"
              [sections]="pluginSections()"
            />
          }
          @default {
            <app-resource-sheet
              ariaLabel="Synchronisation avec le central"
              title="Synchronisation"
              subtitle="Ce que le central demande à ce worker et ce qu’il a appliqué"
              [sections]="syncSections()"
            />
          }
        }
      </div>
    </app-central-maintenance-template>
  `,
})
export class WorkerCapabilitiesPageComponent {
  protected readonly local = inject(LocalWorkerController);
  private readonly store = inject(LocalWorkerStoreStateService);

  protected readonly tab = signal('tools');
  protected readonly tabs = computed<readonly CategoryTabOption[]>(() => [
    { value: 'tools', label: 'Outils', count: this.local.status()?.capabilities.length ?? 0 },
    { value: 'plugins', label: 'Plugins', count: receivedPluginItems(this.store.state()).length },
    { value: 'sync', label: 'Synchronisation' },
  ]);
  protected readonly toolSections = computed(() =>
    detectedToolSections(this.local.status()?.capabilities ?? [], this.local.agentUnavailable()),
  );
  protected readonly pluginSections = computed(() => receivedPluginSections(this.store.state()));
  protected readonly syncSections = computed(() =>
    centralLinkSections(this.local.status(), this.store.state(), this.local.agentUnavailable()).filter(
      (section) => section.id === 'central-sync',
    ),
  );

  constructor() {
    effect(() => this.store.setWorkerId(this.local.status()?.workerId));
  }
}
