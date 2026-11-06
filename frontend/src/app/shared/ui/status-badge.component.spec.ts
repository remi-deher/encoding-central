import { TestBed } from '@angular/core/testing';
import { StatusBadgeComponent } from './status-badge.component';

describe('StatusBadgeComponent', () => {
  function render(label: string, tone?: string) {
    const fixture = TestBed.createComponent(StatusBadgeComponent);
    fixture.componentRef.setInput('label', label);
    if (tone) fixture.componentRef.setInput('tone', tone);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows its label with the neutral tone by default', () => {
    const badge = render('En ligne').querySelector('.status-badge')!;
    expect(badge.textContent).toContain('En ligne');
    expect(badge.classList).toContain('tone-neutral');
  });

  it('applies the requested tone', () => {
    expect(render('Erreur', 'danger').querySelector('.status-badge')!.classList).toContain(
      'tone-danger',
    );
  });

  it('shows an icon alongside the text and maps the legacy ok tone to success', () => {
    const badge = render('OK', 'ok').querySelector('.status-badge')!;
    expect(badge.textContent).toContain('OK');
    expect(badge.querySelector('app-icon')).not.toBeNull();
    expect(badge.classList).toContain('tone-success');
  });
});
