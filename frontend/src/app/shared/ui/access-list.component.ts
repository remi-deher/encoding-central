import { ChangeDetectionStrategy, Component, inject, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatMenuModule } from '@angular/material/menu';
import { ConfirmationDialogComponent } from './confirmation-dialog.component';
import { ConfirmationDialogData } from './confirmation-dialog.component';
import { StatusBadgeComponent, StatusTone } from './status-badge.component';

export interface AccessListBadge {
  readonly label: string;
  readonly tone: StatusTone;
  /** Sans point d’état, pour une information qui n’est pas un état. */
  readonly plain?: boolean;
}

export interface AccessListRow {
  readonly id: string;
  readonly title: string;
  readonly kind: 'user' | 'token';
  readonly details: readonly string[];
  /** Première ligne de détail en police technique (préfixe de jeton). */
  readonly monoDetail?: string;
  readonly badges: readonly AccessListBadge[];
}

export interface AccessListAction<T extends AccessListRow = AccessListRow> {
  readonly id: string;
  readonly label: string;
  readonly tone?: 'default' | 'danger';
  readonly visibleWhen?: (row: T) => boolean;
  readonly confirmation?: (row: T) => ConfirmationDialogData;
}

export interface AccessListActionEvent<T extends AccessListRow = AccessListRow> {
  readonly actionId: string;
  readonly row: T;
}

/** Liste de comptes ou d’accès : une ligne lisible, une action ⋮ pour le reste. */
@Component({
  selector: 'app-access-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatMenuModule, StatusBadgeComponent],
  template: `
    @if (rows().length) {
      <ul class="access-list">
        @for (row of rows(); track row.id) {
          <li class="access-row">
            <span class="access-lead" [class.is-token]="row.kind === 'token'" aria-hidden="true">
              @if (row.kind === 'user') {
                {{ initial(row) }}
              } @else {
                <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                  <circle cx="8" cy="15" r="4" />
                  <path d="m11 12 9-9M16 7l3 3" />
                </svg>
              }
            </span>
            <div class="access-main">
              <strong>{{ row.title }}</strong>
              <div class="access-details">
                @if (row.monoDetail) {
                  <span class="mono">{{ row.monoDetail }}</span>
                }
                @for (detail of row.details; track $index) {
                  <span>{{ detail }}</span>
                }
              </div>
            </div>
            <div class="access-badges">
              @for (badge of row.badges; track badge.label) {
                @if (badge.plain) {
                  <span class="access-chip" [attr.data-tone]="badge.tone">{{ badge.label }}</span>
                } @else {
                  <app-status-badge [label]="badge.label" [tone]="badge.tone" />
                }
              }
            </div>
            @if (visibleActions(row).length) {
              <button
                matIconButton
                type="button"
                class="access-menu"
                [matMenuTriggerFor]="menu"
                [attr.aria-label]="'Autres actions pour ' + row.title"
              >
                <span aria-hidden="true">⋮</span>
              </button>
              <mat-menu #menu="matMenu">
                @for (action of visibleActions(row); track action.id) {
                  <button
                    mat-menu-item
                    type="button"
                    [class.danger]="action.tone === 'danger'"
                    (click)="request(action, row)"
                  >
                    {{ action.label }}
                  </button>
                }
              </mat-menu>
            }
          </li>
        }
      </ul>
    } @else {
      <p class="access-empty">{{ emptyMessage() }}</p>
    }
  `,
  styles: `
    :host {
      display: block;
      min-width: 0;
    }
    .access-list {
      display: grid;
      gap: 3px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .access-row {
      display: flex;
      align-items: center;
      gap: var(--space-4);
      min-width: 0;
      padding: var(--space-3) var(--space-4);
      background: var(--surface-container);
      border-radius: var(--radius-sm);
      transition: background-color 150ms var(--motion-emphasized, ease);
    }
    .access-row:hover {
      background: var(--surface-container-high);
    }
    .access-row:first-child {
      border-top-left-radius: var(--radius-xl);
      border-top-right-radius: var(--radius-xl);
    }
    .access-row:last-child {
      border-bottom-left-radius: var(--radius-xl);
      border-bottom-right-radius: var(--radius-xl);
    }
    .access-lead {
      flex: none;
      display: grid;
      place-items: center;
      width: 44px;
      height: 44px;
      border-radius: var(--radius-md);
      background: var(--accent-soft);
      color: var(--accent);
      font-size: var(--font-title-md, 18px);
      font-weight: 700;
      text-transform: uppercase;
    }
    .access-lead.is-token {
      background: var(--surface-container-highest);
      color: var(--text-secondary);
    }
    .access-lead svg {
      width: 22px;
      height: 22px;
    }
    .access-main {
      flex: 1;
      min-width: 0;
      display: grid;
      gap: 2px;
    }
    .access-main strong {
      font-weight: 600;
      overflow-wrap: anywhere;
    }
    .access-details {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1) var(--space-3);
      color: var(--text-muted);
      font-size: var(--font-label);
    }
    .mono {
      font-family: ui-monospace, 'JetBrains Mono', Consolas, monospace;
    }
    .access-badges {
      display: flex;
      flex-wrap: wrap;
      justify-content: flex-end;
      align-items: center;
      gap: var(--space-2);
    }
    .access-chip {
      padding: 2px var(--space-3);
      border-radius: var(--radius-pill);
      background: var(--surface-container-highest);
      color: var(--text-secondary);
      font-size: var(--font-label);
      font-weight: 600;
      white-space: nowrap;
    }
    .access-chip[data-tone='accent'] {
      background: var(--accent-soft);
      color: var(--accent);
    }
    .danger {
      color: var(--danger-strong);
    }
    .access-empty {
      margin: 0;
      padding: var(--space-4);
      color: var(--text-secondary);
      text-align: center;
    }
    @media (max-width: 720px) {
      .access-row {
        display: grid;
        grid-template-columns: 44px minmax(0, 1fr) auto;
        grid-template-areas:
          'lead main menu'
          'badges badges badges';
        align-items: center;
      }
      .access-lead {
        grid-area: lead;
      }
      .access-main {
        grid-area: main;
      }
      .access-menu {
        grid-area: menu;
      }
      .access-badges {
        grid-area: badges;
        justify-content: flex-start;
      }
    }
    @media (prefers-reduced-motion: reduce) {
      .access-row {
        transition: none;
      }
    }
  `,
})
export class AccessListComponent<T extends AccessListRow = AccessListRow> {
  private readonly dialog = inject(MatDialog);
  readonly rows = input.required<readonly T[]>();
  readonly actions = input<readonly AccessListAction<T>[]>([]);
  readonly emptyMessage = input('Aucun élément.');
  readonly rowAction = output<AccessListActionEvent<T>>();

  protected initial(row: T): string {
    return row.title.trim().charAt(0);
  }

  protected visibleActions(row: T): readonly AccessListAction<T>[] {
    return this.actions().filter((action) => action.visibleWhen?.(row) ?? true);
  }

  protected request(action: AccessListAction<T>, row: T): void {
    const confirmation = action.confirmation?.(row);
    if (!confirmation) {
      this.rowAction.emit({ actionId: action.id, row });
      return;
    }
    this.dialog
      .open(ConfirmationDialogComponent, { data: confirmation })
      .afterClosed()
      .subscribe((confirmed: boolean | undefined) => {
        if (confirmed) this.rowAction.emit({ actionId: action.id, row });
      });
  }
}
