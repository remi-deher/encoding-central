import { describe, expect, it } from 'vitest';
import { LocalWorkerStoreState } from './local-worker';
import { receivedPluginItems, syncSummary } from './local-worker-views';

const state = (desired: number, applied: number, status = 'applied'): LocalWorkerStoreState => ({
  workerId: 'w',
  desired: {
    revision: desired,
    items: [
      { kind: 'plugin', id: 'a', version: '1' },
      { kind: 'plugin', id: 'b', version: '2' },
      { kind: 'tool', id: 't', version: '1' },
    ],
  },
  applied: { revision: applied, items: [{ kind: 'plugin', id: 'a', version: '1', status }] },
});

describe('local worker views', () => {
  it('signale une synchronisation à jour, en attente, en échec ou indisponible', () => {
    expect(syncSummary(state(2, 2)).label).toBe('À jour');
    expect(syncSummary(state(3, 2)).label).toBe('En attente');
    expect(syncSummary(state(2, 2, 'failed')).label).toBe('Échec partiel');
    expect(syncSummary(null).label).toBe('Indisponible');
  });

  it('liste les plugins appliqués puis ceux demandés mais non confirmés, sans les outils', () => {
    const items = receivedPluginItems(state(2, 1));
    expect(items.map((item) => [item.id, item.badge.label])).toEqual([
      ['a', 'Appliqué'],
      ['b', 'En attente'],
    ]);
  });
});
