import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { AiClusterView, AiNodeAvailability } from '../../../shared/models/ai-tools';
import { aiTypeLabel } from '../../ai/components/ai-model-selection';

type Tone = 'ok' | 'warning' | 'info' | 'muted';
interface NodeState {
  readonly nodeId: string;
  readonly name: string;
  readonly label: string;
  readonly tone: Tone;
  readonly detail: string | null;
}
interface ProviderRow {
  readonly id: string;
  readonly name: string;
  readonly replicated: boolean;
  readonly reconnect: number;
  readonly nodes: readonly NodeState[];
}

const REPLICATED = ['codex-cli', 'claude-cli', 'openai', 'anthropic'] as const;
const WHEN = new Intl.DateTimeFormat('fr-FR', {
  day: '2-digit',
  month: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
});

/** Date reçue d'un autre nœud : une valeur absente ou illisible (nœud d'une autre version) ne doit jamais faire échouer l'affichage. */
export function formatSyncedAt(value: unknown): string | null {
  if (typeof value !== 'string' && typeof value !== 'number') return null;
  const time = new Date(value).getTime();
  return Number.isNaN(time) ? null : WHEN.format(time).replace(',', '');
}

/** Où chaque connexion IA du cluster est présente, et si chaque nœud est à jour (état déclaré par le nœud lui-même). */
@Component({
  selector: 'app-ai-replication-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @for (row of rows(); track row.id) {
      @if (row.reconnect) {
        <p class="banner" role="status">
          <strong>{{ row.name }}</strong> est enregistré dans le cluster mais doit être reconnecté
          sur {{ row.reconnect }} nœud{{ row.reconnect > 1 ? 's' : '' }}.
        </p>
      }
    }
    @for (row of rows(); track row.id) {
      <article>
        <header>
          <h4>{{ row.name }}</h4>
          <span>{{
            row.replicated ? 'Connexion partagée par le cluster' : 'Local à chaque nœud'
          }}</span>
        </header>
        <ul>
          @for (node of row.nodes; track node.nodeId) {
            <li [class]="'tone-' + node.tone">
              <strong>{{ node.name }}</strong>
              <span class="state">{{ node.label }}</span>
              @if (node.detail) {
                <small>{{ node.detail }}</small>
              }
            </li>
          }
        </ul>
      </article>
    } @empty {
      <p>Disponibilité du cluster indisponible.</p>
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--space-2);
    }
    article {
      padding: var(--space-3) var(--space-4);
      background: var(--surface-container);
      border-radius: var(--radius-lg);
    }
    header {
      display: flex;
      justify-content: space-between;
      flex-wrap: wrap;
      gap: var(--space-2);
    }
    h4,
    p {
      margin: 0;
    }
    header span,
    small {
      color: var(--text-secondary);
    }
    ul {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-2);
      margin: var(--space-2) 0 0;
      padding: 0;
      list-style: none;
    }
    li {
      display: flex;
      flex-wrap: wrap;
      align-items: baseline;
      gap: var(--space-2);
      padding: 4px 12px;
      border-radius: var(--radius-pill);
      background: var(--surface-container-highest);
    }
    .state::before {
      content: '● ';
    }
    .tone-ok .state {
      color: var(--success);
    }
    .tone-warning {
      background: var(--warning-soft);
    }
    .tone-warning .state {
      color: var(--warning);
      font-weight: 700;
    }
    .tone-info .state {
      color: var(--text-primary);
    }
    .tone-muted .state {
      color: var(--text-secondary);
    }
    .banner {
      padding: var(--space-2) var(--space-4);
      border-radius: var(--radius-lg);
      background: var(--warning-soft);
      color: var(--text-primary);
    }
  `,
})
export class AiReplicationPanelComponent {
  readonly view = input.required<AiClusterView>();
  /** Limite l'affichage à un fournisseur (tiroir de détail). */
  readonly only = input<string | null>(null);

  readonly rows = computed<readonly ProviderRow[]>(() => {
    const nodes = this.view().nodes;
    if (!nodes.length) return [];
    return [...REPLICATED, 'ollama']
      .filter((id) => !this.only() || id === this.only())
      .map((id) => {
        const replicated = id !== 'ollama';
        const latest = Math.max(
          0,
          ...nodes.flatMap(
            (node) =>
              node.credentials?.find((c) => c.provider === id && c.state !== 'absent')?.revision ??
              0,
          ),
        );
        const states = nodes.map((node) => this.nodeState(node, id, replicated, latest));
        return {
          id,
          name: aiTypeLabel(id),
          replicated,
          reconnect: nodes.filter((node) => node.reconnect?.includes(id)).length,
          nodes: states,
        };
      });
  });

  private nodeState(
    node: AiNodeAvailability,
    id: string,
    replicated: boolean,
    latest: number,
  ): NodeState {
    const base = { nodeId: node.nodeId, name: node.name };
    if (!node.reachable)
      return { ...base, label: 'Injoignable', tone: 'muted', detail: 'État inconnu' };
    if (!replicated)
      return node.providers.includes(id)
        ? { ...base, label: 'Disponible', tone: 'ok', detail: null }
        : { ...base, label: 'Non détecté', tone: 'muted', detail: null };
    if (node.reconnect?.includes(id))
      return {
        ...base,
        label: 'À reconnecter',
        tone: 'warning',
        detail: 'Connexion refusée sur ce nœud',
      };
    const status = node.credentials?.find((c) => c.provider === id);
    if (!status)
      return { ...base, label: 'État inconnu', tone: 'muted', detail: 'Nœud à mettre à jour ?' };
    if (status.state === 'absent')
      return { ...base, label: 'Aucune connexion', tone: 'muted', detail: null };
    const behind = status.state === 'pending' || (status.revision ?? 0) < latest;
    const when = formatSyncedAt(status.syncedAt);
    return behind
      ? {
          ...base,
          label: 'En attente',
          tone: 'info',
          detail: 'Version plus récente en cours de réception',
        }
      : { ...base, label: 'À jour', tone: 'ok', detail: when };
  }
}
