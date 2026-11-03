import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { DataTableComponent } from '../../../shared/ui/data-table.component';
import { LibraryAccessStore } from '../library-access.store';
import { LibraryCatalogStore } from '../library-catalog.store';

/** Onglet « Accès par worker » de la fiche bibliothèque (emplacement `libraryAccess`). */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-library-access-tab',
  imports: [MatButtonModule, DataTableComponent],
  template: `
    @if (catalog.selectedLibrary()) {
      <section class="library-access-summary" aria-labelledby="library-access-heading">
        <div>
          <h3 id="library-access-heading">Workers autorisés par le serveur</h3>
          <p>{{ access.accessStatus() }}</p>
        </div>
        <button
          matButton="outlined"
          type="button"
          [disabled]="
            access.selectedAccessesLoading() ||
            access.accessFormLoading() ||
            catalog.accessFormBusy()
          "
          (click)="access.configureAccesses()"
        >
          Modifier les accès
        </button>
      </section>
      @if (access.selectedAccesses().length) {
        <app-data-table
          [columns]="access.accessColumns"
          [rows]="access.accessRows()"
          [rowActions]="access.accessRowActions"
          [separateRowActionColumns]="true"
          (rowAction)="access.onAccessRowAction($event)"
          (toggleChange)="access.onAccessToggle($event)"
          emptyMessage="Aucun accès configuré."
        />
      } @else {
        <p class="library-access-empty">
          Aucun worker n’a encore accès à cette bibliothèque. Elle ne sera pas traitée tant qu’un
          accès n’est pas configuré.
        </p>
      }
    }
  `,
  styleUrl: './library-access-tab.component.scss',
})
export class LibraryAccessTabComponent {
  protected readonly catalog = inject(LibraryCatalogStore);
  protected readonly access = inject(LibraryAccessStore);
}
