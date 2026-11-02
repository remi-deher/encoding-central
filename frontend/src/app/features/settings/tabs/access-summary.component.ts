import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { ApiTokenResponse } from '../../../shared/models/api-token';
import { UserAccount } from '../../../shared/models/auth';
import { formatRelativeTime } from '../../../shared/models/date-format';

/** Bilan des accès, calculé uniquement à partir des comptes et jetons chargés. */
@Component({
  selector: 'app-access-summary',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="summary" aria-label="Résumé des accès">
      <div class="tile">
        <b>{{ activeUsers() }}</b
        ><span>{{ activeUsers() > 1 ? 'comptes actifs' : 'compte actif' }}</span>
      </div>
      <div class="tile">
        <b>{{ activeTokens().length }}</b
        ><span>{{ activeTokens().length > 1 ? 'jetons d’API actifs' : 'jeton d’API actif' }}</span>
      </div>
      <div class="tile" [class.attention]="unusedTokens() > 0">
        <b>{{ unusedTokens() }}</b
        ><span>{{ unusedTokens() > 1 ? 'jetons jamais utilisés' : 'jeton jamais utilisé' }}</span>
      </div>
      <div class="tile">
        <b class="when">{{ lastTokenUse() }}</b
        ><span>dernier accès par jeton</span>
      </div>
    </section>
  `,
  styles: `
    :host {
      display: block;
      min-width: 0;
    }
    .summary {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(170px, 1fr));
      gap: var(--space-2);
    }
    .tile {
      display: grid;
      gap: 2px;
      padding: var(--space-4);
      background: var(--surface-container);
      border-radius: var(--radius-xl);
    }
    b {
      font-size: var(--font-display-sm);
      font-weight: 700;
      line-height: 1.1;
      font-variant-numeric: tabular-nums;
    }
    b.when {
      font-size: var(--font-title-lg);
      line-height: 1.5;
    }
    span {
      font-size: var(--font-label);
      color: var(--text-muted);
    }
    .tile.attention {
      background: var(--warning-soft);
    }
    .tile.attention b {
      color: var(--warning);
    }
  `,
})
export class AccessSummaryComponent {
  readonly users = input.required<readonly UserAccount[]>();
  readonly tokens = input.required<readonly ApiTokenResponse[]>();

  protected readonly activeUsers = computed(() => this.users().filter((u) => u.isActive).length);
  protected readonly activeTokens = computed(() =>
    this.tokens().filter(
      (t) => t.isActive && !(t.expiresAt && new Date(t.expiresAt).getTime() <= Date.now()),
    ),
  );
  protected readonly unusedTokens = computed(
    () => this.activeTokens().filter((t) => !t.lastUsedAt).length,
  );
  protected readonly lastTokenUse = computed(() => {
    const times = this.tokens()
      .map((t) => (t.lastUsedAt ? new Date(t.lastUsedAt).getTime() : 0))
      .filter((t) => t > 0);
    return times.length ? formatRelativeTime(Math.max(...times)) : 'Aucun';
  });
}
