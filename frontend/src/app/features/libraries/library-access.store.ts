import { HttpClient } from '@angular/common/http';
import { computed, inject, Injectable, signal } from '@angular/core';
import { catchError, EMPTY, finalize, forkJoin, of } from 'rxjs';
import { CentralApi } from '../../shared/services/central-api';
import { NotificationService } from '../../shared/services/notification.service';
import { WorkerGroupResponse } from '../../shared/models/central';
import {
  DataTableActionEvent,
  DataTableColumn,
  DataTableRowAction,
  DataTableToggleEvent,
} from '../../shared/ui/data-table.component';
import {
  LibraryWorkerAccessOption,
  LibraryWorkerAccessValue,
} from '../../shared/ui/library-worker-access-form.component';
import {
  LibraryWorkerAccessResponse,
} from '../../shared/ui/library-worker-access-types';
import { LibraryCatalogStore } from './library-catalog.store';
import { LibraryRecord, toAccessRow, toAccessSummary } from './library-catalog.utils';

/**
 * Accès par worker de la bibliothèque sélectionnée : tableau, activation,
 * formulaire de configuration et enregistrement.
 */
@Injectable()
export class LibraryAccessStore {
  private readonly http = inject(HttpClient);
  private readonly clusterApi = inject(CentralApi);
  private readonly notifications = inject(NotificationService);
  readonly catalog = inject(LibraryCatalogStore);

  readonly selectedAccesses = signal<readonly LibraryWorkerAccessResponse[]>([]);
  readonly selectedAccessesLoading = signal(false);
  readonly accessFormLoading = signal(false);
  private readonly accessActivationBusy = signal<ReadonlySet<string>>(new Set());
  readonly accessNodes = signal<readonly LibraryWorkerAccessOption[]>([]);
  readonly accessNodeGroups = signal<readonly WorkerGroupResponse[]>([]);
  readonly automaticallySelectedAccessNodeIds = signal<readonly string[]>([]);
  readonly initialAccesses = signal<readonly LibraryWorkerAccessResponse[]>([]);

  readonly accessStatus = computed(() => {
    const library = this.catalog.selectedLibrary();

    if (library?.state === 'Désactivée')
      return 'Bibliothèque désactivée par le serveur. Les chemins et autorisations restent enregistrés.';

    if (!library?.configuredWorkerCount)
      return 'Aucun accès actif. Les workers ne peuvent pas encore traiter ses médias.';

    const modes = [
      library.directAccessWorkerCount
        ? `${library.directAccessWorkerCount} accès direct${library.directAccessWorkerCount > 1 ? 's' : ''}`
        : '',
      library.transferWorkerCount
        ? `${library.transferWorkerCount} transfert${library.transferWorkerCount > 1 ? 's' : ''}`
        : '',
    ]
      .filter(Boolean)
      .join(' · ');

    return `${library.configuredWorkerCount} worker${library.configuredWorkerCount > 1 ? 's' : ''} utilisable${library.configuredWorkerCount > 1 ? 's' : ''} · ${modes}`;
  });

  readonly accessColumns: readonly DataTableColumn[] = [
    { key: 'workerName', label: 'Worker' },
    {
      key: 'isEnabled',
      label: 'Accès',
      toggle: {
        showStatusLabel: false,
        ariaLabel: (row, checked) =>
          `${checked ? 'Désactiver' : row['mode'] === 'unsupported' ? 'Configurer' : 'Activer'} l’accès de ${row['workerName']}`,
        disabledWhen: (row) =>
          this.accessActivationBusy().has(`${this.catalog.selectedLibrary()?.id}:${row['id']}`),
        confirmation: (row, checked) =>
          checked
            ? undefined
            : {
                title: 'Désactiver cet accès ?',
                message: `Le worker « ${row['workerName']} » ne recevra plus de tâche pour cette bibliothèque.`,
                confirmLabel: 'Désactiver',
              },
      },
    },
    { key: 'modeLabel', label: 'Accès' },
    { key: 'location', label: 'Chemin ou source' },
    { key: 'permissions', label: 'Droits' },
  ];

  readonly accessRows = computed(() => this.selectedAccesses().map((access) => toAccessRow(access)));

  readonly accessRowActions: readonly DataTableRowAction[] = [
    { id: 'edit', label: 'Modifier' },
    {
      id: 'delete',
      label: 'Supprimer',
      tone: 'danger',
      confirmation: (row) => ({
        title: 'Supprimer cet accès ?',
        message: `L’accès de « ${row['workerName']} » sera retiré de cette bibliothèque.`,
        confirmLabel: 'Supprimer l’accès',
        variant: 'danger',
      }),
    },
  ];

  loadSelectedAccesses(libraryId: string): void {
    this.selectedAccessesLoading.set(true);
    this.http
      .get<readonly LibraryWorkerAccessResponse[]>(`/api/v1/libraries/${libraryId}/worker-access`)
      .pipe(
        catchError(() => {
          this.notifications.show('Les accès de cette bibliothèque n’ont pas pu être chargés.');
          return of([]);
        }),
        finalize(() => this.selectedAccessesLoading.set(false)),
      )
      .subscribe((accesses) => this.selectedAccesses.set(accesses));
  }

  configureAccesses(): void {
    const library = this.catalog.selectedLibrary();
    if (!library) return;
    this.catalog.actionType.set('access');
    this.catalog.accessSetupMode.set(false);
    this.catalog.actionOpen.set(true);
    this.loadAccessForm(library.id);
  }

  /** Après création d’une bibliothèque : ouvrir directement la configuration des accès. */
  startSetupForNewLibrary(libraryId: string): void {
    this.catalog.actionType.set('access');
    this.catalog.accessSetupMode.set(true);
    this.loadAccessForm(libraryId);
  }

  configureLibraryAccess(values: readonly LibraryWorkerAccessValue[]): void {
    this.saveAccessConfiguration(values);
  }

  onAccessRowAction(event: DataTableActionEvent): void {
    const access = this.selectedAccesses().find((item) => item.workerId === event.row['id']);
    if (!access) return;

    if (event.actionId === 'edit') {
      this.configureAccesses();
      return;
    }

    const updatedAccesses = this.selectedAccesses()
      .filter((item) => event.actionId !== 'delete' || item.workerId !== access.workerId)
      .map((item) => ({
        workerId: item.workerId,
        mode: item.mode,
        isEnabled: item.isEnabled,
        localPath: item.localPath,
        canRead: item.canRead,
        canWrite: item.canWrite,
        transferSourceWorkerId: item.transferSourceWorkerId,
      }));

    this.saveAccessConfiguration(updatedAccesses);
  }

  onAccessToggle(event: DataTableToggleEvent): void {
    const access = this.selectedAccesses().find((item) => item.workerId === event.row['id']);
    if (!access) return;

    if (event.checked && access.mode === 'unsupported') {
      this.configureAccesses();
      return;
    }

    const library = this.catalog.selectedLibrary();
    if (!library) return;

    const busyKey = `${library.id}:${access.workerId}`;
    this.accessActivationBusy.update((current) => new Set(current).add(busyKey));

    this.http
      .put<LibraryWorkerAccessResponse>(
        `/api/v1/libraries/${library.id}/worker-access/${access.workerId}/activation`,
        { isEnabled: event.checked },
      )
      .pipe(
        catchError(() => {
          this.notifications.show('L’accès de ce worker n’a pas pu être modifié.');
          return EMPTY;
        }),
        finalize(() =>
          this.accessActivationBusy.update((current) => {
            const next = new Set(current);
            next.delete(busyKey);
            return next;
          }),
        ),
      )
      .subscribe((updated) => {
        const accesses = this.selectedAccesses().map((item) =>
          item.workerId === updated.workerId ? updated : item,
        );
        this.selectedAccesses.set(accesses);
        this.applyAccessSummary(library, accesses);
        this.notifications.show(
          `Accès à « ${library.name} » ${event.checked ? 'réactivé' : 'désactivé'} pour ${access.workerName}.`,
        );
      });
  }

  private loadAccessForm(libraryId: string): void {
    this.accessFormLoading.set(true);

    const otherLibraryIds = this.catalog
      .libraries()
      .map((library) => library.id)
      .filter((id) => id !== libraryId);

    forkJoin({
      nodes: this.clusterApi.listNodes(),
      groups: this.http.get<readonly WorkerGroupResponse[]>('/api/v1/worker-groups'),
      accesses: this.http.get<readonly LibraryWorkerAccessResponse[]>(
        `/api/v1/libraries/${libraryId}/worker-access`,
      ),
      otherLibraryAccesses: otherLibraryIds.length
        ? forkJoin(
            otherLibraryIds.map((id) =>
              this.http.get<readonly LibraryWorkerAccessResponse[]>(
                `/api/v1/libraries/${id}/worker-access`,
              ),
            ),
          )
        : of([] as readonly (readonly LibraryWorkerAccessResponse[])[]),
    })
      .pipe(
        catchError(() => {
          this.accessFormLoading.set(false);
          this.notifications.show(
            'Les workers ou les accès de cette bibliothèque n’ont pas pu être chargés.',
          );
          return EMPTY;
        }),
      )
      .subscribe(({ nodes, groups, accesses, otherLibraryAccesses }) => {
        this.accessNodes.set(nodes);
        this.accessNodeGroups.set(groups);
        this.initialAccesses.set(accesses);
        this.automaticallySelectedAccessNodeIds.set([
          ...new Set(
            otherLibraryAccesses
              .flat()
              .filter((access) => access.isEnabled && access.mode === 'direct')
              .map((access) => access.workerId),
          ),
        ]);
        this.accessFormLoading.set(false);
      });
  }

  private saveAccessConfiguration(values: readonly LibraryWorkerAccessValue[]): void {
    const library = this.catalog.selectedLibrary();
    if (!library) return;

    this.catalog.accessFormBusy.set(true);

    this.http
      .put<readonly LibraryWorkerAccessResponse[]>(`/api/v1/libraries/${library.id}/worker-access`, {
        nodes: values,
      })
      .pipe(
        catchError((error: { status?: number; error?: { errors?: Record<string, string[]> } }) => {
          this.catalog.accessFormBusy.set(false);
          const validationMessage = error.error?.errors
            ? Object.values(error.error.errors).flat()[0]
            : undefined;
          this.notifications.show(
            validationMessage ??
              'Les accès de la bibliothèque n’ont pas pu être enregistrés. Vérifiez le serveur et les workers sélectionnés.',
          );
          return EMPTY;
        }),
      )
      .subscribe((accesses) => {
        this.catalog.accessFormBusy.set(false);
        this.selectedAccesses.set(accesses);
        this.applyAccessSummary(library, accesses);
        this.catalog.actionOpen.set(false);
        this.notifications.show(
          `Accès de « ${library.name} » enregistrés pour ${accesses.filter((item) => item.mode !== 'unsupported').length} worker(s).`,
        );
      });
  }

  private applyAccessSummary(
    library: LibraryRecord,
    accesses: readonly LibraryWorkerAccessResponse[],
  ): void {
    const summary = toAccessSummary(accesses, library.state);
    const state = library.state === 'Désactivée' ? 'Désactivée' : summary.state;

    this.catalog.libraries.update((current) =>
      current.map((item) => (item.id === library.id ? { ...item, ...summary, state } : item)),
    );
    this.catalog.selectedLibrary.update((item) =>
      item?.id === library.id ? { ...item, ...summary, state } : item,
    );
  }
}
