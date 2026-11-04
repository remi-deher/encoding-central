import { LibraryProcessingSettings } from '../../shared/models/library-workflow';
import type {
  ResourceSheetNotice,
  ResourceSheetSection,
} from '../../shared/ui/resource-sheet.component';
import type { LibraryRecord } from './library-catalog.utils';

/** Fiche de synthèse de la bibliothèque sélectionnée (fonction pure). */
export function buildLibrarySheet(input: {
  readonly library: LibraryRecord | null;
  readonly assignment: { readonly name: string; readonly version: number } | null | undefined;
  readonly processing: LibraryProcessingSettings | null;
  readonly scan: { readonly state?: string; readonly completedAt?: string | null } | null | undefined;
  readonly workflowError: string;
  readonly workflowLabel: string;
  readonly processingSummary: string;
  readonly discoveryStatus: string;
  readonly mediaCount: number;
}): {
  readonly title: string;
  readonly subtitle: string;
  readonly notices: readonly ResourceSheetNotice[];
  readonly sections: readonly ResourceSheetSection[];
} | null {
  const { library } = input;
  if (!library) return null;
  {
    const assignment = input.assignment;
    const processing = input.processing;
    const scan = input.scan;
    const accessCount = library.configuredWorkerCount;
    const scanState = scan?.state;
    const scanLabel = scanState
      ? scanState === 'queued'
        ? 'En attente'
        : scanState === 'running'
          ? 'En cours'
          : scanState === 'completed'
            ? 'Terminée'
            : 'Échec'
      : input.workflowError
        ? 'Indisponible'
        : 'Aucune découverte';

    const notices: ResourceSheetNotice[] = [];
    if (input.workflowError) {
      notices.push({
        tone: 'warning',
        title: 'Découverte indisponible',
        description: input.workflowError,
      });
    }

    const sections: readonly ResourceSheetSection[] = [
      {
        id: 'library-processing',
        title: 'Flow et traitement',
        caption: assignment ? `${assignment.name} · v${assignment.version}` : 'Aucun flow attribué',
        items: [
          {
            label: 'Flow attribué',
            detail: input.workflowLabel,
            badges: [
              {
                label: assignment ? 'Flow attribué' : 'À configurer',
                tone: assignment ? 'success' : 'warning',
              },
            ],
          },
          {
            label: 'Surveillance et limites',
            detail: input.processingSummary,
            badges: processing
              ? [
                  {
                    label: processing.watchEnabled ? 'Automatique' : 'Manuelle',
                    tone: processing.watchEnabled ? 'accent' : 'neutral',
                  },
                  {
                    label: processing.maxConcurrentTasks
                      ? `${processing.maxConcurrentTasks} tâche(s) max.`
                      : 'Sans plafond global',
                    tone: 'neutral',
                  },
                ]
              : [],
          },
        ],
      },
      {
        id: 'library-access',
        title: 'Accès du central',
        caption: accessCount ? `${accessCount} worker(s) configuré(s)` : 'Aucun worker configuré',
        metrics: [
          {
            label: 'Accès direct',
            value: `${library.directAccessWorkerCount} worker(s)`,
          },
          {
            label: 'Transfert vers worker',
            value: `${library.transferWorkerCount} worker(s)`,
          },
        ],
      },
      {
        id: 'library-discovery',
        title: 'Découverte et activité',
        caption: `${input.mediaCount} fichier(s) détecté(s)`,
        metrics: [
          {
            label: 'Dernière découverte',
            value: scanLabel,
            detail: input.discoveryStatus,
          },
          {
            label: 'Fichiers détectés',
            value: `${input.mediaCount}`,
          },
        ],
        fields: [
          {
            label: 'Terminée le',
            value: scan?.completedAt
              ? new Date(scan.completedAt).toLocaleString('fr-FR')
              : 'Aucune date enregistrée',
          },
          { label: 'Créée le', value: library.modifiedAt },
        ],
      },
    ];

    return {
      title: library.name,
      subtitle: `${library.state} · ${library.configuredNodes}`,
      notices,
      sections,
    };
  }
}


