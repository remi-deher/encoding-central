import {
  WorkerActivityResponse,
  WorkerResponse,
  NODE_ROLE_LABELS,
} from '../../shared/models/central';
import { workerLifecycle, NODE_LIFECYCLE_LABELS, NODE_LIFECYCLE_TONES } from '../../shared/models/central-health';
import { runtimeEnvironmentLabel } from '../../shared/models/worker-management';
import {
  formatMemory,
  workerAcceleratorBadges,
  workerHardwareLabel,
  tidyProcessorName,
} from '../../shared/models/worker-hardware';
import type { ResourceSheetSection } from '../../shared/ui/resource-sheet.component';
import type { WorkerLibraryRecord } from './worker-catalog.utils';

/** Fiche « Général » du tiroir de détail d’un worker (fonction pure). */
export function buildWorkerGeneralSheet(
  node: WorkerResponse | null,
  activity: WorkerActivityResponse | null,
  effectiveMax: number | null,
  nodeLibraries: readonly WorkerLibraryRecord[],
): {
  readonly title: string;
  readonly subtitle: string;
  readonly sections: readonly ResourceSheetSection[];
} | null {
  {
    if (!node) return null;

    const inventory = node.inventory;
    const gpus = inventory?.gpus ?? [];
    const libraries = nodeLibraries.filter(
      (library) => library.isEnabled && library.mode !== 'unsupported',
    );
    const directLibraries = libraries.filter((library) => library.mode === 'direct').length;
    const transferLibraries = libraries.filter((library) => library.mode === 'transfer').length;
    const isWorker = node.roles.includes('worker');
    const lifecycle = workerLifecycle(node);
    const roleBadges = node.roles.map((role) => ({
      label: NODE_ROLE_LABELS[role],
      tone: 'accent' as const,
    }));
    const labelBadges = Object.entries(node.labels ?? {})
      .filter((entry): entry is [string, string] => Boolean(entry[1]))
      .map(([key, value]) => ({ label: `${key}: ${value}`, tone: 'neutral' as const }));

    const sections: readonly ResourceSheetSection[] = [
      {
        id: 'node-general-identity',
        title: 'Identité et rôles',
        caption: 'Membre du central',
        items: [
          {
            label: node.displayName?.trim() || node.name,
            badges: [
              {
                label: NODE_LIFECYCLE_LABELS[lifecycle],
                tone: NODE_LIFECYCLE_TONES[lifecycle],
              },
              ...roleBadges,
              ...labelBadges,
            ],
          },
        ],
        fields: [
          { label: 'Nom affiché', value: node.displayName?.trim() || node.name },
          { label: 'Nom technique', value: node.name },
          { label: 'Identifiant', value: node.id, mono: true },
        ],
      },
      {
        id: 'node-general-activity',
        title: 'Disponibilité et activité',
        caption: `Dernier signal · ${new Date(node.lastSeenAt).toLocaleString('fr-FR')}`,
        metrics: [
          {
            label: 'Tâches actives / limite',
            value: isWorker
              ? `${activity?.active ?? '—'} / ${effectiveMax ?? node.maxConcurrentTasks}`
              : 'Fonctions serveur',
            unavailable: isWorker && !activity,
          },
          {
            label: 'Réussies',
            value: activity?.succeeded.toString() ?? 'Indisponible',
            unavailable: !activity,
          },
          {
            label: 'Échecs',
            value: activity?.failed.toString() ?? 'Indisponible',
            unavailable: !activity,
          },
          {
            label: 'Annulations',
            value: activity?.cancelled.toString() ?? 'Indisponible',
            unavailable: !activity,
          },
          {
            label: 'Tâches alimentées par ce worker',
            value: activity?.sourceTasks.toString() ?? 'Indisponible',
            unavailable: !activity,
          },
          {
            label: 'Dernière activité',
            value: activity
              ? activity.lastActivity
                ? new Date(activity.lastActivity).toLocaleString('fr-FR')
                : 'Aucune activité enregistrée'
              : 'Indisponible',
            unavailable: !activity,
          },
        ],
      },
      {
        id: 'node-general-system',
        title: 'Système et ressources',
        caption: inventory ? workerHardwareLabel(node) : 'Inventaire non déclaré',
        metrics: inventory
          ? [
              {
                label: 'Processeur',
                value: inventory.processorModel?.trim()
                  ? tidyProcessorName(inventory.processorModel.trim())
                  : `${inventory.processorCount} cœurs`,
                detail: `${inventory.processorCount} cœurs · ${inventory.operatingSystem} · ${inventory.architecture}`,
              },
              {
                label: 'Mémoire totale disponible',
                value: formatMemory(inventory.memoryMb),
                detail: `${inventory.memoryMb} Mo`,
              },
              {
                label: 'Système',
                value: `${inventory.operatingSystem} · ${inventory.architecture}`,
              },
              {
                label: 'Environnement d’exécution',
                value: runtimeEnvironmentLabel(inventory.runtimeEnvironment),
              },
              {
                label: gpus.length > 1 ? 'Processeurs graphiques' : 'Processeur graphique',
                value: gpus.length
                  ? gpus.map((gpu) => gpu.name).join(' · ')
                  : inventory.gpus
                    ? 'Aucun détecté'
                    : 'Non déclaré',
                unavailable: !gpus.length,
                detail:
                  gpus
                    .map((gpu) =>
                      [gpu.vendor, gpu.device, gpu.driver ? `pilote ${gpu.driver}` : null]
                        .filter(Boolean)
                        .join(' · '),
                    )
                    .filter(Boolean)
                    .join(' ; ') || undefined,
              },
              {
                label: 'Accélérateurs',
                value: workerAcceleratorBadges(node).join(' · '),
                detail:
                  workerAcceleratorBadges(node)[0] === 'CPU'
                    ? 'Aucun accélérateur matériel vérifié : encodage sur CPU.'
                    : undefined,
              },
            ]
          : [
              {
                label: 'Matériel',
                value: 'Non déclaré',
                unavailable: true,
                detail: 'Disponible après le prochain signal du worker.',
              },
            ],
      },
      {
        id: 'node-general-libraries',
        title: 'Accès aux bibliothèques',
        caption: `${libraries.length} autorisée(s)`,
        metrics: [
          { label: 'Accès direct', value: `${directLibraries} bibliothèque(s)` },
          { label: 'Transfert WebDAV', value: `${transferLibraries} bibliothèque(s)` },
        ],
      },
    ];

    return {
      title: node.displayName?.trim() || node.name,
      subtitle: `${node.roles.map((role) => NODE_ROLE_LABELS[role]).join(' · ')} · ${NODE_LIFECYCLE_LABELS[lifecycle]}`,
      sections,
    };
  }
}


