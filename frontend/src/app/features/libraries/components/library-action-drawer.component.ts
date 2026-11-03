import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  viewChildren,
} from '@angular/core';
import { RecordFormComponent } from '../../../shared/ui/record-form.component';
import { LibraryWorkerAccessFormComponent } from '../../../shared/ui/library-worker-access-form.component';
import { LibraryAccessStore } from '../library-access.store';
import { LibraryCatalogStore } from '../library-catalog.store';
import { LibraryWorkflowStore } from '../library-workflow.store';

/**
 * Contenu du tiroir d’action des bibliothèques : création/édition,
 * surveillance et limites, attribution de flow et accès par worker.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-library-action-drawer',
  imports: [RecordFormComponent, LibraryWorkerAccessFormComponent],
  templateUrl: './library-action-drawer.component.html',
})
export class LibraryActionDrawerComponent {
  readonly catalog = inject(LibraryCatalogStore);
  readonly workflow = inject(LibraryWorkflowStore);
  readonly access = inject(LibraryAccessStore);

  private readonly recordForms = viewChildren(RecordFormComponent);
  readonly actionRecordForm = computed(() =>
    this.recordForms().find((form) => form.embedded()),
  );
  readonly actionStep = computed(() => {
    const form = this.actionRecordForm();
    return form ? { current: form.stepIndex() + 1, total: form.stepCount() } : null;
  });
}
