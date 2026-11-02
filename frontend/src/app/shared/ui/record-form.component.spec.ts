import { BreakpointObserver } from '@angular/cdk/layout';
import { MatStepper } from '@angular/material/stepper';
import { By } from '@angular/platform-browser';
import { of } from 'rxjs';
import { TestBed } from '@angular/core/testing';
import { RecordFormComponent, RecordFormData } from './record-form.component';

const DATA: RecordFormData = {
  title: 'Créer',
  description: 'Description du formulaire',
  submitLabel: 'Enregistrer',
  fields: [{ id: 'name', label: 'Nom', type: 'text', required: true }],
};

describe('RecordFormComponent embedded', () => {
  function render(embedded: boolean) {
    const fixture = TestBed.createComponent(RecordFormComponent);
    fixture.componentRef.setInput('data', DATA);
    fixture.componentRef.setInput('embedded', embedded);
    fixture.detectChanges();
    return fixture;
  }

  it('keeps its own buttons when not embedded', () => {
    const element = render(false).nativeElement as HTMLElement;
    expect(element.querySelector('.form-actions')).not.toBeNull();
  });

  it('hides its buttons and description and exposes the footer actions when embedded', () => {
    const fixture = render(true);
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('.form-actions')).toBeNull();
    expect(element.querySelector('.step-description')).toBeNull();
    const form = fixture.componentInstance;
    expect(form.stepCount()).toBe(2);
    expect(form.primaryLabel()).toBe('Vérifier');
    expect(form.secondaryLabel()).toBe('Annuler');
  });

  it('cancels from the first step and refuses to advance an invalid step', () => {
    const fixture = render(true);
    const form = fixture.componentInstance;
    let cancelled = 0;
    form.cancelled.subscribe(() => cancelled++);
    form.secondary();
    expect(cancelled).toBe(1);
    form.primary();
    fixture.detectChanges();
    expect(form.stepIndex()).toBe(0);
  });
  it('uses vertical steps on a narrow screen and keeps embedded progress in its drawer', () => {
    TestBed.configureTestingModule({
      providers: [
        {
          provide: BreakpointObserver,
          useValue: { observe: () => of({ matches: true, breakpoints: {} }) },
        },
      ],
    });
    const fixture = render(false);
    expect(fixture.debugElement.query(By.directive(MatStepper)).componentInstance.orientation).toBe(
      'vertical',
    );
    fixture.componentRef.setInput('embedded', true);
    fixture.detectChanges();
    expect(fixture.debugElement.query(By.directive(MatStepper)).componentInstance.orientation).toBe(
      'horizontal',
    );
  });
});
