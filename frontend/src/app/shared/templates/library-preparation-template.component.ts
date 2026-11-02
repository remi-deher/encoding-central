import {
  ChangeDetectionStrategy,
  Component,
  TemplateRef,
  contentChild,
  input,
  model,
  output,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { MatTabsModule } from '@angular/material/tabs';
import {
  DataTableActionEvent,
  DataTableRowAction,
  DataTableSelectionActionEvent,
} from '../ui/data-table.component';
import {
  ResourceListGroup,
  ResourceListItem,
  ResourceListSelectionAction,
  ResourceListToggleEvent,
} from '../ui/resource-list.component';
import { CatalogEmptyState } from './management-catalog-template.component';
import { ResourceCatalogTemplateComponent } from './resource-catalog-template.component';

/** Gabarit Préparer : catalogue de bibliothèques et fiche à onglets pour définir le périmètre de traitement. */
@Component({
  selector: 'app-library-preparation-template',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatTabsModule, NgTemplateOutlet, ResourceCatalogTemplateComponent],
  template: `
    <app-resource-catalog-template
      [title]="title()"
      [items]="items()"
      [rowActions]="rowActions()"
      [selectionActions]="selectionActions()"
      [selectable]="selectable()"
      [disabled]="disabled()"
      [(selectedIds)]="selectedIds"
      [openLabel]="openLabel()"
      [openMode]="openMode()"
      [menuPlacement]="menuPlacement()"
      [groupDefinitions]="groupDefinitions()"
      [selectionUnit]="selectionUnit()"
      [listEmptyMessage]="listEmptyMessage()"
      [showResourceIcon]="showResourceIcon()"
      [filterLabel]="filterLabel()"
      [searchLabel]="searchLabel()"
      [searchQuery]="searchQuery()"
      [searchLayout]="searchLayout()"
      [filterCount]="filterCount()"
      [note]="note()"
      [showToolbar]="showToolbar()"
      [emptyState]="emptyState()"
      [catalogState]="catalogState()"
      [catalogMessage]="catalogMessage()"
      [detailLayout]="detailLayout()"
      [detailOpen]="detailOpen()"
      [detailTitle]="detailTitle()"
      [detailDescription]="detailDescription()"
      [actionOpen]="actionOpen()"
      [actionTitle]="actionTitle()"
      [actionDescription]="actionDescription()"
      [actionStep]="actionStep()"
      [drawerSize]="drawerSize()"
      [drawerHeaderDensity]="drawerHeaderDensity()"
      [detailBodyLayout]="detailBodyLayout()"
      [drawerModal]="drawerModal()"
      [catalogueNavigationTemplate]="catalogueNavigation()"
      [actionContentTemplate]="actionContent()"
      [actionFooterActionsTemplate]="actionFooterActions()"
      [detailHeaderActionsTemplate]="detailHeaderActions()"
      [pageActionsTemplate]="pageActions()"
      [toolbarActionsTemplate]="toolbarActions()"
      (searchQueryChange)="searchQueryChange.emit($event)"
      (emptyStateAction)="emptyStateAction.emit()"
      (detailClosed)="detailClosed.emit()"
      (actionClosed)="actionClosed.emit()"
      (open)="open.emit($event)"
      (action)="action.emit($event)"
      (toggleChange)="toggleChange.emit($event)"
      (selectionAction)="selectionAction.emit($event)"
    >
      <ng-content select="[catalogue-navigation]" />
      <ng-content select="[catalogue-summary]" />
      <ng-template #detailContent catalogue-detail>
        <mat-tab-group
          class="library-detail-tabs"
          aria-label="Sections de la bibliothèque"
          [selectedIndex]="detailTabIndex()"
          (selectedIndexChange)="detailTabIndex.set($event)"
        >
          <mat-tab label="Synthèse">
            <ng-container [ngTemplateOutlet]="summaryContent()" />
          </mat-tab>
          <mat-tab [label]="'Fichiers détectés (' + mediaCount() + ')'">
            <ng-container [ngTemplateOutlet]="mediaContent()" />
          </mat-tab>
          <mat-tab [label]="'Accès des nœuds (' + accessCount() + ')'">
            <ng-container [ngTemplateOutlet]="accessContent()" />
          </mat-tab>
          <mat-tab label="Historique">
            <ng-container [ngTemplateOutlet]="historyContent()" />
          </mat-tab>
        </mat-tab-group>
      </ng-template>
      <ng-content select="ng-template" />
    </app-resource-catalog-template>
  `,
  styles: `
    .library-detail-tabs {
      box-sizing: border-box;
      display: flex;
      flex: 1 1 0;
      height: 100%;
      min-height: 0;
      padding: var(--space-4) var(--space-5);
    }

    :host ::ng-deep .library-detail-tabs > .mat-mdc-tab-body-wrapper {
      flex: 1 1 0;
      min-height: 0;
    }

    :host ::ng-deep .library-detail-tabs .mat-mdc-tab-body-content {
      overflow: auto;
      overscroll-behavior: contain;
    }

    @media (max-width: 700px) {
      .library-detail-tabs {
        padding: var(--space-3) var(--space-4);
      }
    }
  `,
})
export class LibraryPreparationTemplateComponent {
  protected readonly actionContent = contentChild('actionContent', { read: TemplateRef });
  protected readonly actionFooterActions = contentChild('actionFooterActions', {
    read: TemplateRef,
  });
  protected readonly detailHeaderActions = contentChild('detailHeaderActions', {
    read: TemplateRef,
  });
  protected readonly pageActions = contentChild('pageActions', { read: TemplateRef });
  protected readonly toolbarActions = contentChild('catalogueToolbarActions', {
    read: TemplateRef,
  });
  readonly title = input.required<string>();
  readonly items = input.required<readonly ResourceListItem[]>();
  readonly rowActions = input<readonly DataTableRowAction[]>([]);
  readonly selectionActions = input<readonly ResourceListSelectionAction[]>([]);
  readonly selectable = input(true);
  readonly disabled = input(false);
  readonly selectedIds = model<ReadonlySet<string>>(new Set());
  readonly openLabel = input('Ouvrir');
  readonly openMode = input<'button' | 'title' | 'none'>('button');
  readonly menuPlacement = input<'inline' | 'top-right'>('inline');
  readonly groupDefinitions = input<readonly ResourceListGroup[]>([]);
  readonly selectionUnit = input('bibliothèque(s)');
  readonly listEmptyMessage = input('Aucune bibliothèque.');
  readonly showResourceIcon = input(true);

  readonly filterLabel = input('Filtres');
  readonly searchLabel = input('Rechercher');
  readonly searchQuery = input('');
  readonly searchLayout = input<'end' | 'centered' | 'centered-sheet'>('centered');
  readonly filterCount = input(0);
  readonly note = input('');
  readonly showToolbar = input(true);
  readonly emptyState = input<CatalogEmptyState | null>(null);
  readonly catalogState = input<'ready' | 'loading' | 'error'>('ready');
  readonly catalogMessage = input('');
  readonly detailLayout = input<'drawer' | 'page'>('drawer');
  readonly detailOpen = input(false);
  readonly detailTitle = input('Bibliothèque');
  readonly detailDescription = input('');
  readonly detailTabIndex = model(0);
  readonly mediaCount = input(0);
  readonly accessCount = input(0);
  readonly actionOpen = input(false);
  readonly actionTitle = input('Configurer');
  readonly actionDescription = input('');
  readonly actionStep = input<{ readonly current: number; readonly total: number } | null>(null);
  readonly drawerSize = input<'standard' | 'wide'>('wide');
  readonly drawerHeaderDensity = input<'standard' | 'compact'>('standard');
  readonly detailBodyLayout = input<'scroll' | 'fill'>('fill');
  readonly drawerModal = input(true);

  readonly searchQueryChange = output<string>();
  readonly emptyStateAction = output<void>();
  readonly detailClosed = output<void>();
  readonly actionClosed = output<void>();
  readonly open = output<string>();
  readonly action = output<DataTableActionEvent>();
  readonly toggleChange = output<ResourceListToggleEvent>();
  readonly selectionAction = output<DataTableSelectionActionEvent>();

  protected readonly summaryContent = contentChild('librarySummary', { read: TemplateRef });
  protected readonly catalogueNavigation = contentChild('catalogueNavigation', {
    read: TemplateRef,
  });
  protected readonly mediaContent = contentChild('libraryMedia', { read: TemplateRef });
  protected readonly accessContent = contentChild('libraryAccess', { read: TemplateRef });
  protected readonly historyContent = contentChild('libraryHistory', { read: TemplateRef });
}
