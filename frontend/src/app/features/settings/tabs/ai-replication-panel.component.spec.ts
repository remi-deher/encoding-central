import { TestBed } from '@angular/core/testing';
import { AiReplicationPanelComponent, formatSyncedAt } from './ai-replication-panel.component';
import { AiClusterView } from '../../../shared/models/ai-tools';

describe('AiReplicationPanelComponent', () => {
  const view: AiClusterView = {
    nodes: [
      {
        nodeId: 'a',
        name: 'nas-51',
        providers: ['claude-cli'],
        reachable: true,
        credentials: [{ provider: 'claude-cli', state: 'synced', revision: 7, syncedAt: null }],
      },
      {
        nodeId: 'b',
        name: 'worker-3',
        providers: [],
        reachable: true,
        credentials: [{ provider: 'claude-cli', state: 'synced', revision: 6, syncedAt: null }],
        reconnect: ['codex-cli'],
      },
      { nodeId: 'c', name: 'worker-4', providers: [], reachable: false },
      { nodeId: 'd', name: 'ancien', providers: [], reachable: true },
    ],
  };

  function render(current: AiClusterView = view): string {
    TestBed.configureTestingModule({ imports: [AiReplicationPanelComponent] });
    const fixture = TestBed.createComponent(AiReplicationPanelComponent);
    fixture.componentRef.setInput('view', current);
    fixture.detectChanges();
    return fixture.nativeElement.textContent as string;
  }

  it('distingue à jour, en attente, à reconnecter, injoignable et état inconnu avec du texte', () => {
    const text = render();
    expect(text).toContain('À jour');
    expect(text).toContain('En attente');
    expect(text).toContain('À reconnecter');
    expect(text).toContain('Injoignable');
    expect(text).toContain('État inconnu');
  });

  it('annonce un fournisseur à reconnecter et n’invente pas de réplication pour Ollama', () => {
    const text = render();
    expect(text).toContain('doit être reconnecté sur 1 nœud');
    expect(text).toContain('Local à chaque nœud');
  });

  it('affiche la date .NET reçue et ignore une date illisible sans lever d’erreur', () => {
    const node = (syncedAt: unknown) =>
      ({
        nodeId: 'x',
        name: 'n',
        providers: [],
        reachable: true,
        credentials: [{ provider: 'claude-cli', state: 'synced', revision: 1, syncedAt }],
      }) as never;
    expect(render({ nodes: [node('2026-10-21T22:12:10.1234567+00:00')] })).toContain('À jour');
    for (const bad of ['pas une date', {}, '', 42e30]) {
      TestBed.resetTestingModule();
      expect(render({ nodes: [node(bad)] })).toContain('À jour');
    }
  });

  it('formate la date au format jj/mm hh:mm', () => {
    // The display is local: construct the expected calendar day in the test's timezone.
    expect(formatSyncedAt(new Date(2026, 9, 21, 12, 12, 10).toISOString())).toBe('21/10 12:12');
    expect(formatSyncedAt('2026-10-21T22:12:10.1234567Z')).toMatch(/^\d\d\/\d\d \d\d:\d\d$/);
    expect(formatSyncedAt(null)).toBeNull();
    expect(formatSyncedAt('x')).toBeNull();
  });
});
