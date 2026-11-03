import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  computed,
  effect,
  inject,
  untracked,
  viewChild,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { ActivatedRoute } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { map } from 'rxjs';
import {
  PageShellContextService,
} from '../../core/layout/page-shell-context';
import { LibraryPreparationTemplateComponent } from '../../shared/templates/library-preparation-template.component';
import { DrawerActionButtonComponent } from '../../shared/ui/drawer-action-button/drawer-action-button.component';
import { RecordFormActionsComponent } from '../../shared/ui/record-form-actions.component';
import { LibrarySectionTabsComponent } from './library-section-tabs.component';
import { LibraryCatalogStore } from './library-catalog.store';
import { LibraryWorkflowStore } from './library-workflow.store';
import { LibraryAccessStore } from './library-access.store';
import { LibrarySummaryTabComponent } from './components/library-summary-tab.component';
import { LibraryMediaTabComponent } from './components/library-media-tab.component';
import { LibraryAccessTabComponent } from './components/library-access-tab.component';
import { LibraryHistoryTabComponent } from './components/library-history-tab.component';
import { LibraryActionDrawerComponent } from './components/library-action-drawer.component';

/**
 * Page Bibliothèques : liste des définitions, fiche détaillée en tiroir et
 * tiroir d’action. L’état vit dans les stores de la feature ; les onglets
 * et le tiroir sont des sous-composants.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-library-catalog-page',
  providers: [LibraryCatalogStore, LibraryWorkflowStore, LibraryAccessStore],
  imports: [
    LibraryPreparationTemplateComponent,
    MatButtonModule,
    DrawerActionButtonComponent,
    RecordFormActionsComponent,
    LibrarySectionTabsComponent,
    LibrarySummaryTabComponent,
    LibraryMediaTabComponent,
    LibraryAccessTabComponent,
    LibraryHistoryTabComponent,
    LibraryActionDrawerComponent,
  ],
  templateUrl: './library-catalog-page.component.html',
})
export class LibraryCatalogPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);
  private readonly pageShellContext = inject(PageShellContextService);
  private readonly routeData = toSignal(this.route.data, {
    initialValue: this.route.snapshot.data,
  });
  private readonly libraryImportInput =
    viewChild<ElementRef<HTMLInputElement>>('libraryImportFile');

  readonly catalog = inject(LibraryCatalogStore);
  readonly workflow = inject(LibraryWorkflowStore);
  readonly access = inject(LibraryAccessStore);

  private readonly libraryParam = toSignal(
    this.route.queryParamMap.pipe(map((params) => params.get('library'))),
    { initialValue: this.route.snapshot.queryParamMap.get('library') },
  );

  private readonly actionDrawer = viewChild(LibraryActionDrawerComponent);
  protected readonly actionRecordForm = computed(
    () => this.actionDrawer()?.actionRecordForm() ?? null,
  );
  protected readonly actionStep = computed(() => this.actionDrawer()?.actionStep() ?? null);

  protected readonly title = computed(
    () => (this.routeData()['title'] as string) ?? 'Bibliothèques',
  );

  constructor() {
    this.destroyRef.onDestroy(
      this.pageShellContext.register({
        searchPlaceholder: 'Rechercher dans Bibliothèques…',
        query: this.catalog.searchQuery,
        onSearchQueryChange: (query) => this.catalog.searchQuery.set(query),
        searchResults: (query) => this.catalog.librarySearchResults(query),
        actions: [
          {
            id: 'create-library',
            label: 'Ajouter une bibliothèque',
            icon: 'add',
            run: () => this.catalog.addLibrary(),
          },
          {
            id: 'import-libraries',
            label: 'Importer',
            icon: 'upload',
            visibleWhen: () => !this.catalog.hasSelectedLibraries(),
            run: () => this.libraryImportInput()?.nativeElement.click(),
          },
          {
            id: 'export-libraries',
            label: 'Exporter',
            icon: 'outward',
            visibleWhen: () => this.catalog.hasSelectedLibraries(),
            run: () => void this.catalog.exportSelectedLibraries(),
          },
        ],
      }),
    );
    this.catalog.refresh();
    // La fiche est portée par l'URL (?library=<id>) : le menu latéral et le bouton précédent
    // du navigateur ramènent à la liste sans bouton retour dédié.
    effect(() => {
      const id = this.libraryParam();
      const libraries = this.catalog.libraries();
      untracked(() => {
        if (!id) {
          if (this.catalog.selectedLibrary()) this.catalog.selectedLibrary.set(null);
          return;
        }
        if (this.catalog.selectedLibrary()?.id === id) return;
        const library = libraries.find((item) => item.id === id);
        if (library)
          this.workflow.openLibrary(library, (libraryId) =>
            this.access.loadSelectedAccesses(libraryId),
          );
      });
    });
  }

  protected onLibraryImportSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    void this.catalog.onLibraryImportSelected(file);
  }
}
