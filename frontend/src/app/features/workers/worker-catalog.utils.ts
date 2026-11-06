import { formatMemory } from '../../shared/models/worker-hardware';
import type { ResourceSheetSection } from '../../shared/ui/resource-sheet.component';
import type { StatusTone } from '../../shared/ui/status-badge.component';
import type { DetailCard } from '../../shared/ui/detail-card-grid.component';
import type { GuidedStep } from '../../shared/ui/guided-setup.component';
import type { DataTableRow } from '../../shared/ui/data-table.component';
import {
  workerLifecycle,
  NODE_LIFECYCLE_LABELS,
  NODE_LIFECYCLE_TONES,
} from '../../shared/models/central-health';
import { formatRelativeTime, formatShortDateTime } from '../../shared/models/date-format';
import {
  formatWorkerLabel,
  WorkerRole,
  WorkerResponse,
  WorkerInventory,
  LibraryResponse,
  WorkerToolCapability,
  NODE_ROLE_LABELS,
} from '../../shared/models/central';
import type { WorkerProcessingSettings } from '../../shared/models/central';
import type {
  WorkerOverviewHardware,
  WorkerOverviewLibrary,
  WorkerOverviewTaskCapacity,
} from './worker-overview-card.component';
import type { LibraryWorkerAccessResponse } from '../../shared/ui/library-worker-access-types';
import type { LibraryWorkerAccessMode } from '../../shared/ui/library-worker-access-types';
import type { RecordFormData } from '../../shared/ui/record-form.component';

export interface WorkerLibraryRecord extends DataTableRow {
  readonly id: string;
  readonly name: string;
  readonly isEnabled: boolean;
  readonly mode: LibraryWorkerAccessMode;
  readonly modeLabel: string;
  readonly pathLabel: string;
}

export interface PendingWorkerEnrollmentResponse {
  readonly id: string;
  readonly name: string;
  readonly roles: readonly WorkerRole[];
  readonly maxConcurrentTasks: number;
  readonly capabilities: readonly WorkerToolCapability[];
  readonly createdAt: string;
  readonly expiresAt: string;
  readonly verificationCode?: string | null;
  readonly displayName?: string | null;
}

export interface PendingWorkerEnrollmentRecord extends DataTableRow {
  readonly id: string;
  readonly name: string;
  readonly verificationCode: string;
  readonly rolesLabel: string;
  readonly capabilitiesLabel: string;
  readonly expiresAtLabel: string;
}

export interface MemberCard {
  readonly id: string;
  readonly title: string;
  readonly subtitle: string;
  readonly statusLabel: string;
  readonly statusTone: StatusTone;
  readonly isMaster: boolean;
  readonly roles: readonly string[];
  readonly admissionEnabled: boolean | null;
  readonly admissionDisabled: boolean;
  readonly hardware: WorkerOverviewHardware;
  readonly taskCapacity: WorkerOverviewTaskCapacity;
  readonly meta: string;
  readonly sections: readonly ResourceSheetSection[];
  readonly libraries: readonly WorkerOverviewLibrary[];
}

export interface WorkerRecord extends DataTableRow {
  readonly id: string;
  readonly name: string;
  readonly roleFilter: string;
  readonly roleLabel: string;
  readonly libraryAccess: string;
  readonly workerLoad: string;
  readonly queueStatus: string;
  readonly state: string;
  readonly lastSignal: string;
}

export const NODE_ROLE_OPTIONS: readonly { readonly value: WorkerRole; readonly label: string }[] =
  [
    { value: 'server', label: 'Serveur' },
    { value: 'worker', label: 'Worker' },
  ];

export const ENROLLMENT_FORM_DATA: RecordFormData = {
  title: 'Ajouter un worker',
  description:
    'Examinez les demandes des workers avant de les accepter. Si un worker n’apparaît pas, générez une clé depuis sa page Workers (« Cluster puis Rejoindre un central ») et saisissez-la ici ; elle expire au bout de 10 minutes.',
  submitLabel: 'Accepter le worker',
  reviewTitle: 'Vérifier le jumelage',
  fields: [
    {
      id: 'pairingKey',
      label: 'Clé générée sur le worker',
      type: 'text',
      required: true,
      maxLength: 16,
    },
  ],
};

export const CREATE_LIBRARY_FORM_DATA: RecordFormData = {
  title: 'Déclarer une bibliothèque',
  description:
    'Créez une définition partagée côté serveur. Cette création ne donne pas encore accès aux médias à ce worker.',
  submitLabel: 'Déclarer la bibliothèque',
  fields: [{ id: 'name', label: 'Nom', type: 'text', required: true, maxLength: 128 }],
};

/** Regroupe les états de cycle de vie : en ligne, hors ligne, ou à traiter (vidange, incompatible, quarantaine). */
export function workerStateGroup(node: WorkerResponse): 'online' | 'offline' | 'attention' {
  const lifecycle = node.lifecycle ?? node.status;
  return lifecycle === 'online' ? 'online' : lifecycle === 'offline' ? 'offline' : 'attention';
}

/** Unité « tâche » ou « tâches » selon le nombre. */
export function taskUnit(count: number): string {
  return count > 1 ? 'tâches' : 'tâche';
}

/** Libellé et ton du cycle de vie d'un worker, partagés avec le reste de l'application. */
export function nodeLifecycleBadge(node: WorkerResponse): { label: string; tone: StatusTone } {
  const lifecycle = workerLifecycle(node);
  return { label: NODE_LIFECYCLE_LABELS[lifecycle], tone: NODE_LIFECYCLE_TONES[lifecycle] };
}

export function formatLastSeen(value: string): string {
  return formatRelativeTime(value);
}

export function formatExpiry(value: string): string {
  const seconds = Math.max(0, Math.ceil((new Date(value).getTime() - Date.now()) / 1000));
  if (seconds < 60) return `Dans ${seconds} s`;
  return `Dans ${Math.ceil(seconds / 60)} min`;
}

export function formatExpiryTime(value: string): string {
  return formatShortDateTime(value);
}

export function capabilitiesLabel(capabilities: readonly WorkerToolCapability[]): string {
  return capabilities.length
    ? capabilities.map((item) => `${item.toolId} ${item.version}`).join(' · ')
    : 'Aucun outil détecté';
}

export function nodeOverviewHardware(inventory: WorkerInventory | null): WorkerOverviewHardware {
  const operatingSystem = inventory?.operatingSystem?.trim() ?? '';
  const runtime = inventory?.runtimeEnvironment?.trim().toLowerCase() ?? '';
  const os = operatingSystem.toLowerCase();
  const platform: WorkerOverviewHardware['platform'] =
    runtime === 'docker'
      ? 'docker'
      : os.includes('windows')
        ? 'windows'
        : os.includes('linux')
          ? 'linux'
          : 'generic';
  const platformLabel =
    platform === 'docker' ? 'Docker · conteneur' : operatingSystem || 'Plateforme non déclarée';
  const cleanGpuName = (raw: string): string => {
    let g = raw
      .replace(/Advanced Micro Devices, Inc\. \[AMD\/ATI\]/gi, 'AMD')
      .replace(/Advanced Micro Devices, Inc\./gi, 'AMD')
      .replace(/Intel Corporation\s*/gi, 'Intel ')
      .replace(/NVIDIA Corporation\s*/gi, 'NVIDIA ')
      .replace(/\([Rr]\)|\([Tt][Mm]\)/g, '')
      .replace(/\s+/g, ' ')
      .trim();
    const match = /\[(.*?)\]/.exec(g);
    if (match && match[1].length > 3) {
      const brand = g.startsWith('AMD')
        ? 'AMD '
        : g.startsWith('Intel')
          ? 'Intel '
          : g.startsWith('NVIDIA')
            ? 'NVIDIA '
            : '';
      if (!match[1].startsWith(brand.trim())) {
        return `${brand}${match[1]}`.trim();
      }
      return match[1].trim();
    }
    return g;
  };

  const gpu = inventory?.gpus?.length
    ? inventory.gpus.map((item) => cleanGpuName(item.name)).join(' · ')
    : inventory?.gpus
      ? 'Aucun détecté'
      : 'Non déclaré';

  const rawCpu = inventory?.processorModel?.trim() || '';
  const cores = inventory?.processorCount;
  let cpu = 'Non déclaré';
  if (rawCpu) {
    cpu = rawCpu
      .replace(/\([Rr]\)|\([Tt][Mm]\)/g, '')
      .replace(/@\s*[\d.]+\s*GHz/gi, '')
      .replace(/\s+CPU\b/g, '')
      .replace(/\s+/g, ' ')
      .trim();
    if (cores && !cpu.toLowerCase().includes('cœur') && !cpu.toLowerCase().includes('core')) {
      cpu += ` (${cores} cœurs)`;
    }
  } else if (cores) {
    cpu = `${cores} cœurs`;
  }

  return {
    cpu,
    memory: inventory ? formatMemory(inventory.memoryMb) : 'Non déclarée',
    gpu,
    platform,
    platformLabel,
  };
}

export function memberTaskCapacity(
  node: WorkerResponse,
  activityKnown: boolean,
  activeTasks: Readonly<Record<string, number>>,
): WorkerOverviewTaskCapacity {
  const workerEnabled = node.roles.includes('worker');
  const accepted = activityKnown ? (activeTasks[node.id] ?? 0) : null;
  const maximum = workerEnabled ? node.maxConcurrentTasks : null;
  const remaining =
    workerEnabled && accepted !== null && maximum !== null ? Math.max(0, maximum - accepted) : null;
  let note = '';
  if (!workerEnabled) note = 'Fonction Worker désactivée.';
  else if (accepted === null) note = 'Comptage des tâches indisponible.';
  else if (workerLifecycle(node) === 'draining')
    note = 'Admission suspendue : les places libres ne sont pas attribuées.';
  else if (workerLifecycle(node) !== 'online')
    note = 'Worker indisponible pour recevoir des tâches.';

  return { accepted, remaining, maximum, note };
}

export function nodeOverviewLibraries(
  allLibraries: readonly LibraryResponse[],
  nodeAccessByLibrary: ReadonlyMap<string, readonly LibraryWorkerAccessResponse[]>,
  nodeId: string | null,
): readonly WorkerOverviewLibrary[] {
  if (!nodeId) return [];
  return allLibraries.flatMap((library) => {
    const access = nodeAccessByLibrary.get(library.id)?.find((item) => item.workerId === nodeId);
    if (!access) return [];

    const stateLabel = !library.isActive
      ? 'Désactivée globalement'
      : access.mode === 'unsupported'
        ? 'Non pris en charge'
        : access.isEnabled
          ? 'Active'
          : 'Désactivée sur ce worker';
    const stateTone: StatusTone = !library.isActive
      ? 'neutral'
      : access.mode === 'unsupported' || !access.isEnabled
        ? 'warning'
        : 'success';
    const modeLabel =
      access.mode === 'direct'
        ? 'Accès direct'
        : access.mode === 'transfer'
          ? 'Transfert WebDAV'
          : 'Aucun accès configuré';
    const accessLabel =
      [access.canRead ? 'Lecture' : null, access.canWrite ? 'Écriture' : null]
        .filter((item): item is string => item !== null)
        .join(' · ') || 'Aucun droit déclaré';

    return [
      {
        id: library.id,
        name: library.name,
        path:
          access.localPath ??
          (access.mode === 'transfer'
            ? 'Espace temporaire du worker'
            : access.mode === 'unsupported'
              ? 'Aucun accès actif'
              : 'Chemin à configurer'),
        stateLabel,
        stateTone,
        modeLabel,
        accessLabel,
      },
    ];
  });
}

export function libraryAccessSummary(
  loadState: 'loading' | 'ready' | 'error',
  allLibraries: readonly LibraryResponse[],
  nodeAccessByLibrary: ReadonlyMap<string, readonly LibraryWorkerAccessResponse[]>,
  nodeId: string,
): string {
  if (loadState === 'loading') return 'Chargement…';
  if (loadState === 'error') return 'Indisponible';
  const accesses = allLibraries.flatMap((library) => {
    const access = nodeAccessByLibrary.get(library.id)?.find((item) => item.workerId === nodeId);
    return access && access.isEnabled && access.mode !== 'unsupported' ? [{ library, access }] : [];
  });
  if (!accesses.length) return 'À configurer';
  const activeCount = accesses.filter(({ library }) => library.isActive).length;
  const inactiveCount = accesses.length - activeCount;
  return inactiveCount
    ? `${activeCount} ${activeCount > 1 ? 'actives' : 'active'} · ${inactiveCount} ${inactiveCount > 1 ? 'désactivées' : 'désactivée'}`
    : `${activeCount} ${activeCount > 1 ? 'bibliothèques actives' : 'bibliothèque active'}`;
}

export interface WorkerRecordContext {
  readonly activityKnown: boolean;
  readonly activeTasks: Readonly<Record<string, number>>;
  readonly libraryAccessLoadState: 'loading' | 'ready' | 'error';
  readonly allLibraries: readonly LibraryResponse[];
  readonly nodeAccessByLibrary: ReadonlyMap<string, readonly LibraryWorkerAccessResponse[]>;
}

export function toWorkerRecord(node: WorkerResponse, ctx: WorkerRecordContext): WorkerRecord {
  const roleLabel = node.roles.map((role) => NODE_ROLE_LABELS[role]).join(' · ');
  const worker = node.roles.includes('worker');
  return {
    id: node.id,
    name: formatWorkerLabel(node.name, node.displayName),
    roleFilter: `|${node.roles.join('|')}|`,
    roleLabel,
    libraryAccess: libraryAccessSummary(
      ctx.libraryAccessLoadState,
      ctx.allLibraries,
      ctx.nodeAccessByLibrary,
      node.id,
    ),
    workerLoad: worker
      ? ctx.activityKnown
        ? String(ctx.activeTasks[node.id] ?? 0) +
          ' / ' +
          node.maxConcurrentTasks +
          ' ' +
          taskUnit(node.maxConcurrentTasks)
        : String(node.maxConcurrentTasks) +
          ' ' +
          taskUnit(node.maxConcurrentTasks) +
          ' max. · charge indisponible'
      : 'Non applicable',
    queueStatus: 'Non disponible',
    state: NODE_LIFECYCLE_LABELS[workerLifecycle(node)],
    lastSignal: formatLastSeen(node.lastSeenAt),
  };
}

export interface EnrollmentStepContext {
  readonly clusterAddress: string | null;
  readonly clusterName: string;
}

export function buildEnrollmentSteps(
  request: PendingWorkerEnrollmentResponse,
  ctx: EnrollmentStepContext,
): readonly GuidedStep[] {
  const cardTitle = formatWorkerLabel(request.name, request.displayName);
  const card: DetailCard = {
    id: `enrollment-${request.id}`,
    title: cardTitle,
    subtitle: 'Worker demandeur',
    icon: 'server',
    status: { label: 'En attente', tone: 'warning' },
    tags: request.roles.map((role) => ({ label: NODE_ROLE_LABELS[role], tone: 'accent' })),
    sections: [
      {
        title: 'Configuration annoncée',
        items: [
          ...(request.displayName && request.name && request.displayName !== request.name
            ? [{ label: 'Nom d’hôte', description: request.name }]
            : []),
          {
            label: 'Capacités',
            description: capabilitiesLabel(request.capabilities),
          },
          {
            label: 'Exécution simultanée',
            description: `${request.maxConcurrentTasks} ${taskUnit(request.maxConcurrentTasks)}`,
          },
          {
            label: 'Demande valable jusqu’au',
            description: formatExpiryTime(request.expiresAt),
          },
        ],
      },
    ],
  };
  const codeCard: DetailCard = {
    id: `enrollment-code-${request.id}`,
    title: 'Central d’accueil',
    subtitle: 'Adresse de référence annoncée par ce central',
    icon: 'server',
    tags: [],
    sections: [
      {
        title: 'Vérification à deux écrans',
        items: [
          {
            label: 'Adresse du central',
            description: ctx.clusterAddress ?? 'Central actuel',
          },
        ],
      },
    ],
  };
  return [
    {
      id: 'request',
      label: 'Demande',
      description: 'Vérifiez le worker et les fonctions qu’il annonce au central.',
      cards: [card],
      actions: [
        { id: 'cancel', label: 'Annuler' },
        { id: 'continue', label: 'Continuer', primary: true },
      ],
    },
    {
      id: 'verification',
      label: 'Vérification',
      description: 'Le code doit être identique sur les deux écrans.',
      heroCode: request.verificationCode
        ? {
            code: request.verificationCode,
            label: 'Code de vérification',
            helper: 'Comparez avec le code affiché sur le worker demandeur.',
            badge: 'Vérification requise',
            tone: 'warning',
          }
        : undefined,
      cards: [codeCard],
      note: request.verificationCode
        ? 'Confirmez uniquement si le code correspond exactement sur les deux workers.'
        : 'Aucun code de vérification n’est disponible. Cette demande ne peut pas être approuvée ; actualisez les demandes et réessayez.',
      acknowledgement: {
        label: 'J’ai comparé les deux écrans et les codes sont identiques.',
      },
      actions: [
        { id: 'back', label: 'Précédent' },
        {
          id: 'continue',
          label: 'Continuer',
          primary: true,
          disabled: !request.verificationCode,
          requiresAcknowledgement: true,
        },
      ],
    },
    {
      id: 'approval',
      label: 'Approbation',
      description: `Le worker rejoindra le central ${ctx.clusterName} avec les rôles et capacités annoncés.`,
      cards: [card],
      note: 'Après approbation, le worker apparaît parmi les membres dès qu’il confirme sa connexion.',
      actions: [
        { id: 'back', label: 'Précédent' },
        { id: 'approve', label: 'Approuver le rattachement', primary: true },
      ],
    },
  ];
}

export function processingLimitForm(
  node: WorkerResponse,
  settings: WorkerProcessingSettings,
): RecordFormData {
  return {
    title: 'Limite globale du worker',
    description:
      'Ce plafond concerne toutes ses bibliothèques. Les limites de bibliothèque et les limites par worker/bibliothèque s’appliquent aussi.',
    submitLabel: 'Enregistrer',
    review: false,
    cancellable: false,
    fields: [
      {
        id: 'maximum',
        label: 'Fichiers traités simultanément, toutes bibliothèques',
        type: 'number',
        min: 1,
        max: 1000,
        step: 1,
        hint:
          'Vide : utiliser la limite locale annoncée par le worker (' +
          node.maxConcurrentTasks +
          ').',
      },
    ],
    initialValues: {
      maximum: settings.maxConcurrentTasks == null ? '' : String(settings.maxConcurrentTasks),
    },
  };
}
