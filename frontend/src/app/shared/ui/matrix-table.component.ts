import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { StatusBadgeComponent, StatusTone } from './status-badge.component';

export interface MatrixCell {
  readonly label: string;
  readonly tone: StatusTone;
}
export interface MatrixHeader {
  readonly id: string;
  readonly label: string;
}

/** Tableau croisé en lecture seule : une cellule par couple ligne × colonne. */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-matrix-table',
  imports: [StatusBadgeComponent],
  template: `
    <div class="matrix-scroll" tabindex="0">
      <table>
        <caption class="visually-hidden">
          {{
            caption()
          }}
        </caption>
        <thead>
          <tr>
            <th scope="col">{{ cornerLabel() }}</th>
            @for (column of columns(); track column.id) {
              <th scope="col">{{ column.label }}</th>
            }
          </tr>
        </thead>
        <tbody>
          @for (row of rows(); track row.id) {
            <tr>
              <th scope="row">{{ row.label }}</th>
              @for (column of columns(); track column.id) {
                <td>
                  @if (cells()[row.id + '|' + column.id]; as cell) {
                    <app-status-badge [label]="cell.label" [tone]="cell.tone" />
                  }
                </td>
              }
            </tr>
          }
        </tbody>
      </table>
    </div>
  `,
  styles: `
    .matrix-scroll {
      overflow-x: auto;
    }
    table {
      border-collapse: collapse;
      width: 100%;
    }
    th,
    td {
      padding: var(--space-2, 8px) var(--space-3, 12px);
      text-align: left;
      border-bottom: 1px solid var(--border-subtle);
    }
    .visually-hidden {
      position: absolute;
      width: 1px;
      height: 1px;
      overflow: hidden;
      clip: rect(0 0 0 0);
    }
  `,
})
export class MatrixTableComponent {
  readonly caption = input.required<string>();
  readonly cornerLabel = input('');
  readonly rows = input.required<readonly MatrixHeader[]>();
  readonly columns = input.required<readonly MatrixHeader[]>();
  /** Clés de la forme `idLigne|idColonne`. */
  readonly cells = input.required<Readonly<Record<string, MatrixCell>>>();
}
