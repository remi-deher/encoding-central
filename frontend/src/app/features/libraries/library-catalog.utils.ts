import { LibraryResponse } from '../../shared/models/central';
import type { DataTableRow } from '../../shared/ui/data-table.component';
import type {
  ResourceListGroup,
  ResourceListItem,
  ResourceListTag,
} from '../../shared/ui/resource-list.component';
import type { LibraryWorkerAccessMode } from '../../shared/ui/library-worker-access-types';
import type { LibraryWorkerAccessResponse } from '../../shared/ui/library-worker-access-types';
import type { RecordFormData } from '../../shared/ui/record-form.component';

export interface LibraryExportFile {
  readonly format: number;
  readonly libraries: readonly Pick<LibraryResponse, 'name' | 'isActive'>[];
}

export interface LibraryRecord extends DataTableRow {
  readonly id: string;
  readonly name: string;
  readonly configuredNodes: string;
  readonly configuredWorkerCount: number;
  readonly directAccessWorkerCount: number;
  readonly transferWorkerCount: number;
  readonly isActive: boolean;
  readonly accessModes: string;
  readonly nodePaths: string;
  readonly lastScan: string;
  readonly associatedFlows: string;
  readonly assignedFlow: string;
  readonly monitoring: string;
  readonly state: string;
  readonly modifiedAt: string;
  readonly modifiedOrder: number;
}

export interface LibraryAccessRecord extends DataTableRow {
  readonly workerName: string;
  readonly isEnabled: boolean;
  readonly modeLabel: string;
  readonly location: string;
  readonly permissions: string;
  readonly mode: LibraryWorkerAccessMode;
}

export type LibraryActionType = 'create' | 'edit' | 'access' | 'flow' | 'processing';

export const LIBRARY_GROUPS: readonly ResourceListGroup[] = [
  { id: 'configuration', label: 'À configurer', tone: 'warning' },
  { id: 'ready', label: 'Prêtes', tone: 'default' },
  { id: 'disabled', label: 'Désactivées', tone: 'blue' },
];

export const CREATE_LIBRARY_FORM_DATA: RecordFormData = {
  title: 'Ajouter une bibliothèque',
  description:
    'Créez sa définition partagée. Les chemins et modes d’accès seront configurés séparément pour chaque worker.',
  submitLabel: 'Créer la bibliothèque',
  fields: [{ id: 'name', label: 'Nom', type: 'text', required: true, maxLength: 128 }],
};

export function libraryState(isActive: boolean, configuredNodeCount: number): string {
  return !isActive ? 'Désactivée' : configuredNodeCount ? 'Active' : 'Accès à configurer';
}

export function accessModeLabel(mode: LibraryWorkerAccessMode): string {
  if (mode === 'direct') return 'Accès direct';
  if (mode === 'transfer') return 'Transfert worker';
  return 'Aucun accès';
}

export type LibraryAccessSummary = Pick<
  LibraryRecord,
  | 'configuredNodes'
  | 'accessModes'
  | 'nodePaths'
  | 'state'
  | 'configuredWorkerCount'
  | 'directAccessWorkerCount'
  | 'transferWorkerCount'
>;

export function toAccessSummaryFromCounts(library: LibraryResponse): LibraryAccessSummary {
  const directCount = library.directAccessWorkerCount;
  const transferCount = library.transferWorkerCount;
  const configuredCount = library.configuredWorkerCount;
  const modes = [
    directCount ? `${directCount} direct(s)` : '',
    transferCount ? `${transferCount} transfert(s)` : '',
  ]
    .filter(Boolean)
    .join(' · ');

  return {
    configuredWorkerCount: configuredCount,
    directAccessWorkerCount: directCount,
    transferWorkerCount: transferCount,
    configuredNodes: configuredCount ? `${configuredCount} worker(s) utilisable(s)` : 'Aucun accès',
    accessModes: modes || 'Non configuré',
    nodePaths: directCount ? 'Consulter les chemins configurés' : 'Aucun chemin direct',
    state: libraryState(library.isActive, configuredCount),
  };
}

export function toAccessSummary(
  accesses: readonly LibraryWorkerAccessResponse[],
  selectedState: string | undefined,
): LibraryAccessSummary {
  const directCount = accesses.filter(
    (access) => access.isEnabled && access.mode === 'direct',
  ).length;
  const transferCount = accesses.filter(
    (access) => access.isEnabled && access.mode === 'transfer',
  ).length;
  const configuredCount = directCount + transferCount;
  const modes = [
    directCount ? `${directCount} direct(s)` : '',
    transferCount ? `${transferCount} transfert(s)` : '',
  ]
    .filter(Boolean)
    .join(' · ');

  return {
    configuredWorkerCount: configuredCount,
    directAccessWorkerCount: directCount,
    transferWorkerCount: transferCount,
    configuredNodes: configuredCount ? `${configuredCount} worker(s) utilisable(s)` : 'Aucun accès',
    accessModes: modes || 'Non configuré',
    nodePaths:
      accesses
        .filter((access) => access.isEnabled && access.mode === 'direct')
        .map((access) => `${access.workerName}: ${access.localPath}`)
        .join(' · ') || 'Aucun chemin direct',
    state:
      selectedState === 'Désactivée'
        ? 'Désactivée'
        : configuredCount
          ? 'Active'
          : 'Accès à configurer',
  };
}

/** Ligne de la liste des bibliothèques (groupe À configurer / Prêtes / Désactivées). */
export function toLibraryListItem(
  library: LibraryRecord,
  activationBusy: ReadonlySet<string>,
): ResourceListItem {
  const hasFlow = library.assignedFlow !== 'Aucun flow';
  const tags: ResourceListTag[] = [
    hasFlow
      ? { label: `Flow : ${library.assignedFlow}`, tone: 'ok' }
      : { label: 'Flow à attribuer', tone: 'warn' },
    library.configuredWorkerCount
      ? { label: library.accessModes }
      : { label: 'Aucun worker n’a accès', tone: 'warn' },
    { label: `Surveillance ${library.monitoring.toLocaleLowerCase('fr')}` },
  ];
  if (!library.isActive) tags.unshift({ label: 'Désactivée', tone: 'warn' });

  return {
    id: library.id,
    name: library.name,
    subtitle: `Créée le ${library.modifiedAt}`,
    group: !library.isActive
      ? LIBRARY_GROUPS[2]
      : hasFlow && library.configuredWorkerCount > 0
        ? LIBRARY_GROUPS[1]
        : LIBRARY_GROUPS[0],
    rowData: library,
    tags,
    toggle: {
      checked: library.isActive,
      disabled: activationBusy.has(library.id),
      ariaLabel: `Activer la bibliothèque ${library.name}`,
      confirmOff: {
        title: 'Désactiver cette bibliothèque ?',
        message: `« ${library.name} » ne sera plus disponible pour le central. Ses accès configurés seront conservés.`,
        confirmLabel: 'Désactiver',
      },
    },
  };
}

/** Ligne du tableau des accès par worker d’une bibliothèque. */
export function toAccessRow(access: LibraryWorkerAccessResponse): LibraryAccessRecord {
  return {
    id: access.workerId,
    workerName: access.workerName,
    isEnabled: access.isEnabled,
    modeLabel: `${accessModeLabel(access.mode)}${access.mode !== 'unsupported' && !access.isEnabled ? ' · Désactivé' : ''}`,
    location:
      access.mode === 'direct'
        ? (access.localPath ?? 'Chemin absent')
        : access.mode === 'transfer'
          ? `Depuis ${access.transferSourceWorkerName ?? 'source inconnue'}`
          : 'Aucun accès',
    permissions:
      access.mode === 'direct'
        ? [access.canRead ? 'Lecture' : '', access.canWrite ? 'Écriture' : '']
            .filter(Boolean)
            .join(' · ') || 'Aucun droit'
        : access.mode === 'transfer'
          ? 'Copie temporaire · lecture et écriture'
          : 'Aucun',
    mode: access.mode,
  };
}
