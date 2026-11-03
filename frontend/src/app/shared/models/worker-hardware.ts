import { joinBadges } from './media-badges';
import { WorkerResponse } from './central';
import type {
  ResourceCardChip,
  ResourceCardSection,
  ResourceCardTone,
} from '../ui/resource-card.component';

export function formatMemory(memoryMb: number): string {
  return memoryMb >= 1024 ? `${Math.round((memoryMb / 1024) * 10) / 10} Go` : `${memoryMb} Mo`;
}

/** Ordre de préférence de l'affichage : celui des priorités par défaut des profils d'encodage. */
const ACCELERATION_ORDER = ['nvenc', 'qsv', 'vaapi'] as const;
const ACCELERATION_LABELS: Readonly<Record<string, string>> = {
  nvenc: 'NVENC',
  qsv: 'Quick Sync',
  vaapi: 'VAAPI',
  cpu: 'CPU',
};

function accelerationOf(encoderId: string): string | null {
  const suffix = encoderId.split('_').pop() ?? '';
  return ACCELERATION_ORDER.find((item) => item === suffix) ?? null;
}

/**
 * Accélérateurs d'un worker : le premier encodeur vérifié par mire, puis les autres vérifiés.
 * Repli sur CPU quand aucun encodeur matériel n'est vérifié.
 * Les encodeurs seulement présents dans la compilation FFmpeg (mire échouée) ne sont
 * jamais présentés comme des accélérateurs utilisables.
 */
export function workerAcceleratorBadges(
  node: Pick<WorkerResponse, 'roles' | 'capabilities'>,
): string[] {
  if (!node.roles.includes('worker')) return ['Non applicable'];
  if (!node.capabilities.length) return ['Non déclaré'];
  const usable = new Set<string>();
  for (const tool of node.capabilities) {
    for (const check of tool.encoderChecks ?? []) {
      const acceleration = accelerationOf(check.encoderId);
      if (acceleration && check.status === 'usable') usable.add(acceleration);
    }
    for (const acceleration of tool.accelerationTypes) {
      if (ACCELERATION_LABELS[acceleration] && acceleration !== 'cpu') usable.add(acceleration);
    }
  }
  const verified = ACCELERATION_ORDER.filter((item) => usable.has(item));
  return verified.length ? verified.map((item) => ACCELERATION_LABELS[item]) : ['CPU'];
}

/**
 * Familles d'accélérateurs présentes dans FFmpeg mais dont la mire a échoué sur ce matériel.
 */
export function workerUnvalidatedAccelerators(
  node: Pick<WorkerResponse, 'roles' | 'capabilities'>,
): string[] {
  if (!node.roles.includes('worker') || !node.capabilities.length) return [];
  const usable = new Set<string>();
  const detected = new Set<string>();
  for (const tool of node.capabilities) {
    for (const check of tool.encoderChecks ?? []) {
      const acceleration = accelerationOf(check.encoderId);
      if (acceleration) (check.status === 'usable' ? usable : detected).add(acceleration);
    }
    for (const acceleration of tool.accelerationTypes) {
      if (ACCELERATION_LABELS[acceleration] && acceleration !== 'cpu') usable.add(acceleration);
    }
  }
  const pending = ACCELERATION_ORDER.filter((item) => !usable.has(item) && detected.has(item));
  return pending.map((item) => ACCELERATION_LABELS[item]);
}

/**
 * Libellés explicites pour les accélérateurs non validés.
 * Exemple : « NVENC (encodeur FFmpeg présent, non validé) ».
 */
export function workerUnvalidatedAcceleratorBadges(
  node: Pick<WorkerResponse, 'roles' | 'capabilities'>,
): string[] {
  return workerUnvalidatedAccelerators(node).map(
    (item) => `${item} (encodeur FFmpeg présent, non validé)`,
  );
}

export type OperatingSystemFamily = 'linux' | 'windows' | 'macos' | 'unknown';

export function detectOsFamily(osString?: string | null): OperatingSystemFamily {
  if (!osString) return 'unknown';
  const lower = osString.toLowerCase();
  if (lower.includes('linux') || lower.includes('ubuntu') || lower.includes('debian') || lower.includes('fedora') || lower.includes('centos') || lower.includes('alpine') || lower.includes('arch') || lower.includes('redhat') || lower.includes('suse')) {
    return 'linux';
  }
  if (lower.includes('windows') || lower.includes('win32') || lower.includes('win64')) {
    return 'windows';
  }
  if (lower.includes('mac') || lower.includes('darwin') || lower.includes('osx') || lower.includes('apple')) {
    return 'macos';
  }
  return 'unknown';
}

export function workerOsFamily(node: WorkerResponse): OperatingSystemFamily {
  return detectOsFamily(node.inventory?.operatingSystem);
}

/** Matériel déclaré : système, processeur, mémoire installée, puis processeur graphique. */
export function workerHardwareLabel(node: WorkerResponse): string {
  const inventory = node.inventory;
  if (!inventory) return 'Non renseigné';
  const gpus = (inventory.gpus ?? []).map((gpu) => gpu.name);
  return [
    `${inventory.operatingSystem} ${inventory.architecture}`,
    `${inventory.processorModel?.trim() || 'Processeur'} · ${inventory.processorCount} cœurs`,
    `${formatMemory(inventory.memoryMb)} de RAM`,
    ...(gpus.length ? [`GPU : ${gpus.join(', ')}`] : []),
  ].join(' · ');
}

export const workerAcceleratorValue = (node: WorkerResponse): string =>
  joinBadges(workerAcceleratorBadges(node));

export const ACCELERATION_TONES: Readonly<Record<string, ResourceCardTone>> = {
  nvenc: 'lime',
  qsv: 'blue',
  vaapi: 'purple',
};

export function workerAcceleratorChips(node: WorkerResponse): ResourceCardChip[] {
  if (!node.roles.includes('worker') || !node.capabilities.length) return [];
  const toneOf = (item: string): ResourceCardTone =>
    ACCELERATION_TONES[
      Object.keys(ACCELERATION_LABELS).find((key) => ACCELERATION_LABELS[key] === item) ?? ''
    ] ?? 'neutral';
  return workerAcceleratorBadges(node)
    .filter((item) => item !== 'CPU')
    .map((item) => ({ label: item, tone: toneOf(item) }));
}

/** « Intel(R) Xeon(R) CPU E5620 @ 2.40GHz » devient « Intel Xeon E5620 @ 2.40GHz ». */
export function tidyProcessorName(name: string): string {
  return name
    .replace(/\((R|TM)\)/gi, '')
    .replace(/\bCPU\b\s*/g, '')
    .replace(/\s+/g, ' ')
    .trim();
}

function acceleratorSection(node: WorkerResponse): ResourceCardSection {
  const label = 'Accélérateur graphique';
  if (!node.roles.includes('worker'))
    return { label, hint: 'Non applicable : la fonction worker n’est pas activée.' };
  if (!node.capabilities.length)
    return { label, hint: 'Non déclaré : cet agent n’a pas encore remonté ses capacités.' };
  const [primary, ...others] = workerAcceleratorBadges(node);
  const toneOf = (item: string): ResourceCardTone =>
    ACCELERATION_TONES[
      Object.keys(ACCELERATION_LABELS).find((key) => ACCELERATION_LABELS[key] === item) ?? ''
    ] ?? 'neutral';
  const chip = (item: string): ResourceCardChip => ({ label: item, tone: toneOf(item) });
  return {
    label,
    primary: chip(primary),
    chips: others.map(chip),
    hint:
      primary === ACCELERATION_LABELS['cpu']
        ? 'Aucun accélérateur matériel vérifié : encodage sur CPU.'
        : undefined,
  };
}

function hardwareSection(node: WorkerResponse): ResourceCardSection {
  const inventory = node.inventory;
  if (!inventory)
    return {
      label: 'Matériel déclaré',
      hint: 'Non renseigné : disponible après le prochain signal du worker.',
    };
  const gpus = inventory.gpus;
  return {
    label: 'Matériel déclaré',
    facts: [
      { label: 'OS', value: `${inventory.operatingSystem} · ${inventory.architecture}` },
      {
        label: 'CPU',
        value: `${tidyProcessorName(inventory.processorModel?.trim() || 'Processeur')} · ${inventory.processorCount} cœurs`,
      },
      { label: 'RAM', value: formatMemory(inventory.memoryMb) },
      {
        label: 'GPU',
        value: gpus?.length
          ? gpus.map((gpu) => gpu.name).join(', ')
          : gpus
            ? 'Aucun détecté'
            : 'Non déclaré',
      },
    ],
  };
}

export interface WorkerProcessingInput {
  /** Tâches actives observées ; `null` tant que la source n'a pas répondu. */
  readonly activeTasks: number | null;
  readonly libraryAccess: string;
}

function processingSection(node: WorkerResponse, input: WorkerProcessingInput): ResourceCardSection {
  const label = 'Traitements';
  const hint = `Bibliothèques : ${input.libraryAccess}`;
  if (!node.roles.includes('worker'))
    return { label, hint: 'Non applicable : la fonction worker n’est pas activée.' };
  const max = node.maxConcurrentTasks;
  if (input.activeTasks === null || node.status === 'offline')
    return {
      label,
      facts: [{ label: 'Max.', value: `${max} tâche(s)` }],
      hint: `${node.status === 'offline' ? 'Hors ligne : charge inconnue.' : 'Charge indisponible.'} ${hint}`,
    };
  return {
    label,
    meter: {
      value: input.activeTasks,
      max,
      label: `${input.activeTasks} places de traitement occupées sur ${max}`,
      valueLabel: `${input.activeTasks} / ${max} tâche(s)`,
    },
    hint,
  };
}

/** Blocs de la carte d'un membre du central : accélérateur (optionnel), matériel, traitements. */
export function workerMemberSections(
  node: WorkerResponse,
  input: WorkerProcessingInput,
  options?: { readonly includeAccelerator?: boolean },
): ResourceCardSection[] {
  const includeAcc = options?.includeAccelerator ?? true;
  return includeAcc
    ? [acceleratorSection(node), hardwareSection(node), processingSection(node, input)]
    : [hardwareSection(node), processingSection(node, input)];
}
