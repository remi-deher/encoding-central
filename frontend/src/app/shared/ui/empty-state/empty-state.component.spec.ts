import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { EmptyStateComponent } from './empty-state.component';

@Component({
  standalone: true,
  imports: [EmptyStateComponent],
  template: `
    <app-empty-state icon="search" title="Aucun résultat" message="Essayez un autre filtre.">
      <button type="button">Réinitialiser</button>
    </app-empty-state>
  `,
})
class EmptyStateHostComponent {}

describe('EmptyStateComponent', () => {
  it('announces the empty state and projects its action', () => {
    const fixture = TestBed.createComponent(EmptyStateHostComponent);
    fixture.detectChanges();
    const root = fixture.nativeElement.querySelector('[role="status"]') as HTMLElement;

    expect(root.getAttribute('aria-live')).toBe('polite');
    expect(root.textContent).toContain('Aucun résultat');
    expect(root.textContent).toContain('Essayez un autre filtre.');
    expect(root.querySelector('button')?.textContent).toContain('Réinitialiser');
    expect(root.querySelector('app-icon')).not.toBeNull();
  });
});
