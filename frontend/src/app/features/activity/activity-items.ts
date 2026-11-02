import { AiAssistantActivity } from '../../shared/models/ai-tools';
import {
  executionStateLabel,
  ExecutionResponse,
  ExecutionState,
} from '../../shared/models/execution';
import { formatFileSize } from '../../shared/models/file-size';
import { formatRelativeTime } from '../../shared/models/date-format';

export type ActivityKind = 'execution' | 'ai';
export type ActivityGroup = 'active' | 'queue' | 'errors' | 'cancelled' | 'done';
export type ActivityTone = 'accent' | 'neutral' | 'danger' | 'success' | 'warning';
export const ACTIVITY_STATES: Readonly<Record<ActivityGroup, readonly ExecutionState[]>> = {
  active: ['running', 'cancelling'],
  queue: ['queued'],
  errors: ['failed'],
  cancelled: ['cancelled'],
  done: ['succeeded', 'deferred'],
};

export const ACTIVITY_GROUPS: readonly {
  readonly id: ActivityGroup;
  readonly label: string;
  readonly section: string;
}[] = [
  { id: 'active', label: 'En cours', section: 'En cours' },
  { id: 'queue', label: 'À traiter', section: 'À traiter' },
  { id: 'errors', label: 'Erreurs', section: 'Erreurs' },
  { id: 'cancelled', label: 'Annulés', section: 'Annulés' },
  { id: 'done', label: 'Terminés', section: 'Terminés' },
];

export interface ActivityItem {
  readonly key: string;
  readonly kind: ActivityKind;
  readonly group: ActivityGroup;
  readonly title: string;
  readonly subtitle: string;
  readonly stateLabel: string;
  readonly tone: ActivityTone;
  readonly progress: number | null;
  readonly time: string;
  readonly sortTime: number;
  readonly execution?: ExecutionResponse;
  readonly ai?: AiAssistantActivity;
}

const AI_PROVIDERS: Readonly<Record<string, string>> = {
  'codex-cli': 'Codex CLI',
  'claude-cli': 'Claude Code',
  ollama: 'Ollama',
  openai: 'OpenAI',
  anthropic: 'Anthropic',
};
const AI_OPERATIONS: Readonly<Record<string, string>> = {
  assistant: 'Assistant',
  'flow-generation': 'Génération de flow',
  'flow-refactor': 'Modification de flow',
  'flow-explain': 'Explication de flow',
};
const AI_STATUSES: Readonly<Record<string, string>> = {
  running: 'En cours',
  succeeded: 'Réussie',
  'proposal-ready': 'Proposition prête',
  'applied-to-editor': 'Appliquée à l’éditeur',
  rejected: 'Rejetée',
  failed: 'Échouée',
  cancelled: 'Annulée',
};
const AI_ERRORS: Readonly<Record<string, string>> = {
  quota: 'quota',
  authentication: 'authentification',
  timeout: 'délai dépassé',
  'provider-error': 'fournisseur',
  'remote-result-uncertain': 'résultat distant incertain après interruption',
};

export const aiProviderName = (provider: string): string => AI_PROVIDERS[provider] ?? provider;
export const aiOperationName = (operation?: string): string =>
  AI_OPERATIONS[operation ?? 'assistant'] ?? 'Opération IA';
export const aiStatusName = (status: string): string => AI_STATUSES[status] ?? status;
export const aiErrorName = (category: string): string => AI_ERRORS[category] ?? category;
export const aiDuration = (milliseconds: number): string =>
  milliseconds < 1000 ? `${milliseconds} ms` : `${(milliseconds / 1000).toFixed(1)} s`;

function time(value: string | null | undefined): number {
  const parsed = value ? Date.parse(value) : NaN;
  return Number.isNaN(parsed) ? 0 : parsed;
}

export function executionGroup(state: ExecutionResponse['state']): ActivityGroup {
  switch (state) {
    case 'queued':
      return 'queue';
    case 'running':
    case 'cancelling':
      return 'active';
    case 'failed':
      return 'errors';
    case 'cancelled':
      return 'cancelled';
    default:
      return 'done';
  }
}

function executionTone(state: ExecutionResponse['state']): ActivityTone {
  switch (state) {
    case 'running':
    case 'cancelling':
      return 'accent';
    case 'failed':
      return 'danger';
    case 'succeeded':
      return 'success';
    case 'deferred':
      return 'warning';
    default:
      return 'neutral';
  }
}

export function executionItem(execution: ExecutionResponse): ActivityItem {
  const group = executionGroup(execution.state);
  const instant =
    group === 'queue'
      ? execution.queuedAt
      : group === 'active'
        ? (execution.startedAt ?? execution.queuedAt)
        : (execution.finishedAt ?? execution.updatedAt);
  const subtitle = [
    execution.library,
    `${execution.flow} · v${execution.flowVersion}`,
    execution.node,
    group === 'done' || group === 'queue' ? null : execution.detail,
  ]
    .filter(Boolean)
    .join(' · ');
  return {
    key: 'execution:' + execution.id,
    kind: 'execution',
    group,
    title: execution.media.split(/[\\/]/).pop() || execution.media,
    subtitle,
    stateLabel: executionStateLabel(execution),
    tone: execution.ownership?.state === 'quarantined' ? 'warning' : executionTone(execution.state),
    progress: execution.state === 'running' ? execution.progress : null,
    time: formatRelativeTime(instant),
    sortTime: time(instant),
    execution,
  };
}

export function aiItem(activity: AiAssistantActivity): ActivityItem {
  const group: ActivityGroup =
    activity.status === 'running'
      ? 'active'
      : activity.status === 'failed'
        ? 'errors'
        : activity.status === 'cancelled'
          ? 'cancelled'
          : 'done';
  const instant = activity.status === 'running' ? activity.startedAt : activity.updatedAt;
  return {
    key: 'ai:' + activity.requestId,
    kind: 'ai',
    group,
    title: activity.conversationTitle?.trim() || aiOperationName(activity.operation),
    subtitle: [
      activity.conversationTitle?.trim() ? aiOperationName(activity.operation) : null,
      aiProviderName(activity.provider),
      activity.durationMilliseconds !== null ? aiDuration(activity.durationMilliseconds) : null,
      activity.errorCategory ? aiErrorName(activity.errorCategory) : null,
    ]
      .filter(Boolean)
      .join(' · '),
    stateLabel: aiStatusName(activity.status),
    tone:
      group === 'active'
        ? 'accent'
        : group === 'errors'
          ? 'danger'
          : group === 'cancelled'
            ? 'neutral'
            : 'success',
    progress: null,
    time: formatRelativeTime(instant),
    sortTime: time(instant),
    ai: activity,
  };
}

/** Taille avant/après lisible pour la fiche d’une exécution. */
export function metadataSize(metadata: ExecutionResponse['beforeMetadata'] | undefined): string {
  const size = metadata?.format?.size;
  return size != null ? formatFileSize(Number(size)) : '—';
}
