import { HttpClient } from '@angular/common/http';
import { computed, inject, Injectable, signal } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { Router, ActivatedRoute } from '@angular/router';
import { catchError, EMPTY, finalize, firstValueFrom, forkJoin, map, of } from 'rxjs';
import { formatDateOnly } from '../../shared/models/date-format';
import { CentralApi } from '../../shared/services/central-api';
import { NotificationService } from '../../shared/services/notification.service';
import { ConfirmationDialogComponent } from '../../shared/ui/confirmation-dialog.component';
import { FileDownload } from '../../shared/services/file-download';
import { LibraryResponse } from '../../shared/models/central';
import {
  DataTableActionEvent,
  DataTableRow,
  DataTableRowAction,
  DataTableSelectionActionEvent,
} from '../../shared/ui/data-table.component';
import type { ResourceListToggleEvent } from '../../shared/ui/resource-list.component';
import { PageShellSearchResult } from '../../core/layout/page-shell-context';
import { LibraryProcessingSettings } from '../../shared/models/library-workflow';
import {
  CREATE_LIBRARY_FORM_DATA,
  LibraryActionType,
  LibraryExportFile,
  LibraryRecord,
  libraryState,
  toAccessSummaryFromCounts,
} from './library-catalog.utils';

/**
 * Liste des bibliothèques : chargement, enregistrement local, recherche,
 * sélection, création/édition/suppression, activation et import/export.
 * Le workflow (flow attribué, découverte, accès) vit dans LibraryWorkflowStore.
 */
@Injectable()
export class LibraryCatalogStore {
  private readonly http = inject(HttpClient);
  private readonly clusterApi = inject(CentralApi);
  private readonly dialog = inject(MatDialog);
  private readonly notifications = inject(NotificationService);
  private readonly downloads = inject(FileDownload);
  readonly route = inject(ActivatedRoute);
  readonly router = inject(Router);

  readonly libraries = signal<readonly LibraryRecord[]>([]);
  readonly searchQuery = signal('');
  readonly selectedLibraryIds = signal<ReadonlySet<string>>(new Set());
  readonly selectedLibrary = signal<LibraryRecord | null>(null);
  readonly selectedLibraryTabIndex = signal(0);
  readonly processingByLibrary = signal<Readonly<Record<string, LibraryProcessingSettings>>>({});
  readonly actionOpen = signal(false);
  readonly actionType = signal<LibraryActionType>('create');
  readonly accessSetupMode = signal(false);
  readonly accessFormBusy = signal(false);
  readonly activationBusy = signal<ReadonlySet<string>>(new Set());

  readonly createFormData = CREATE_LIBRARY_FORM_DATA;

  readonly editFormData = computed(() => ({
    ...this.createFormData,
    title: 'Modifier la bibliothèque',
    description: 'Modifiez le nom de la définition partagée sans changer les accès configurés.',
    submitLabel: 'Enregistrer la définition',
    initialValues: {
      name: this.selectedLibrary()?.name ?? '',
    },
  }));

  readonly libraryRowActions: readonly DataTableRowAction[] = [
    { id: 'edit', label: 'Modifier' },
    { id: 'flow', label: 'Attribuer un flow' },
    { id: 'processing', label: 'Surveillance et limites' },
    {
      id: 'delete',
      label: 'Supprimer',
      tone: 'danger',
      confirmation: (row) => ({
        title: 'Supprimer cette bibliothèque ?',
        message: `La définition « ${row['name']} » et ses accès par worker seront supprimés. Les fichiers média ne seront pas touchés. Cette action est irréversible.`,
        confirmLabel: 'Supprimer',
        variant: 'danger',
      }),
    },
  ];

  readonly librarySelectionActions = [
    {
      id: 'activate',
      label: 'Activer',
      visibleWhen: (rows: readonly DataTableRow[]) =>
        rows.some(
          (row) => !this.libraries().find((item) => item.id === String(row['id']))?.isActive,
        ),
    },
    {
      id: 'deactivate',
      label: 'Désactiver',
      visibleWhen: (rows: readonly DataTableRow[]) =>
        rows.some(
          (row) => this.libraries().find((item) => item.id === String(row['id']))?.isActive,
        ),
    },
    { id: 'delete', label: 'Supprimer', visibleWhen: () => true },
  ];

  librarySearchResults(query: string): readonly PageShellSearchResult[] {
    const normalized = query.trim().toLocaleLowerCase('fr-FR');
    return this.libraries()
      .filter(
        (library) =>
          !normalized ||
          [
            library.name,
            library.state,
            library.assignedFlow,
            library.configuredNodes,
            library.accessModes,
            library.nodePaths,
          ]
            .join(' ')
            .toLocaleLowerCase('fr-FR')
            .includes(normalized),
      )
      .slice(0, 8)
      .map((library) => ({
        id: `library:${library.id}`,
        label: library.name,
        description: `${library.state} · ${library.assignedFlow}`,
        group: 'Bibliothèques',
        icon: 'library' as const,
        run: () => this.onLibraryOpen({ id: library.id }),
      }));
  }

  hasSelectedLibraries(): boolean {
    const selected = this.selectedLibraryIds();
    return this.libraries().some((library) => selected.has(library.id));
  }

  async exportSelectedLibraries(): Promise<void> {
    const selectedIds = new Set(
      [...this.selectedLibraryIds()].filter((id) =>
        this.libraries().some((library) => library.id === id),
      ),
    );
    if (!selectedIds.size) return;
    try {
      const exportFile = await firstValueFrom(
        this.http.get<{
          format: number;
          exportedAt: string;
          centralName: string;
          libraries: readonly LibraryResponse[];
        }>('/api/v1/libraries/export'),
      );
      const selected = exportFile.libraries.filter((library) => selectedIds.has(library.id));
      if (!selected.length) {
        this.notifications.show('Aucune bibliothèque sélectionnée à exporter.');
        return;
      }
      this.downloads.save(
        new Blob([JSON.stringify({ ...exportFile, libraries: selected }, null, 2)], {
          type: 'application/json',
        }),
        'bibliotheques-selection.json',
      );
    } catch {
      this.notifications.show('Les bibliothèques sélectionnées n’ont pas pu être exportées.');
    }
  }

  async onLibraryImportSelected(file: File | undefined): Promise<void> {
    if (file) await this.importLibraries(file);
  }

  private async importLibraries(file: File): Promise<void> {
    let entries: readonly Pick<LibraryResponse, 'name' | 'isActive'>[];
    try {
      const parsed: unknown = JSON.parse(await file.text());
      if (
        !parsed ||
        typeof parsed !== 'object' ||
        !('format' in parsed) ||
        parsed.format !== 1 ||
        !('libraries' in parsed) ||
        !Array.isArray(parsed.libraries)
      ) {
        throw new Error('invalid-file');
      }
      const payload = parsed as LibraryExportFile;
      entries = payload.libraries.filter(
        (library): library is Pick<LibraryResponse, 'name' | 'isActive'> =>
          !!library &&
          typeof library === 'object' &&
          'name' in library &&
          typeof library.name === 'string' &&
          !!library.name.trim(),
      );
      if (!entries.length) throw new Error('empty-file');
    } catch {
      this.notifications.show(
        'Import impossible. Sélectionnez un export de bibliothèques au format JSON pris en charge.',
      );
      return;
    }

    let existing: readonly LibraryResponse[];
    try {
      existing = await firstValueFrom(this.clusterApi.listLibraries());
    } catch {
      this.notifications.show('Impossible de charger les bibliothèques avant l’import.');
      return;
    }
    this.libraries.set(existing.map((library) => this.toRecord(library)));

    const knownNames = new Set(existing.map((library) => library.name.toLocaleLowerCase('fr-FR')));
    let imported = 0;
    let skipped = 0;
    let failed = 0;
    let activationFailed = 0;
    for (const entry of entries) {
      const name = entry.name.trim();
      const key = name.toLocaleLowerCase('fr-FR');
      if (knownNames.has(key)) {
        skipped++;
        continue;
      }
      try {
        const created = await firstValueFrom(
          this.http.post<LibraryResponse>('/api/v1/libraries', { name }),
        );
        knownNames.add(key);
        imported++;
        if (entry.isActive === false) {
          try {
            await firstValueFrom(
              this.http.put<LibraryResponse>(`/api/v1/libraries/${created.id}/activation`, {
                isActive: false,
              }),
            );
          } catch {
            activationFailed++;
          }
        }
      } catch (error) {
        if ((error as { status?: number }).status === 409) skipped++;
        else failed++;
      }
    }
    this.refresh();
    const details = [
      `${imported} bibliothèque(s) importée(s)`,
      skipped ? `${skipped} déjà présente(s)` : '',
      failed ? `${failed} en échec` : '',
      activationFailed ? `${activationFailed} état(s) à vérifier` : '',
      imported ? 'flows et accès des workers à reconfigurer' : '',
    ].filter(Boolean);
    this.notifications.show(details.join(' · '));
  }

  onLibraryOpen(row: DataTableRow): void {
    const id = String(row['id']);
    void this.router.navigate([], { relativeTo: this.route, queryParams: { library: id } });
  }

  onDetailClosed(): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { library: null },
      queryParamsHandling: 'merge',
    });
  }

  addLibrary(): void {
    this.actionType.set('create');
    this.actionOpen.set(true);
  }

  closeCreateLibrary(): void {
    this.actionOpen.set(false);
  }

  onLibraryAction(
    event: DataTableActionEvent,
    hooks: {
      onProcessing: (library: LibraryRecord) => void;
      onFlow: (library: LibraryRecord) => void;
    },
  ): void {
    const library = this.libraries().find((item) => item.id === event.row['id']);
    if (!library) return;

    if (event.actionId === 'processing') {
      this.selectedLibrary.set(library);
      hooks.onProcessing(library);
      return;
    }
    if (event.actionId === 'flow') {
      this.selectedLibrary.set(library);
      hooks.onFlow(library);
      return;
    }

    if (event.actionId === 'edit') {
      this.selectedLibrary.set(library);
      this.actionType.set('edit');
      this.actionOpen.set(true);
    } else if (event.actionId === 'delete') {
      this.deleteLibrary(library);
    }
  }

  onLibraryItemToggle(event: ResourceListToggleEvent): void {
    const library = this.libraries().find((item) => item.id === event.id);
    if (library) this.setLibraryActive(library, event.checked);
  }

  async onLibrarySelection(event: DataTableSelectionActionEvent): Promise<void> {
    const selected = event.rows
      .map((row) => this.libraries().find((library) => library.id === String(row['id'])))
      .filter((library): library is LibraryRecord => !!library);
    if (!selected.length) return;
    const targets =
      event.actionId === 'activate'
        ? selected.filter((item) => !item.isActive)
        : event.actionId === 'deactivate'
          ? selected.filter((item) => item.isActive)
          : selected;
    if (!targets.length) return;
    const action = event.actionId;
    const confirmed = await firstValueFrom(
      this.dialog
        .open(ConfirmationDialogComponent, {
          data:
            action === 'delete'
              ? {
                  title: `Supprimer ${targets.length} bibliothèque(s) ?`,
                  message: `Les définitions et accès par worker de ${targets.map((item) => `« ${item.name} »`).join(', ')} seront supprimés. Les fichiers média ne seront pas touchés.`,
                  confirmLabel: 'Supprimer',
                  variant: 'danger',
                }
              : {
                  title: `${action === 'activate' ? 'Activer' : 'Désactiver'} ${targets.length} bibliothèque(s) ?`,
                  message: targets.map((item) => `« ${item.name} »`).join(', '),
                  confirmLabel: action === 'activate' ? 'Activer' : 'Désactiver',
                },
        })
        .afterClosed(),
    );
    if (!confirmed) return;
    const results = await Promise.allSettled(
      targets.map(async (library) => {
        if (action === 'delete') {
          await firstValueFrom(this.http.delete<void>(`/api/v1/libraries/${library.id}`));
          this.libraries.update((items) => items.filter((item) => item.id !== library.id));
          if (this.selectedLibrary()?.id === library.id) this.selectedLibrary.set(null);
        } else {
          const isActive = action === 'activate';
          const updated = await firstValueFrom(
            this.http.put<LibraryResponse>(`/api/v1/libraries/${library.id}/activation`, {
              isActive,
            }),
          );
          this.replaceLibraryRecord(this.toRecord(updated));
        }
      }),
    );
    const succeeded = results.filter((result) => result.status === 'fulfilled').length;
    this.notifications.show(
      `${succeeded}/${targets.length} bibliothèque(s) ${action === 'delete' ? 'supprimée(s)' : action === 'activate' ? 'activée(s)' : 'désactivée(s)'}.`,
    );
  }

  updateLibrary(value: Record<string, string>): void {
    const library = this.selectedLibrary();
    if (!library) return;

    this.http
      .put<LibraryResponse>(`/api/v1/libraries/${library.id}`, {
        name: value['name'],
      })
      .pipe(
        catchError((error: { status?: number }) => {
          this.notifications.show(
            error.status === 409
              ? 'Une bibliothèque porte déjà ce nom.'
              : 'La définition n’a pas pu être modifiée.',
          );
          return EMPTY;
        }),
      )
      .subscribe((updated) => {
        const record = this.toRecord(updated);
        this.libraries.update((items) =>
          items.map((item) => (item.id === updated.id ? record : item)),
        );
        this.selectedLibrary.set(record);
        this.actionOpen.set(false);
        this.notifications.show(`Définition « ${updated.name} » enregistrée.`);
      });
  }

  createLibrary(value: Record<string, string>, onCreated: (library: LibraryRecord) => void): void {
    this.accessFormBusy.set(true);

    this.http
      .post<LibraryResponse>('/api/v1/libraries', value)
      .pipe(
        catchError((error: { status?: number }) => {
          const message =
            error.status === 409
              ? 'Une bibliothèque porte déjà ce nom.'
              : 'La bibliothèque n’a pas pu être créée. Vérifiez que le serveur est disponible.';
          this.notifications.show(message);
          return of(null);
        }),
      )
      .subscribe((created) => {
        this.accessFormBusy.set(false);
        if (!created) return;
        const record = this.toRecord(created);
        this.libraries.update((current) => [...current, record]);
        this.selectedLibrary.set(record);
        this.notifications.show(
          `Bibliothèque « ${created.name} » créée. Vous pouvez maintenant configurer les accès de ses workers.`,
        );
        onCreated(record);
      });
  }

  private setLibraryActive(library: LibraryRecord, isActive: boolean): void {
    this.activationBusy.update((busy) => new Set(busy).add(library.id));

    this.replaceLibraryRecord({
      ...library,
      isActive,
      state: libraryState(isActive, library.configuredWorkerCount),
    });

    this.http
      .put<LibraryResponse>(`/api/v1/libraries/${library.id}/activation`, { isActive })
      .pipe(
        catchError(() => {
          this.replaceLibraryRecord(library);
          this.notifications.show('L’état de la bibliothèque n’a pas pu être modifié.');
          return EMPTY;
        }),
        finalize(() =>
          this.activationBusy.update((busy) => {
            const next = new Set(busy);
            next.delete(library.id);
            return next;
          }),
        ),
      )
      .subscribe((updated) => {
        const record = this.toRecord(updated);
        this.replaceLibraryRecord(record);
        this.notifications.show(
          `Bibliothèque « ${updated.name} » ${isActive ? 'réactivée' : 'désactivée'}.`,
        );
      });
  }

  replaceLibraryRecord(record: LibraryRecord): void {
    this.libraries.update((items) => items.map((item) => (item.id === record.id ? record : item)));
    this.selectedLibrary.update((current) => (current?.id === record.id ? record : current));
  }

  deleteLibrary(library: LibraryRecord): void {
    this.http
      .delete<void>(`/api/v1/libraries/${library.id}`)
      .pipe(
        catchError(() => {
          this.notifications.show('La bibliothèque n’a pas pu être supprimée.');
          return EMPTY;
        }),
      )
      .subscribe(() => {
        this.libraries.update((items) => items.filter((item) => item.id !== library.id));
        if (this.selectedLibrary()?.id === library.id) this.selectedLibrary.set(null);
        this.notifications.show(
          `Bibliothèque « ${library.name} » supprimée. Les fichiers média sont conservés.`,
        );
      });
  }

  refresh(): void {
    this.clusterApi
      .listLibraries()
      .pipe(
        catchError(() => {
          this.notifications.show('Impossible de charger les bibliothèques depuis le serveur.');
          return of([]);
        }),
      )
      .subscribe((libraries) => {
        this.libraries.set(libraries.map((library) => this.toRecord(library)));
        if (!libraries.length) return;
        forkJoin(
          libraries.map((library) =>
            this.clusterApi.libraryProcessing(library.id).pipe(
              map((settings) => [library.id, settings] as const),
              catchError(() => of(null)),
            ),
          ),
        ).subscribe((entries) => {
          const valid = entries.filter(
            (entry): entry is readonly [string, LibraryProcessingSettings] => entry !== null,
          );
          if (valid.length) {
            this.processingByLibrary.update((current) => ({
              ...current,
              ...Object.fromEntries(valid),
            }));
          }
        });
      });
  }

  toRecord(library: LibraryResponse): LibraryRecord {
    const date = new Date(library.createdAt);
    const summary = toAccessSummaryFromCounts(library);
    const existing = this.libraries().find((item) => item.id === library.id);
    const assignedFlow =
      library.flowName !== undefined
        ? library.flowName && library.flowVersion != null
          ? `${library.flowName} · v${library.flowVersion}`
          : 'Aucun flow'
        : (existing?.assignedFlow ?? 'Aucun flow');
    const processing = this.processingByLibrary()[library.id];
    const monitoring = processing
      ? processing.watchEnabled
        ? 'Automatique'
        : 'Manuelle'
      : (existing?.monitoring ?? 'Manuelle');

    return {
      id: library.id,
      name: library.name,
      isActive: library.isActive,
      ...summary,
      lastScan: 'Jamais',
      associatedFlows: assignedFlow,
      assignedFlow,
      monitoring,
      modifiedAt: formatDateOnly(date),
      modifiedOrder: date.getTime(),
    };
  }
}
