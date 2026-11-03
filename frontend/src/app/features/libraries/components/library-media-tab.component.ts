import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { DataTableComponent } from '../../../shared/ui/data-table.component';
import { LibraryCatalogStore } from '../library-catalog.store';
import { LibraryWorkflowStore } from '../library-workflow.store';

/** Onglet « Fichiers détectés » de la fiche bibliothèque (emplacement `libraryMedia`). */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-library-media-tab',
  imports: [DataTableComponent],
  template: `
    @if (catalog.selectedLibrary()) {
      <h3>Fichiers détectés</h3>
      <app-data-table
        [columns]="workflow.mediaColumns"
        [rows]="workflow.mediaRows()"
        [selectable]="true"
        [listActions]="workflow.mediaListActions"
        [rowActions]="workflow.mediaRowActions"
        [selectionActions]="workflow.mediaSelectionActions"
        (listAction)="workflow.discoverFiles()"
        (rowAction)="workflow.processFiles([$event.row['id'].toString()])"
        (selectionAction)="workflow.onMediaSelection($event)"
        emptyMessage="Aucun fichier détecté. Attribuez un flow puis lancez la découverte."
      />
    }
  `,
})
export class LibraryMediaTabComponent {
  protected readonly catalog = inject(LibraryCatalogStore);
  protected readonly workflow = inject(LibraryWorkflowStore);
}
