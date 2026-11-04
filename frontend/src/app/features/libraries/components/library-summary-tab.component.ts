import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { ResourceSheetComponent } from '../../../shared/ui/resource-sheet.component';
import { LibraryWorkflowStore } from '../library-workflow.store';

/** Onglet « Synthèse » de la fiche bibliothèque (emplacement `librarySummary`). */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-library-summary-tab',
  imports: [ResourceSheetComponent],
  template: `
    @if (workflow.librarySheet(); as sheet) {
      <app-resource-sheet
        [fill]="true"
        ariaLabel="Accès, traitement et activité de la bibliothèque"
        [title]="sheet.title"
        [subtitle]="sheet.subtitle"
        [notices]="sheet.notices"
        [sections]="sheet.sections"
      />
    }
  `,
})
export class LibrarySummaryTabComponent {
  protected readonly workflow = inject(LibraryWorkflowStore);
}
