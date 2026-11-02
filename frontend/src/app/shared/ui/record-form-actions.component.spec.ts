import { TestBed } from '@angular/core/testing';
import { RecordFormActionsComponent } from './record-form-actions.component';
import { RecordFormComponent, RecordFormData } from './record-form.component';

const DATA: RecordFormData = {
  title: 'Créer',
  description: 'Description',
  submitLabel: 'Enregistrer',
  fields: [{ id: 'name', label: 'Nom', type: 'text', required: true }],
};

describe('RecordFormActionsComponent', () => {
  it('renders nothing without a form and the form buttons with one', () => {
    const form = TestBed.createComponent(RecordFormComponent);
    form.componentRef.setInput('data', DATA);
    form.componentRef.setInput('embedded', true);
    form.detectChanges();

    const fixture = TestBed.createComponent(RecordFormActionsComponent);
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelectorAll('button').length).toBe(0);

    fixture.componentRef.setInput('form', form.componentInstance);
    fixture.detectChanges();
    const labels = Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll('button'),
    ).map((button) => button.textContent?.trim());
    expect(labels).toEqual(['Annuler', 'Vérifier']);
  });
});
