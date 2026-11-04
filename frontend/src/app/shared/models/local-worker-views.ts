import { WorkerToolCapability } from './central';
import { LocalStoreAppliedItem, LocalWorkerStatus, LocalWorkerStoreState } from './local-worker';
import { formatLastSignal } from './worker-management';
import { ResourceSheetBadge, ResourceSheetSection } from '../ui/resource-sheet.component';

/** Libellés lisibles des états appliqués rapportés par le worker ; un état inconnu reste affiché tel quel. */
const APPLIED_STATUS: Readonly<Record<string, { label: string; tone: ResourceSheetBadge['tone'] }>> = {
  applied: { label: 'Appliqué', tone: 'success' },
  installed: { label: 'Appliqué', tone: 'success' },
  pending: { label: 'En attente', tone: 'warning' },
  failed: { label: 'Échec', tone: 'danger' },
};

export function appliedStatusBadge(item: LocalStoreAppliedItem): ResourceSheetBadge {
  const known = APPLIED_STATUS[item.status.toLowerCase()];
  return known
    ? { label: known.label, tone: known.tone }
    : { label: item.status, tone: 'neutral' };
}

/** Synchronisation : l'état souhaité par le central est-il celui que ce worker a appliqué ? */
export function syncSummary(state: LocalWorkerStoreState | null): {
  readonly label: string;
  readonly tone: ResourceSheetBadge['tone'];
  readonly detail: string;
} {
  if (!state)
    return { label: 'Indisponible', tone: 'warning', detail: 'L’état de synchronisation n’a pas pu être lu.' };
  const { desired, applied } = state;
  const failed = applied.items.filter((item) => item.status.toLowerCase() === 'failed').length;
  if (failed > 0)
    return { label: 'Échec partiel', tone: 'danger', detail: `${failed} élément(s) en échec sur ce worker.` };
  if (applied.revision < desired.revision)
    return {
      label: 'En attente',
      tone: 'warning',
      detail: `Révision ${applied.revision} appliquée sur ${desired.revision} demandée.`,
    };
  return { label: 'À jour', tone: 'success', detail: `Révision ${applied.revision} appliquée.` };
}

/** Lien avec le central et synchronisation, du seul point de vue de ce worker. */
export function centralLinkSections(
  status: LocalWorkerStatus | null,
  store: LocalWorkerStoreState | null,
  agentUnavailable: boolean,
  now = Date.now(),
): readonly ResourceSheetSection[] {
  const linked = status?.isEnrolled === true;
  const pending = !!status?.centralId && !linked;
  const sync = syncSummary(store);
  return [
    {
      id: 'central-link',
      title: 'Lien avec le central',
      caption: agentUnavailable ? 'Agent injoignable' : undefined,
      items: [
        {
          label: status?.centralName ?? 'Aucun central',
          badges: [
            linked
              ? { label: 'Rattaché', tone: 'success' }
              : pending
                ? { label: 'Demande en attente', tone: 'warning' }
                : { label: 'Non rattaché', tone: 'neutral' },
          ],
          detail: status?.enrollmentFailure ?? undefined,
        },
      ],
      fields: [
        { label: 'Identifiant du worker', value: status?.workerId || 'Indisponible', mono: true },
        {
          label: 'Dernier rapport de synchronisation',
          value: formatLastSignal(store?.applied.reportedAt, now) ?? 'Aucun rapport',
        },
      ],
    },
    {
      id: 'central-sync',
      title: 'Synchronisation',
      items: [{ label: 'État souhaité par le central', detail: sync.detail, badges: [{ label: sync.label, tone: sync.tone }] }],
      metrics: store
        ? [
            { label: 'Révision demandée', value: String(store.desired.revision) },
            { label: 'Révision appliquée', value: String(store.applied.revision) },
          ]
        : [],
    },
  ];
}

const PLUGIN_KINDS = new Set(['plugin']);

/** Plugins reçus du central : appliqués, sinon demandés et pas encore confirmés. */
export function receivedPluginItems(store: LocalWorkerStoreState | null): readonly {
  readonly id: string;
  readonly version: string;
  readonly badge: ResourceSheetBadge;
}[] {
  if (!store) return [];
  const applied = store.applied.items.filter((item) => PLUGIN_KINDS.has(item.kind));
  const appliedKeys = new Set(applied.map((item) => `${item.id}@${item.version}`));
  const waiting = store.desired.items
    .filter((item) => PLUGIN_KINDS.has(item.kind) && !appliedKeys.has(`${item.id}@${item.version}`))
    .map((item) => ({ id: item.id, version: item.version, badge: { label: 'En attente', tone: 'warning' as const } }));
  return [
    ...applied.map((item) => ({ id: item.id, version: item.version, badge: appliedStatusBadge(item) })),
    ...waiting,
  ];
}

export function detectedToolSections(
  tools: readonly WorkerToolCapability[],
  agentUnavailable: boolean,
): readonly ResourceSheetSection[] {
  return [
    {
      id: 'detected-tools',
      title: 'Outils détectés sur ce worker',
      caption: agentUnavailable ? 'Dernier état connu' : `${tools.length} outil(s)`,
      notice: tools.length ? undefined : 'Aucun outil détecté par l’agent de ce worker.',
      items: tools.map((tool) => {
        const usable = (tool.encoderChecks ?? []).filter((check) => check.status === 'usable').length;
        const detail = [
          tool.version ? `Version ${tool.version}` : null,
          tool.mediaTypes.length ? tool.mediaTypes.join(', ') : null,
          usable ? `${usable} encodeur(s) vérifié(s)` : null,
        ]
          .filter(Boolean)
          .join(' · ');
        return {
          label: tool.toolId,
          detail: detail || undefined,
          badges: [
            { label: 'Détecté', tone: 'success' as const },
            ...tool.accelerationTypes.map((type) => ({ label: type.toUpperCase(), tone: 'accent' as const })),
          ],
        };
      }),
    },
  ];
}

export function receivedPluginSections(
  store: LocalWorkerStoreState | null,
): readonly ResourceSheetSection[] {
  const plugins = receivedPluginItems(store);
  return [
    {
      id: 'received-plugins',
      title: 'Plugins reçus du central',
      caption: store ? `${plugins.length} plugin(s)` : 'Indisponible',
      notice: !store
        ? 'L’état de synchronisation n’a pas pu être lu.'
        : plugins.length
          ? undefined
          : 'Le central n’a demandé aucun plugin à ce worker.',
      items: plugins.map((plugin) => ({
        label: plugin.id,
        detail: `Version ${plugin.version}`,
        badges: [plugin.badge],
      })),
    },
  ];
}
