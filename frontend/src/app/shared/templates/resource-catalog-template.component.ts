import {
  ChangeDetectionStrategy,
  Component,
  TemplateRef,
  contentChild,
  input,
  model,
  output,
} from '@angular/core';
import {
  DataTableActionEvent,
  DataTableRowAction,
  DataTableSelectionActionEvent,
} from '../ui/data-table.component';
import { IconName } from '../ui/icon.component';
import {
  ResourceListComponent,
  ResourceListGroup,
  ResourceListItem,
  ResourceListSelectionAction,
  ResourceListToggleEvent,
} from '../ui/resource-list.component';
import {
  CatalogEmptyState,
  ManagementCatalogTemplateComponent,
} from './management-catalog-template.component';

/** Shell partagé des catalogues : la feature fournit les lignes et les interactions métier. */
@Component({
  selector: 'app-resource-catalog-template',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ManagementCatalogTemplateComponent, ResourceListComponent],
  template: `
    <app-management-catalog-template
      [title]="title()"
      [titleVisuallyHidden]="true"
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
      [actionCloseGuarded]="actionCloseGuarded()"
      (actionCloseRequested)="actionCloseRequested.emit()"
      [actionTitle]="actionTitle()"
      [actionDescription]="actionDescription()"
      [actionStep]="actionStep()"
      [drawerSize]="drawerSize()"
      [drawerHeaderDensity]="drawerHeaderDensity()"
      [detailBodyLayout]="detailBodyLayout()"
      [drawerModal]="drawerModal()"
      [pageActionsInput]="pageActionsTemplate() ?? pageActions()"
      [toolbarActionsInput]="toolbarActionsTemplate() ?? toolbarActions()"
      [detailContentInput]="detailContent()"
      [detailFooterActionsInput]="detailFooterActions()"
      [detailHeaderActionsInput]="detailHeaderActionsTemplate() ?? detailHeaderActions()"
      [actionFooterActionsInput]="actionFooterActionsTemplate() ?? actionFooterActions()"
      [actionContentInput]="actionContentTemplate() ?? actionContent()"
      [catalogueNavigationInput]="catalogueNavigation()"
      [catalogueSummaryInput]="catalogueSummary()"
      (searchQueryChange)="searchQueryChange.emit($event)"
      (emptyStateAction)="emptyStateAction.emit()"
      (detailClosed)="detailClosed.emit()"
      (actionClosed)="actionClosed.emit()"
    >
      <div catalogue-table>
        <app-resource-list
          [items]="items()"
          [actions]="rowActions()"
          [icon]="resourceIcon()"
          [showIcon]="showResourceIcon()"
          [openLabel]="openLabel()"
          [openMode]="openMode()"
          [menuPlacement]="menuPlacement()"
          [groupDefinitions]="groupDefinitions()"
          [selectionUnit]="selectionUnit()"
          [emptyMessage]="listEmptyMessage()"
          [selectable]="selectable()"
          [disabled]="disabled()"
          [selectionActions]="selectionActions()"
          [(selectedIds)]="selectedIds"
          (open)="open.emit($event)"
          (action)="action.emit($event)"
          (toggleChange)="toggleChange.emit($event)"
          (selectionAction)="selectionAction.emit($event)"
        />
      </div>
    </app-management-catalog-template>
  `,
})
export class ResourceCatalogTemplateComponent {
  readonly toolbarActionsTemplate = input<TemplateRef<unknown> | undefined>();
  readonly pageActionsTemplate = input<TemplateRef<unknown> | undefined>();
  readonly detailHeaderActionsTemplate = input<TemplateRef<unknown> | undefined>();
  readonly actionFooterActionsTemplate = input<TemplateRef<unknown> | undefined>();
  readonly actionContentTemplate = input<TemplateRef<unknown> | undefined>();
  protected readonly pageActions = contentChild('pageActions', { read: TemplateRef });
  protected readonly toolbarActions = contentChild('catalogueToolbarActions', {
    read: TemplateRef,
  });
  protected readonly detailContent = contentChild('detailContent', { read: TemplateRef });
  protected readonly detailFooterActions = contentChild('detailFooterActions', {
    read: TemplateRef,
  });
  protected readonly detailHeaderActions = contentChild('detailHeaderActions', {
    read: TemplateRef,
  });
  protected readonly actionFooterActions = contentChild('actionFooterActions', {
    read: TemplateRef,
  });
  protected readonly actionContent = contentChild('actionContent', { read: TemplateRef });
  protected readonly catalogueNavigationContent = contentChild('catalogueNavigation', {
    read: TemplateRef,
  });
  protected readonly catalogueSummaryContent = contentChild('catalogueSummary', {
    read: TemplateRef,
  });
  readonly catalogueNavigationTemplate = input<TemplateRef<unknown> | undefined>();
  readonly catalogueSummaryTemplate = input<TemplateRef<unknown> | undefined>();
  protected readonly catalogueNavigation = () =>
    this.catalogueNavigationTemplate() ?? this.catalogueNavigationContent();
  protected readonly catalogueSummary = () =>
    this.catalogueSummaryTemplate() ?? this.catalogueSummaryContent();

  readonly title = input.required<string>();
  readonly items = input.required<readonly ResourceListItem[]>();
  readonly rowActions = input<readonly DataTableRowAction[]>([]);
  readonly selectionActions = input<readonly ResourceListSelectionAction[]>([]);
  readonly resourceIcon = input<IconName>('flow');
  readonly showResourceIcon = input(true);
  readonly openLabel = input('Ouvrir');
  readonly openMode = input<'button' | 'title' | 'none'>('button');
  readonly menuPlacement = input<'inline' | 'top-right'>('inline');
  readonly groupDefinitions = input<readonly ResourceListGroup[]>([]);
  readonly selectionUnit = input('élément(s)');
  readonly listEmptyMessage = input('Aucun résultat.');
  readonly selectable = input(false);
  readonly disabled = input(false);
  readonly selectedIds = model<ReadonlySet<string>>(new Set());

  readonly filterLabel = input('Filtres');
  readonly searchLabel = input('');
  readonly searchQuery = input('');
  readonly searchLayout = input<'end' | 'centered' | 'centered-sheet'>('end');
  readonly filterCount = input(0);
  readonly note = input('');
  readonly showToolbar = input(true);
  readonly emptyState = input<CatalogEmptyState | null>(null);
  readonly catalogState = input<'ready' | 'loading' | 'error'>('ready');
  readonly catalogMessage = input('');
  readonly detailLayout = input<'drawer' | 'page'>('drawer');
  readonly detailOpen = input(false);
  readonly detailTitle = input('Détails');
  readonly detailDescription = input('');
  readonly actionOpen = input(false);
  readonly actionCloseGuarded = input(false);
  readonly actionCloseRequested = output<void>();
  readonly actionTitle = input('Ajouter');
  readonly actionDescription = input('');
  readonly actionStep = input<{ readonly current: number; readonly total: number } | null>(null);
  readonly drawerSize = input<'standard' | 'wide'>('wide');
  readonly drawerHeaderDensity = input<'standard' | 'compact'>('standard');
  readonly detailBodyLayout = input<'scroll' | 'fill'>('scroll');
  readonly drawerModal = input(true);

  readonly searchQueryChange = output<string>();
  readonly emptyStateAction = output<void>();
  readonly detailClosed = output<void>();
  readonly actionClosed = output<void>();
  readonly open = output<string>();
  readonly action = output<DataTableActionEvent>();
  readonly toggleChange = output<ResourceListToggleEvent>();
  readonly selectionAction = output<DataTableSelectionActionEvent>();
}
