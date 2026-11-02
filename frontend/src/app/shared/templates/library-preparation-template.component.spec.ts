import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { LibraryPreparationTemplateComponent } from './library-preparation-template.component';

@Component({
  imports: [LibraryPreparationTemplateComponent],
  template: `
    <app-library-preparation-template title="Bibliothèques" [items]="[]" [actionOpen]="opened()">
      <ng-template #actionContent><input aria-label="Nom de la bibliothèque" /></ng-template>
      <ng-template #actionFooterActions><button>Enregistrer</button></ng-template>
    </app-library-preparation-template>
  `,
})
class HostComponent {
  opened = signal(true);
}

describe('Library preparation action drawer', () => {
  it('renders projected forms and actions, destroys them on close and recreates them on reopen', () => {
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const input = element.querySelector('input[aria-label="Nom de la bibliothèque"]');
    expect(input).not.toBeNull();
    expect(element.textContent).toContain('Enregistrer');
    fixture.componentInstance.opened.set(false);
    fixture.changeDetectorRef.markForCheck();
    fixture.detectChanges();
    expect(element.querySelector('input[aria-label="Nom de la bibliothèque"]')).toBeNull();
    expect(element.textContent).not.toContain('Enregistrer');
    fixture.componentInstance.opened.set(true);
    fixture.changeDetectorRef.markForCheck();
    fixture.detectChanges();
    expect(element.querySelector('input[aria-label="Nom de la bibliothèque"]')).not.toBeNull();
    expect(element.querySelector('input[aria-label="Nom de la bibliothèque"]')).not.toBe(input);
  });
});
