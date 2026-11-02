import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  model,
  output,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog } from '@angular/material/dialog';
import { MatMenuModule } from '@angular/material/menu';
import { MatSlideToggleChange, MatSlideToggleModule } from '@angular/material/slide-toggle';
import { take } from 'rxjs';
import {
  ConfirmationDialogComponent,
  ConfirmationDialogData,
} from './confirmation-dialog.component';
import type {
  DataTableActionEvent,
  DataTableRow,
  DataTableRowAction,
  DataTableSelectionAction,
} from './data-table.component';
import { IconComponent, IconName } from './icon.component';

export interface ResourceListTag {
  readonly label: string;
  /** `ok` pour un état conforme, `warn` pour une attention à porter, `neutral` sinon. */
  readonly tone?: 'neutral' | 'ok' | 'warn' | 'danger';
}

export interface ResourceListGroup {
  readonly id: string;
  readonly label: string;
  readonly icon?: IconName;
  readonly tone?: 'default' | 'blue' | 'purple' | 'warning';
  readonly countUnit?: { readonly singular: string; readonly plural: string };
}

export interface ResourceListItem {
  readonly id: string;
  readonly name: string;
  readonly subtitle?: string;
  readonly meta?: string;
  readonly group?: ResourceListGroup;
  /** Données métier originales à transmettre aux actions de ligne et de sélection. */
  readonly rowData?: DataTableRow;
  readonly tags: readonly ResourceListTag[];
  readonly toggle?: {
    readonly checked: boolean;
    readonly disabled?: boolean;
    readonly ariaLabel: string;
    /** Confirmation demandée à la désactivation seulement. */
    readonly confirmOff?: ConfirmationDialogData;
  };
}

export interface ResourceListSelectionAction extends DataTableSelectionAction {
  readonly labelFor?: (rows: readonly DataTableRow[]) => string;
}

export interface ResourceListToggleEvent {
  readonly id: string;
  readonly checked: boolean;
}

/** Liste de ressources en lignes empilées, avec groupes, états et actions fournis par les pages. */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-resource-list',
  imports: [IconComponent, MatButtonModule, MatCheckboxModule, MatMenuModule, MatSlideToggleModule],
  template: `
    @if (items().length) {
      @if (selectable() && selectedItems().length) {
        <div class="resource-selection" role="toolbar" aria-label="Actions sur la sélection">
          <span aria-live="polite">
            {{ selectedItems().length }} {{ selectionUnit() }} sélectionné(s)
          </span>
          @for (action of selectionActions(); track action.id) {
            @if (isSelectionActionVisible(action)) {
              <button
                matButton="outlined"
                type="button"
                [disabled]="disabled()"
                (click)="requestSelectionAction(action)"
              >
                {{ action.labelFor?.(selectedRows()) ?? action.label }}
              </button>
            }
          }
          <button matButton="text" type="button" [disabled]="disabled()" (click)="clearSelection()">
            Tout désélectionner
          </button>
        </div>
      }
      <div class="resource-groups">
        @for (group of groupedItems(); track group.id) {
          <section class="resource-group" [attr.aria-label]="group.label || null">
            @if (group.label) {
              <header class="resource-group-header">
                @if (group.icon) {
                  <span class="resource-group-icon" [attr.data-tone]="group.tone ?? 'default'">
                    <app-icon [name]="group.icon" />
                  </span>
                }
                <h2>{{ group.label }}</h2>
                <span class="resource-group-count">
                  {{ group.items.length }}
                  @if (group.countUnit) {
                    {{
                      group.items.length === 1 ? group.countUnit.singular : group.countUnit.plural
                    }}
                  }
                </span>
              </header>
            }
            <ul class="resource-list">
              @for (item of group.items; track item.id) {
                <li
                  class="resource-item"
                  [class.has-selection]="selectable()"
                  [class.has-icon]="showIcon()"
                  [class.has-top-right-menu]="menuPlacement() === 'top-right'"
                >
                  @if (selectable()) {
                    <mat-checkbox
                      [checked]="selectedIds().has(item.id)"
                      [disabled]="disabled()"
                      [aria-label]="'Sélectionner ' + item.name"
                      (change)="toggleSelection(item.id, $event.checked)"
                    />
                  }
                  @if (showIcon()) {
                    <span class="resource-icon"><app-icon [name]="icon()" /></span>
                  }
                  <div class="resource-title">
                    <h3>
                      @if (openMode() === 'title') {
                        <button
                          class="resource-title-open"
                          type="button"
                          [attr.aria-label]="'Ouvrir ' + item.name"
                          (click)="open.emit(item.id)"
                        >
                          {{ item.name }}
                        </button>
                      } @else {
                        {{ item.name }}
                      }
                    </h3>
                    @if (item.subtitle) {
                      <p>{{ item.subtitle }}</p>
                    }
                  </div>
                  <div class="resource-tags">
                    @for (tag of item.tags; track tag.label) {
                      <span
                        class="resource-tag"
                        [class]="'resource-tag_' + (tag.tone ?? 'neutral')"
                      >
                        {{ tag.label }}
                      </span>
                    }
                  </div>
                  @if (item.meta) {
                    <span class="resource-meta">{{ item.meta }}</span>
                  }
                  @if (item.toggle; as toggle) {
                    <mat-slide-toggle
                      class="resource-toggle"
                      [checked]="toggle.checked"
                      [disabled]="disabled() || (toggle.disabled ?? false)"
                      [aria-label]="toggle.ariaLabel"
                      (change)="requestToggle(item, $event)"
                    />
                  }
                  <div
                    class="resource-actions"
                    [class.resource-actions_top-right]="menuPlacement() === 'top-right'"
                  >
                    @if (openMode() === 'button') {
                      <button
                        matButton="outlined"
                        type="button"
                        [disabled]="disabled()"
                        (click)="open.emit(item.id)"
                      >
                        {{ openLabel() }}
                      </button>
                    }
                    @if (visibleActions(item).length) {
                      <button
                        matIconButton
                        type="button"
                        [disabled]="disabled()"
                        [matMenuTriggerFor]="menu"
                        [attr.aria-label]="'Plus d’actions pour ' + item.name"
                      >
                        <app-icon name="more" />
                      </button>
                      <mat-menu #menu="matMenu">
                        @for (action of visibleActions(item); track action.id) {
                          <button
                            mat-menu-item
                            type="button"
                            [class.resource-menu-item_danger]="action.tone === 'danger'"
                            (click)="requestAction(action, item)"
                          >
                            {{ action.label }}
                          </button>
                        }
                      </mat-menu>
                    }
                  </div>
                </li>
              }
            </ul>
          </section>
        }
      </div>
    } @else {
      <p class="resource-empty">{{ emptyMessage() }}</p>
    }
  `,
  styleUrl: './resource-list.component.scss',
})
export class ResourceListComponent {
  private readonly dialog = inject(MatDialog);

  readonly items = input.required<readonly ResourceListItem[]>();
  readonly actions = input<readonly DataTableRowAction[]>([]);
  readonly icon = input<IconName>('pack');
  readonly showIcon = input(true);
  readonly openLabel = input('Ouvrir');
  readonly openMode = input<'button' | 'title' | 'none'>('button');
  readonly menuPlacement = input<'inline' | 'top-right'>('inline');
  readonly selectionUnit = input('élément(s)');
  readonly emptyMessage = input('Aucun résultat.');
  readonly groupDefinitions = input<readonly ResourceListGroup[]>([]);
  readonly selectable = input(false);
  readonly disabled = input(false);
  readonly selectionActions = input<readonly ResourceListSelectionAction[]>([]);
  readonly selectedIds = model<ReadonlySet<string>>(new Set());
  readonly selectedItems = computed(() =>
    this.items().filter((item) => this.selectedIds().has(item.id)),
  );
  protected readonly selectedRows = computed(() =>
    this.selectedItems().map((item) => this.rowData(item)),
  );
  protected readonly groupedItems = computed(() => {
    const groups = new Map<
      string,
      {
        id: string;
        label?: string;
        icon?: IconName;
        tone?: ResourceListGroup['tone'];
        countUnit?: ResourceListGroup['countUnit'];
        items: ResourceListItem[];
      }
    >();
    for (const definition of this.groupDefinitions()) {
      groups.set(definition.id, { ...definition, items: [] });
    }
    for (const item of this.items()) {
      const key = item.group?.id ?? '';
      let group = groups.get(key);
      if (!group) {
        group = {
          id: key || 'ungrouped',
          label: item.group?.label,
          icon: item.group?.icon,
          tone: item.group?.tone,
          countUnit: item.group?.countUnit,
          items: [],
        };
        groups.set(key, group);
      }
      group.items.push(item);
    }
    return [...groups.values()];
  });

  readonly open = output<string>();
  readonly action = output<DataTableActionEvent>();
  readonly toggleChange = output<ResourceListToggleEvent>();
  readonly selectionAction = output<{ actionId: string; rows: readonly DataTableRow[] }>();

  protected visibleActions(item: ResourceListItem): readonly DataTableRowAction[] {
    const row = this.rowData(item);
    return this.actions().filter((action) => !action.visibleWhen || action.visibleWhen(row));
  }

  protected isSelectionActionVisible(action: ResourceListSelectionAction): boolean {
    const rows = this.selectedRows();
    return rows.length > 0 && (!action.visibleWhen || action.visibleWhen(rows));
  }

  protected toggleSelection(id: string, checked: boolean): void {
    this.selectedIds.update((current) => {
      const next = new Set(current);
      if (checked) next.add(id);
      else next.delete(id);
      return next;
    });
  }

  protected clearSelection(): void {
    this.selectedIds.set(new Set());
  }

  protected requestToggle(item: ResourceListItem, event: MatSlideToggleChange): void {
    const checked = event.checked;
    const confirmation = checked ? undefined : item.toggle?.confirmOff;
    if (!confirmation) {
      this.toggleChange.emit({ id: item.id, checked });
      return;
    }
    // L'interrupteur reste sur son état réel tant que la confirmation n'est pas donnée.
    event.source.checked = !checked;
    this.dialog
      .open(ConfirmationDialogComponent, { data: confirmation })
      .afterClosed()
      .pipe(take(1))
      .subscribe((confirmed) => {
        if (confirmed) this.toggleChange.emit({ id: item.id, checked });
      });
  }

  protected requestAction(action: DataTableRowAction, item: ResourceListItem): void {
    const row = this.rowData(item);
    const confirmation = action.confirmation?.(row);
    if (!confirmation) {
      this.action.emit({ actionId: action.id, row });
      return;
    }
    this.dialog
      .open(ConfirmationDialogComponent, { data: confirmation })
      .afterClosed()
      .pipe(take(1))
      .subscribe((confirmed) => {
        if (confirmed) this.action.emit({ actionId: action.id, row });
      });
  }

  protected requestSelectionAction(action: ResourceListSelectionAction): void {
    const rows = this.selectedRows();
    const confirmation = action.confirmation?.(rows);
    if (!confirmation) {
      this.selectionAction.emit({ actionId: action.id, rows });
      return;
    }
    this.dialog
      .open(ConfirmationDialogComponent, { data: confirmation })
      .afterClosed()
      .pipe(take(1))
      .subscribe((confirmed) => {
        if (confirmed) this.selectionAction.emit({ actionId: action.id, rows });
      });
  }

  private rowData(item: ResourceListItem): DataTableRow {
    return item.rowData ?? { id: item.id, name: item.name };
  }
}
