import { TestBed } from '@angular/core/testing';
import { FormControl } from '@angular/forms';
import { SchemaFormFieldComponent } from './schema-form-field.component';

describe('SchemaFormField numeric contracts', () => {
  for (const numericValue of [true, false]) {
    it(
      numericValue
        ? 'emits numbers for typed configuration controls'
        : 'preserves strings for record form controls',
      () => {
        const fixture = TestBed.createComponent(SchemaFormFieldComponent);
        const control = new FormControl<number | string | null>(numericValue ? 12 : '12');
        fixture.componentRef.setInput('label', 'Qualité');
        fixture.componentRef.setInput('type', 'number');
        fixture.componentRef.setInput('numericValue', numericValue);
        fixture.componentRef.setInput('control', control);
        fixture.detectChanges();
        const input = fixture.nativeElement.querySelector('input') as HTMLInputElement;
        input.value = '22';
        input.dispatchEvent(new Event('input', { bubbles: true }));
        expect(control.value).toBe(numericValue ? 22 : '22');
      },
    );
  }
});
