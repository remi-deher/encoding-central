import { HttpClient } from '@angular/common/http';
import { computed, inject, Injectable, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, EMPTY, exhaustMap, finalize, firstValueFrom, of, startWith, switchMap } from 'rxjs';
import { LiveUpdates } from '../../shared/services/live-updates';
import { NotificationService } from '../../shared/services/notification.service';
import {
  LibraryResponse,
  WorkerActivityResponse,
} from '../../shared/models/central';
import {
  DataTableActionEvent,
  DataTableColumn,
  DataTableRow,
  DataTableRowAction,
  DataTableToggleEvent,
} from '../../shared/ui/data-table.component';
import type { RecordFormData } from '../../shared/ui/record-form.component';
import {
  LibraryWorkerAccessOption,
  LibraryWorkerAccessValue,
} from '../../shared/ui/library-worker-access-form.component';
import { LibraryWorkerAccessResponse } from '../../shared/ui/library-worker-access-types';
import { workerCapabilityCards, workerCapabilityFields } from '../../shared/models/worker-capabilities';
import { WorkerDataStore } from './worker-data.store';
import { WorkerProcessingStore } from './worker-processing.store';
import {
  nodeOverviewLibraries,
  WorkerLibraryRecord,
  WorkerRecord,
} from './worker-catalog.utils';
import { buildWorkerGeneralSheet } from './worker-general-sheet';

/**
 * Fiche détaillée du worker sélectionné : activité, fiche générale, capacités
 * et accès aux bibliothèques. Les réglages de traitement vivent dans
 * {@link WorkerProcessingStore}.
 */
@Injectable()
export class WorkerDetailStore {
  private readonly http = inject(HttpClient);
  private readonly live = inject(LiveUpdates);
  private readonly notifications = inject(NotificationService);
  readonly data = inject(WorkerDataStore);
  readonly processing = inject(WorkerProcessingStore);

  readonly storeState = toSignal(
    toObservable(computed(() => this.data.selectedNode()?.id ?? null)).pipe(
      switchMap((id) => id ? this.live.drive(['workers']).pipe(
        exhaustMap(() => this.http.get<WorkerStoreState>('/api/v1/workers/' + id + '/store-state').pipe(catchError(() => of(null)))),
        startWith(null),
      ) : of(null)),
    ), { initialValue: null },
  );

  readonly rawNode = computed(
    () => this.data.nodes().find((node) => node.id === this.data.selectedNode()?.id) ?? null,
  );

  readonly nodeActivity = toSignal(
    toObservable(computed(() => this.data.selectedNode()?.id ?? null)).pipe(
      switchMap((id) =>
        id
          ? this.live.drive(['executions', 'workers']).pipe(
              exhaustMap(() =>
                this.http
                  .get<WorkerActivityResponse>('/api/v1/workers/' + id + '/activity')
                  .pipe(catchError(() => of(null))),
              ),
              startWith(null),
            )
          : of(null),
      ),
    ),
    { initialValue: null },
  );

  readonly nodeLibraryColumns: readonly DataTableColumn[] = [
    { key: 'name', label: 'Bibliothèque', sortable: true },
    {
      key: 'isEnabled',
      label: 'Accès',
      toggle: {
        showStatusLabel: false,
        ariaLabel: (row, checked) =>
          `${checked ? 'Désactiver' : row['mode'] === 'unsupported' ? 'Configurer' : 'Activer'} l’accès à ${row['name']} sur ce worker`,
        disabledWhen: (row) =>
          this.nodeAccessActivationBusy().has(`${this.data.selectedNode()?.id}:${row['id']}`),
        confirmation: (row, checked) =>
          checked
            ? undefined
            : {
                title: 'Désactiver cette bibliothèque sur ce worker ?',
                message: `« ${row['name']} » restera déclarée et ses accès sur les autres workers ne changeront pas.`,
                confirmLabel: 'Désactiver sur ce worker',
              },
      },
    },
    { key: 'modeLabel', label: 'Mode autorisé', sortable: true },
    { key: 'pathLabel', label: 'Chemin sur ce worker' },
  ];

  readonly nodeLibraryActions: readonly DataTableRowAction[] = [
    { id: 'edit', label: 'Modifier' },
    {
      id: 'delete',
      label: 'Supprimer',
      tone: 'danger',
      confirmation: (row) => ({
        title: 'Retirer cette bibliothèque du worker ?',
        message: `L’accès de ce worker à « ${row['name']} » sera supprimé. La bibliothèque et ses accès sur les autres workers resteront inchangés.`,
        confirmLabel: 'Retirer du worker',
        variant: 'danger',
      }),
    },
  ];

  readonly nodeAccessActivationBusy = signal<ReadonlySet<string>>(new Set());
  readonly accessFormBusy = signal(false);
  readonly selectedNodeLibrary = signal<LibraryResponse | null>(null);
  readonly selectedAvailableLibraryId = signal('');

  readonly nodeLibraries = computed<readonly WorkerLibraryRecord[]>(() => {
    const node = this.data.selectedNode();
    if (!node) return [];
    return this.data.allLibraries().flatMap((library) => {
      const access = this.data
        .nodeAccessByLibrary()
        .get(library.id)
        ?.find((item) => item.workerId === node.id);
      if (!access) return [];
      const localMode =
        access.mode === 'direct'
          ? 'Accès direct'
          : access.mode === 'transfer'
            ? 'Transfert worker'
            : 'Aucun accès configuré';
      return [
        {
          id: library.id,
          name: library.name,
          isEnabled: access.isEnabled,
          mode: access.mode,
          modeLabel: `${localMode}${access.mode !== 'unsupported' && !access.isEnabled ? ' · Désactivée sur ce worker' : ''}${library.isActive ? '' : ' · Désactivée globalement'}`,
          pathLabel:
            access.localPath ??
            (access.mode === 'transfer'
              ? 'Espace temporaire du worker'
              : access.mode === 'unsupported'
                ? 'Aucun accès actif'
                : 'Chemin à configurer'),
        },
      ];
    });
  });

  readonly availableLibraries = computed(() => {
    const allowedIds = new Set(this.nodeLibraries().map((library) => library.id));
    return this.data
      .allLibraries()
      .filter((library) => library.isActive && !allowedIds.has(library.id));
  });

  readonly nodeLibraryRows = computed<readonly DataTableRow[]>(() => this.nodeLibraries());

  readonly selectedNodeLibraryAccess = computed(() => {
    const nodeId = this.data.selectedNode()?.id;
    const libraryId = this.selectedNodeLibrary()?.id;
    return nodeId && libraryId
      ? (this.data
          .nodeAccessByLibrary()
          .get(libraryId)
          ?.find((item) => item.workerId === nodeId) ?? null)
      : null;
  });

  readonly capabilityFields = computed(() =>
    workerCapabilityFields(this.rawNode()?.capabilities ?? []),
  );
  readonly capabilityCards = computed(() =>
    workerCapabilityCards(this.rawNode()?.capabilities ?? []),
  );

  readonly generalSheet = computed(() =>
    buildWorkerGeneralSheet(
      this.rawNode(),
      this.nodeActivity(),
      this.processing.nodeProcessingState()?.effectiveMaxConcurrentTasks ?? null,
      this.nodeLibraries(),
    ),
  );

  readonly identityForm = signal<RecordFormData | null>(null);

  async editNodeIdentity(node: WorkerRecord): Promise<void> {
    try {
      const identity = await firstValueFrom(
        this.http.get<{
          displayName: string | null;
          labels: Readonly<Record<string, string | null>>;
          effectiveLabels: Readonly<Record<string, string | null>>;
          groupIds: readonly string[];
        }>(`/api/v1/workers/${node.id}/identity`),
      );
      if (this.data.selectedNode()?.id !== node.id) this.data.selectedNodeTabIndex.set(0);
      this.data.selectedNode.set(node);
      this.identityForm.set({
        title: 'Nom et étiquettes',
        description: `Groupes actuels : ${identity.groupIds.length}. Étiquettes effectives : ${
          Object.entries(identity.effectiveLabels)
            .map(([key, value]) => (value == null ? key : `${key}=${value}`))
            .join(', ') || 'aucune'
        }.`,
        submitLabel: 'Enregistrer',
        fields: [
          { id: 'displayName', label: 'Nom d’affichage', type: 'text', maxLength: 128 },
          {
            id: 'labels',
            label: 'Étiquettes locales',
            type: 'textarea',
            hint: 'Une par ligne : gpu=nvidia ou production.',
          },
        ],
        initialValues: {
          displayName: identity.displayName ?? '',
          labels: Object.entries(identity.labels)
            .map(([key, value]) => (value == null ? key : `${key}=${value}`))
            .join('\n'),
        },
      });
      this.data.actionType.set('identity');
      this.data.actionOpen.set(true);
    } catch {
      this.notifications.show('Impossible de charger l’identité du worker.');
    }
  }

  async saveNodeIdentity(values: Record<string, string>): Promise<void> {
    const node = this.data.selectedNode();
    if (!node) return;
    const labels: Record<string, string | null> = {};
    for (const line of values['labels']
      .split(/[\n,]+/)
      .map((value) => value.trim())
      .filter(Boolean)) {
      const equals = line.indexOf('=');
      const key = (equals < 0 ? line : line.slice(0, equals)).trim();
      if (!/^[a-zA-Z][a-zA-Z0-9_.-]{0,63}$/.test(key)) {
        this.notifications.show('Clé d’étiquette invalide.');
        return;
      }
      labels[key] = equals < 0 ? null : line.slice(equals + 1).trim();
    }
    try {
      await firstValueFrom(
        this.http.put(`/api/v1/workers/${node.id}/identity`, {
          displayName: values['displayName'] || null,
          labels,
        }),
      );
      this.data.nodes.update((nodes) =>
        nodes.map((item) =>
          item.id === node.id
            ? { ...item, displayName: values['displayName'] || null, labels }
            : item,
        ),
      );
      this.data.actionOpen.set(false);
      this.notifications.show('Identité du worker enregistrée.');
    } catch {
      this.notifications.show('Les étiquettes ou le nom du worker sont invalides.');
    }
  }

  editSelectedNodeIdentity(): void {
    const node = this.data.selectedNode();
    if (node) void this.editNodeIdentity(node);
  }

  nodeAccessOption(node: WorkerRecord): LibraryWorkerAccessOption {
    const original = this.data.nodes().find((item) => item.id === node.id);
    return { id: node.id, name: node.name, roles: original?.roles ?? [] };
  }

  openAddLibrary(): void {
    this.data.actionType.set('create-library');
    this.data.actionOpen.set(true);
  }

  addExistingLibrary(): void {
    const library = this.data
      .allLibraries()
      .find((item) => item.id === this.selectedAvailableLibraryId());
    if (library) this.editNodeLibraryAccess(library);
  }

  editNodeLibraryAccess(library: WorkerLibraryRecord | LibraryResponse): void {
    this.selectedNodeLibrary.set(
      this.data.allLibraries().find((item) => item.id === library.id) ??
        (library as LibraryResponse),
    );
    this.data.actionType.set('library-access');
    this.data.actionOpen.set(true);
  }

  onNodeLibraryToggle(event: DataTableToggleEvent): void {
    const node = this.data.selectedNode();
    const library = this.data.allLibraries().find((item) => item.id === event.row['id']);
    if (!node || !library) return;
    if (event.checked && event.row['mode'] === 'unsupported') {
      this.editNodeLibraryAccess(library);
      return;
    }
    const busyKey = `${node.id}:${library.id}`;
    this.nodeAccessActivationBusy.update((current) => new Set(current).add(busyKey));
    this.http
      .put<LibraryWorkerAccessResponse>(
        `/api/v1/libraries/${library.id}/worker-access/${node.id}/activation`,
        { isEnabled: event.checked },
      )
      .pipe(
        catchError(() => {
          this.notifications.show('L’accès de ce worker n’a pas pu être modifié.');
          return EMPTY;
        }),
        finalize(() =>
          this.nodeAccessActivationBusy.update((current) => {
            const next = new Set(current);
            next.delete(busyKey);
            return next;
          }),
        ),
      )
      .subscribe((updated) => {
        this.data.nodeAccessByLibrary.update((current) =>
          new Map(current).set(
            library.id,
            (current.get(library.id) ?? []).map((item) =>
              item.workerId === node.id ? updated : item,
            ),
          ),
        );
        this.notifications.show(
          `Accès à « ${library.name} » ${event.checked ? 'réactivé' : 'désactivé'} pour ${node.name}.`,
        );
      });
  }

  onNodeLibraryAction(event: DataTableActionEvent): void {
    const node = this.data.selectedNode();
    const library = this.data.allLibraries().find((item) => item.id === event.row['id']);
    if (!node || !library) return;
    if (event.actionId === 'edit') {
      this.editNodeLibraryAccess(library);
      return;
    }

    const existing = this.data.nodeAccessByLibrary().get(library.id) ?? [];
    const access = existing.find((item) => item.workerId === node.id);
    if (!access) return;
    const configured = existing
      .filter((item) => event.actionId !== 'delete' || item.workerId !== node.id)
      .map((item): LibraryWorkerAccessValue => ({
        workerId: item.workerId,
        mode: item.mode,
        isEnabled: item.isEnabled,
        localPath: item.localPath,
        canRead: item.canRead,
        canWrite: item.canWrite,
        transferSourceWorkerId: item.transferSourceWorkerId,
      }));
    this.saveNodeLibraryConfiguration(library, node, configured, event.actionId);
  }

  createLibraryFromNode(value: Record<string, string>): void {
    this.http
      .post<LibraryResponse>('/api/v1/libraries', value)
      .pipe(
        catchError((error: { status?: number }) => {
          this.notifications.show(
            error.status === 409
              ? 'Une bibliothèque porte déjà ce nom.'
              : 'La bibliothèque n’a pas pu être déclarée sur le serveur.',
          );
          return EMPTY;
        }),
      )
      .subscribe((library) => {
        this.data.allLibraries.update((current) => [...current, library]);
        this.notifications.show(
          `La bibliothèque « ${library.name} » est déclarée sur le serveur. Autorisez ce worker pour configurer son accès.`,
        );
        this.editNodeLibraryAccess(library);
      });
  }

  saveNodeLibraryAccess(values: readonly LibraryWorkerAccessValue[]): void {
    const node = this.data.selectedNode();
    const library = this.selectedNodeLibrary();
    if (!node || !library || values.length !== 1) return;
    const existing = this.data.nodeAccessByLibrary().get(library.id) ?? [];
    const merged = [
      ...existing
        .filter((item) => item.workerId !== node.id)
        .map((item) => ({
          workerId: item.workerId,
          mode: item.mode,
          isEnabled: item.isEnabled,
          localPath: item.localPath,
          canRead: item.canRead,
          canWrite: item.canWrite,
          transferSourceWorkerId: item.transferSourceWorkerId,
        })),
      values[0],
    ];
    this.saveNodeLibraryConfiguration(library, node, merged, 'edit');
  }

  private saveNodeLibraryConfiguration(
    library: LibraryResponse,
    node: WorkerRecord,
    configured: readonly LibraryWorkerAccessValue[],
    action: string,
  ): void {
    this.accessFormBusy.set(true);
    this.http
      .put<readonly LibraryWorkerAccessResponse[]>(`/api/v1/libraries/${library.id}/worker-access`, {
        nodes: configured,
      })
      .pipe(
        catchError((error: { error?: { errors?: Record<string, string[]> } }) => {
          this.accessFormBusy.set(false);
          const validation = error.error?.errors
            ? Object.values(error.error.errors).flat()[0]
            : undefined;
          this.notifications.show(
            validation ?? 'La configuration de cette bibliothèque n’a pas pu être enregistrée.',
          );
          return EMPTY;
        }),
      )
      .subscribe((accesses) => {
        this.accessFormBusy.set(false);
        this.data.nodeAccessByLibrary.update((current) =>
          new Map(current).set(library.id, accesses),
        );
        this.data.actionOpen.set(false);
        this.notifications.show(
          action === 'delete'
            ? `Bibliothèque « ${library.name} » retirée du worker ${node.name}.`
            : `Accès à « ${library.name} » enregistré pour ${node.name}.`,
        );
      });
  }

  overviewLibraries(nodeId: string | null) {
    return nodeOverviewLibraries(this.data.allLibraries(), this.data.nodeAccessByLibrary(), nodeId);
  }
}

export interface WorkerStoreState {
  readonly desired: { readonly revision: number; readonly items: readonly StoreItem[] };
  readonly applied: { readonly revision: number; readonly reportedAt: string | null; readonly items: readonly StoreItem[] };
}
export interface StoreItem {
  readonly kind: string; readonly id: string; readonly version: string; readonly platform: string | null;
  readonly sha256: string; readonly status?: string; readonly errorCode?: string | null;
}
