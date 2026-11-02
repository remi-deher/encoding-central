import { ChangeDetectionStrategy, Component, input, output, signal } from '@angular/core';
import { isFlowConfigurationFieldVisible } from '../../models/flow-configuration';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatCheckboxModule } from '@angular/material/checkbox';
import {
  FlowComponentConfigurationField,
  FlowConfigurationRecord,
} from '../../models/flow-component';

@Component({
  selector: 'app-configuration-record-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatCheckboxModule,
  ],
  template: ` <section [attr.aria-label]="field().label">
    <h3>{{ field().label }}</h3>
    <p>{{ field().description }}</p>
    @for (row of value(); track $index; let rowIndex = $index) {
      <details
        class="record"
        [open]="rowIndex === expandedIndex()"
        [attr.aria-label]="'Règle ' + (rowIndex + 1)"
      >
        <summary>
          <strong>Règle {{ rowIndex + 1 }}</strong
          ><span>{{ summary(row) }}</span>
        </summary>
        <div class="record-fields">
          @for (child of field().itemFields; track child.id) {
            @if (
              visible(child, row) &&
              (child.importance !== 'advanced' || advancedRows().has(rowIndex))
            ) {
              @if (child.type === 'boolean') {
                <mat-checkbox
                  [checked]="row[child.id] === true"
                  [disabled]="disabled()"
                  (change)="change(rowIndex, child, $event.checked)"
                  >{{ child.label }}</mat-checkbox
                >
              } @else {
                <mat-form-field appearance="outline" subscriptSizing="dynamic">
                  <mat-label>{{ child.label }}</mat-label>
                  @if (child.type === 'select') {
                    <mat-select
                      [value]="row[child.id]"
                      [disabled]="disabled()"
                      (selectionChange)="change(rowIndex, child, $event.value)"
                    >
                      @for (option of child.options; track option.value) {
                        <mat-option [value]="option.value">{{ option.label }}</mat-option>
                      }
                    </mat-select>
                  } @else {
                    <input
                      matInput
                      [type]="child.type === 'number' ? 'number' : 'text'"
                      [value]="row[child.id] ?? ''"
                      [disabled]="disabled()"
                      (input)="edit(rowIndex, child, $event)"
                    />
                  }
                  @if (child.description) {
                    <mat-hint>{{ child.description }}</mat-hint>
                  }
                </mat-form-field>
              }
            }
          }
          @if (hasAdvanced()) {
            <button matButton="text" type="button" (click)="toggleAdvanced(rowIndex)">
              {{
                advancedRows().has(rowIndex)
                  ? 'Réduire les options de sélection'
                  : 'Autres critères de sélection'
              }}
            </button>
          }
          <div class="actions">
            <button
              matButton="text"
              type="button"
              [disabled]="disabled() || rowIndex === 0"
              (click)="move(rowIndex, -1)"
              [attr.aria-label]="'Monter la règle ' + (rowIndex + 1)"
            >
              Monter
            </button>
            <button
              matButton="text"
              type="button"
              [disabled]="disabled() || rowIndex === value().length - 1"
              (click)="move(rowIndex, 1)"
              [attr.aria-label]="'Descendre la règle ' + (rowIndex + 1)"
            >
              Descendre
            </button>
            <button
              matButton="text"
              type="button"
              [disabled]="disabled() || value().length <= (field().minItems ?? 0)"
              (click)="remove(rowIndex)"
              [attr.aria-label]="'Supprimer la règle ' + (rowIndex + 1)"
            >
              Supprimer
            </button>
          </div>
        </div>
      </details>
    }
    <button
      matButton="outlined"
      type="button"
      [disabled]="disabled() || value().length >= (field().maxItems ?? 64)"
      (click)="add()"
    >
      Ajouter une règle
    </button>
  </section>`,
  styles: `
    :host {
      display: block;
      min-width: 0;
    }
    .record {
      padding: var(--space-4);
      margin-block: var(--space-3);
      background: var(--surface-container-low);
      border-radius: var(--radius-lg);
    }
    .record summary {
      cursor: pointer;
      min-height: 44px;
      overflow-wrap: anywhere;
    }
    .record summary span {
      display: block;
      margin-top: var(--space-2);
      color: var(--text-secondary);
      font-size: var(--font-body);
    }
    .record-fields {
      display: grid;
      gap: var(--space-3);
      padding-top: var(--space-3);
    }
    .actions {
      display: flex;
      flex-wrap: wrap;
    }
    p {
      color: var(--text-secondary);
    }
  `,
})
export class ConfigurationRecordListComponent {
  readonly field =
    input.required<Extract<FlowComponentConfigurationField, { type: 'record-list' }>>();
  readonly value = input<readonly FlowConfigurationRecord[]>([]);
  readonly disabled = input(false);
  readonly valueChange = output<readonly FlowConfigurationRecord[]>();
  protected readonly expandedIndex = signal(0);
  protected readonly advancedRows = signal<ReadonlySet<number>>(new Set());
  protected hasAdvanced(): boolean {
    return this.field().itemFields.some((field) => field.importance === 'advanced');
  }
  protected toggleAdvanced(index: number): void {
    const next = new Set(this.advancedRows());
    next.has(index) ? next.delete(index) : next.add(index);
    this.advancedRows.set(next);
  }
  protected summary(row: FlowConfigurationRecord): string {
    return (
      this.field()
        .itemFields.flatMap((field) => {
          if (!this.visible(field, row)) return [];
          const value = row[field.id];
          if (value === null || value === undefined || value === '' || value === 0) return [];
          if (field.importance === 'advanced' && value === field.defaultValue) return [];
          const label =
            field.type === 'select'
              ? (field.options.find((option) => option.value === value)?.label ?? String(value))
              : String(value);
          return [field.label + ' : ' + label];
        })
        .join(' · ') || 'Toutes les pistes, paramètres à définir'
    );
  }
  protected visible(field: FlowComponentConfigurationField, row: FlowConfigurationRecord): boolean {
    return isFlowConfigurationFieldVisible(field, row);
  }
  protected add(): void {
    const row: Record<string, string | number | boolean | null> = {};
    for (const field of this.field().itemFields) {
      const value = field.defaultValue;
      row[field.id] =
        typeof value === 'object'
          ? null
          : (value ?? (field.type === 'boolean' ? false : field.type === 'number' ? null : ''));
    }
    this.expandedIndex.set(this.value().length);
    this.valueChange.emit([...this.value(), row]);
  }
  protected edit(index: number, field: FlowComponentConfigurationField, event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.change(
      index,
      field,
      field.type === 'number' ? (value === '' ? null : Number(value)) : value,
    );
  }
  protected change(
    index: number,
    field: FlowComponentConfigurationField,
    value: string | number | boolean | null,
  ): void {
    this.valueChange.emit(
      this.value().map((row, i) => (i === index ? { ...row, [field.id]: value } : row)),
    );
  }
  protected remove(index: number): void {
    this.valueChange.emit(this.value().filter((_, i) => i !== index));
  }
  protected move(index: number, offset: number): void {
    const rows = [...this.value()];
    [rows[index], rows[index + offset]] = [rows[index + offset], rows[index]];
    this.valueChange.emit(rows);
  }
}
