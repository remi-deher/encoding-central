import { reconciliationBannerPresentation } from './node-management';
import { ClusterReconciliationHealthStatus } from '../services/cluster-api';

const status: ClusterReconciliationHealthStatus = {
  enabled: true,
  disabledReason: null,
  discoveryAvailable: true,
  discoveryError: null,
  peers: [],
};

describe('Reconciliation diagnostics banner', () => {
  it('shows the blocking table, age and required action', () => {
    const banner = reconciliationBannerPresentation(
      {
        ...status,
        projections: [
          {
            eventId: 'event',
            table: 'flow_draft_backups',
            cause: 'immutable_snapshot_conflict',
            firstFailedAt: '2026-10-27T12:00:00Z',
            lastFailedAt: '2026-10-27T12:01:00Z',
            attempts: 1,
            action: 'Résolution explicite requise.',
          },
        ],
      },
      2,
    );
    expect(banner.isAlert).toBe(true);
    expect(banner.description).toContain('flow_draft_backups');
    expect(banner.description).toContain('2026-10-27T12:00:00Z');
    expect(banner.description).toContain('Résolution explicite requise.');
  });

  it('explains missing catch-up even when no projection is pending', () => {
    const banner = reconciliationBannerPresentation(
      { ...status, attributionBlockers: ['peer_catch_up_unconfirmed:peer-a'] },
      2,
    );
    expect(banner.isAlert).toBe(true);
    expect(banner.description).toContain('Rattrapage sortant non confirmé pour le pair peer-a');
  });
});
