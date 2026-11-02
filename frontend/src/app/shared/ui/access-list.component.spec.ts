import { TestBed } from '@angular/core/testing';
import { AccessListComponent, AccessListRow } from './access-list.component';

describe('AccessListComponent', () => {
  const rows: readonly AccessListRow[] = [
    {
      id: 'u1',
      title: 'admin',
      kind: 'user',
      details: ['Créé le 18/10/2026'],
      badges: [
        { label: 'Administrateur', tone: 'accent', plain: true },
        { label: 'Actif', tone: 'success' },
      ],
    },
    {
      id: 't1',
      title: 'Claude',
      kind: 'token',
      monoDetail: 'enc_pat_6c5d3977…',
      details: ['Sans expiration'],
      badges: [{ label: 'Jamais utilisé', tone: 'warning' }],
    },
  ];

  function create(actions = [{ id: 'delete', label: 'Supprimer', tone: 'danger' as const }]) {
    const fixture = TestBed.createComponent(AccessListComponent);
    fixture.componentRef.setInput('rows', rows);
    fixture.componentRef.setInput('actions', actions);
    fixture.componentRef.setInput('emptyMessage', 'Aucun accès.');
    fixture.detectChanges();
    return fixture;
  }

  it('renders one line per row with its details and labelled state badges', () => {
    const host = create().nativeElement as HTMLElement;
    expect(host.querySelectorAll('.access-row').length).toBe(2);
    expect(host.textContent).toContain('Créé le 18/10/2026');
    expect(host.textContent).toContain('enc_pat_6c5d3977…');
    expect(host.textContent).toContain('Jamais utilisé');
  });

  it('exposes a single labelled actions menu per row', () => {
    const host = create().nativeElement as HTMLElement;
    const buttons = host.querySelectorAll('button[aria-label^="Autres actions pour"]');
    expect(buttons.length).toBe(2);
    expect(buttons[1].getAttribute('aria-label')).toBe('Autres actions pour Claude');
  });

  it('hides the menu when no action is visible for a row', () => {
    const fixture = create([]);
    expect(
      (fixture.nativeElement as HTMLElement).querySelectorAll('button[aria-label]').length,
    ).toBe(0);
  });

  it('shows the empty message without rows', () => {
    const fixture = create();
    fixture.componentRef.setInput('rows', []);
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Aucun accès.');
  });
});
