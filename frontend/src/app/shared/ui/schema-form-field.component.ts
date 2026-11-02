import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';

export interface SchemaFormOption {
  readonly value: string;
  readonly label: string;
}

export type SchemaFormFieldType =
  'text' | 'select' | 'number' | 'password' | 'textarea' | 'email' | 'url' | 'date';

/** Rendu Material commun d'un champ primitif défini par un schéma. */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-schema-form-field',
  imports: [MatFormFieldModule, MatInputModule, MatSelectModule, ReactiveFormsModule],
  template: `
    <mat-form-field appearance="outline" subscriptSizing="dynamic">
      <mat-label>{{ label() }}</mat-label>
      @if (type() === 'select') {
        <mat-select [formControl]="control()" (selectionChange)="selectionChanged.emit()">
          @for (option of options(); track option.value) {
            <mat-option [value]="option.value">{{ option.label }}</mat-option>
          }
        </mat-select>
      } @else if (type() === 'textarea') {
        <textarea
          matInput
          [formControl]="control()"
          [attr.rows]="rows()"
          [attr.placeholder]="placeholder() || null"
          [attr.maxlength]="maxLength() ?? null"
        ></textarea>
      } @else if (type() === 'number' && numericValue()) {
        <input
          matInput
          type="number"
          [formControl]="control()"
          [attr.placeholder]="placeholder() || null"
          [attr.min]="min() ?? null"
          [attr.max]="max() ?? null"
          [attr.step]="step() ?? null"
        />
      } @else {
        <input
          matInput
          [type]="type()"
          [formControl]="control()"
          [attr.autocomplete]="autocomplete() || null"
          [attr.placeholder]="placeholder() || null"
          [attr.min]="min() ?? null"
          [attr.max]="max() ?? null"
          [attr.step]="step() ?? null"
          [attr.minlength]="minLength() ?? null"
          [attr.maxlength]="maxLength() ?? null"
        />
      }
      @if (hint()) {
        <mat-hint>{{ hint() }}</mat-hint>
      }
      @if (error()) {
        <mat-error>{{ error() }}</mat-error>
      }
    </mat-form-field>
  `,
  styles: `
    :host {
      display: block;
      min-width: 0;
    }
    mat-form-field {
      width: 100%;
    }
  `,
})
export class SchemaFormFieldComponent {
  /** Opt in for number-valued controls; record forms keep their existing string contract. */
  readonly numericValue = input(false);
  readonly label = input.required<string>();
  readonly type = input.required<SchemaFormFieldType>();
  readonly control = input.required<FormControl<any>>();
  readonly options = input<readonly SchemaFormOption[]>([]);
  readonly hint = input('');
  readonly error = input('');
  readonly placeholder = input('');
  readonly autocomplete = input('');
  readonly min = input<number | undefined>();
  readonly max = input<number | undefined>();
  readonly step = input<number | undefined>();
  readonly minLength = input<number | undefined>();
  readonly maxLength = input<number | undefined>();
  readonly rows = input(4);
  readonly selectionChanged = output<void>();
}
