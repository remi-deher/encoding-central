import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  input,
  signal,
  OnChanges,
  OnDestroy,
  output,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatTabsModule } from '@angular/material/tabs';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { MatDialog } from '@angular/material/dialog';
import { firstValueFrom, interval } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FlowValidation } from '../../../shared/services/flow-validation';
import {
  FlowDocument,
  FlowPluginCatalog,
  FlowVersionResponse,
} from '../../../shared/models/flow-validation';
import { flowDocumentNodes } from '../../../shared/models/flow-document-graph';
import { compareFlowVersions } from '../../../shared/models/flow-version-comparison';
import { FlowGraphEditorComponent } from '../../../shared/ui/flow-graph-editor.component';
import { ConfirmationDialogComponent } from '../../../shared/ui/confirmation-dialog.component';
import { FileImportComponent } from '../../../shared/ui/file-import.component';
import { FileDownload } from '../../../shared/services/file-download';
import { NotificationService } from '../../../shared/services/notification.service';
import { AuthService } from '../../../core/auth/auth.service';

import { formatFullDateTime } from '../../../shared/models/date-format';

type Selection =
  | { kind: 'current' }
  | { kind: 'published'; version: number }
  | { kind: 'backup'; revision: number };
@Component({
  selector: 'app-flow-details',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink,
    MatTabsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatSelectModule,
    FlowGraphEditorComponent,
    FileImportComponent,
  ],
  templateUrl: './flow-details.component.html',
  styleUrl: './flow-details.component.scss',
})
export class FlowDetailsComponent implements OnChanges, OnDestroy {
  private readonly destroyRef = inject(DestroyRef);
  protected readonly formatDate = formatFullDateTime;
  readonly flowId = input.required<string>();
  readonly initialTab = input(0);
  readonly refreshed = output<void>();
  private readonly api = inject(FlowValidation);
  protected readonly auth = inject(AuthService);
  private readonly dialog = inject(MatDialog);
  private readonly downloads = inject(FileDownload);
  private readonly notifications = inject(NotificationService);
  protected readonly tab = signal(0);
  protected readonly busy = signal(false);
  protected readonly current = signal<FlowVersionResponse | null>(null);
  protected readonly document = signal<FlowDocument | null>(null);
  protected readonly catalog = signal<readonly FlowPluginCatalog[]>([]);
  protected readonly selection = signal<Selection>({ kind: 'current' });
  protected readonly previewState = signal<'loading' | 'ready' | 'error'>('loading');
  protected readonly selectedDate = signal<string | null>(null);
  protected readonly selectedBackup = signal('');
  protected readonly versions = signal<readonly FlowDocument[]>([]);
  protected readonly backups = signal<Awaited<ReturnType<FlowValidation['draftBackups']>>>([]);
  protected readonly usage = signal<Awaited<ReturnType<FlowValidation['usage']>>>([]);
  protected readonly preflight = signal<Awaited<ReturnType<FlowValidation['preflight']>> | null>(
    null,
  );
  protected readonly errors = signal<ReadonlySet<string>>(new Set());
  protected readonly extrasLoaded = signal(false);
  protected readonly compareBefore = signal<number | null>(null);
  protected readonly compareAfter = signal<number | null>(null);
  protected readonly comparison = computed(() => {
    const a = this.versions().find((v) => v.version === this.compareBefore()),
      b = this.versions().find((v) => v.version === this.compareAfter());
    if (!a || !b) return null;
    const nodes = new Map(
      [...flowDocumentNodes(a, this.catalog()), ...flowDocumentNodes(b, this.catalog())].map(
        (node) => [node.id, node],
      ),
    );
    return compareFlowVersions(a, b).map((change) => {
      const parts = change.label
        .split('/')
        .slice(1)
        .map((part) => part.replaceAll('~1', '/').replaceAll('~0', '~'));
      const [area, id, field, key] = parts;
      if (area === 'name') return { ...change, label: 'Nom du flow' };
      if (area === 'mediaType') return { ...change, label: 'Type de média' };
      if (area === 'nodes') {
        const node = nodes.get(id),
          title = node?.name || node?.component.name || id;
        if (!field)
          return {
            label: 'Étape : ' + title,
            detail: change.detail.startsWith('Absent') ? 'Étape ajoutée' : 'Étape retirée',
          };
        const descriptor = node?.component.configurationSchema?.fields.find(
          (item) => item.id === key,
        );
        const label =
          field === 'configuration'
            ? 'Paramètre : ' + (descriptor?.label ?? key)
            : field === 'name'
              ? 'Nom'
              : 'Composant';
        return {
          label: title + ' · ' + label,
          detail:
            descriptor?.type === 'secret'
              ? 'Valeur confidentielle modifiée'
              : change.detail.length > 1000
                ? 'Contenu modifié (ouvrir la version pour consulter la définition).'
                : change.detail,
        };
      }
      if (area === 'connections') {
        const edge =
          b.connections.find((edge) => edge.id === id) ??
          a.connections.find((edge) => edge.id === id);
        const source = edge ? nodes.get(edge.sourceNodeId) : undefined,
          target = edge ? nodes.get(edge.targetNodeId) : undefined;
        return {
          label:
            'Connexion : ' +
            (source?.name || source?.component.name || '?') +
            ' → ' +
            (target?.name || target?.component.name || '?'),
          detail: !field
            ? change.detail.startsWith('Absent')
              ? 'Connexion ajoutée'
              : 'Connexion retirée'
            : 'Raccordement modifié',
        };
      }
      return change;
    });
  });
  protected readonly nodes = computed(() =>
    this.document() ? flowDocumentNodes(this.document()!, this.catalog()) : [],
  );
  protected readonly requirements = computed(() => [
    ...new Set(
      this.document()?.nodes.map((n) => n.component.pluginId + ' · ' + n.component.pluginVersion) ??
        [],
    ),
  ]);
  protected readonly previewLabel = computed(() => {
    const s = this.selection();
    if (!this.current()) return 'Chargement du flow';
    return s.kind === 'published'
      ? 'Version publiée v' + s.version
      : s.kind === 'backup'
        ? 'Sauvegarde de brouillon r' + s.revision
        : this.current()?.draftRevision != null
          ? 'Brouillon actuel r' + this.current()!.draftRevision
          : 'Version publiée v' + this.document()?.version;
  });
  private generation = 0;
  private previewGeneration = 0;
  constructor() {
    interval(60_000)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => void this.refreshInBackground());
  }
  ngOnChanges(): void {
    void this.load();
  }
  ngOnDestroy(): void {
    this.generation++;
    this.previewGeneration++;
  }
  protected async load(): Promise<void> {
    const generation = ++this.generation,
      id = this.flowId();
    this.previewGeneration++;
    this.current.set(null);
    this.document.set(null);
    this.catalog.set([]);
    this.versions.set([]);
    this.backups.set([]);
    this.usage.set([]);
    this.preflight.set(null);
    this.errors.set(new Set());
    this.extrasLoaded.set(false);
    this.previewState.set('loading');
    this.selection.set({ kind: 'current' });
    this.tab.set(this.initialTab());
    this.busy.set(false);
    this.compareBefore.set(null);
    this.compareAfter.set(null);
    this.selectedDate.set(null);
    const extras = Promise.allSettled([
      this.api.versions(id),
      this.api.draftBackups(id),
      this.api.usage(id),
      this.api.preflight(id),
    ]).then((results) => {
      if (generation !== this.generation) return;
      const [versions, backups, usage, preflight] = results;
      if (versions.status === 'fulfilled') {
        this.versions.set([...versions.value].sort((a, b) => b.version - a.version));
        this.compareBefore.set(this.versions().at(1)?.version ?? null);
        this.compareAfter.set(this.versions().at(0)?.version ?? null);
      } else this.fail('versions');
      if (backups.status === 'fulfilled') this.backups.set(backups.value);
      else this.fail('backups');
      if (usage.status === 'fulfilled') this.usage.set(usage.value);
      else this.fail('usage');
      if (preflight.status === 'fulfilled') this.preflight.set(preflight.value);
      else this.fail('validation');
      this.extrasLoaded.set(true);
    });
    try {
      const [saved, plugins] = await Promise.all([this.api.get(id), this.api.catalog()]);
      if (generation !== this.generation) return;
      this.current.set(saved);
      this.catalog.set(plugins);
      this.document.set(saved.definition);
      this.selectedDate.set(saved.createdAt);
      this.selectedBackup.set(saved.backupStatus);
      this.previewState.set('ready');
    } catch {
      if (generation === this.generation) this.previewState.set('error');
    }
    await extras;
  }
  private async refreshInBackground(): Promise<void> {
    if (!this.current() || this.busy()) return;
    const generation = this.generation;
    const id = this.flowId();
    const results = await Promise.allSettled([
      this.api.get(id),
      this.api.catalog(),
      this.api.versions(id),
      this.api.draftBackups(id),
      this.api.usage(id),
      this.api.preflight(id),
    ]);
    if (generation !== this.generation) return;

    const [current, catalog, versions, backups, usage, preflight] = results;
    const errors = new Set<string>();
    if (current.status === 'fulfilled') {
      this.current.set(current.value);
      if (this.selection().kind === 'current') {
        this.document.set(current.value.definition);
        this.selectedDate.set(current.value.createdAt);
        this.selectedBackup.set(current.value.backupStatus);
      }
    } else errors.add('current');
    if (catalog.status === 'fulfilled') this.catalog.set(catalog.value);
    else errors.add('catalog');
    if (versions.status === 'fulfilled')
      this.versions.set([...versions.value].sort((a, b) => b.version - a.version));
    else errors.add('versions');
    if (backups.status === 'fulfilled') this.backups.set(backups.value);
    else errors.add('backups');
    if (usage.status === 'fulfilled') this.usage.set(usage.value);
    else errors.add('usage');
    if (preflight.status === 'fulfilled') this.preflight.set(preflight.value);
    else errors.add('validation');
    this.errors.set(errors);
    this.extrasLoaded.set(true);
  }
  private fail(key: string): void {
    this.errors.update((values) => new Set([...values, key]));
  }
  protected async preview(selection: Selection): Promise<void> {
    if (this.busy() || !this.current()) return;
    const request = ++this.previewGeneration,
      generation = this.generation,
      id = this.flowId();
    this.selection.set(selection);
    this.previewState.set('loading');
    this.document.set(null);
    this.selectedDate.set(null);
    this.selectedBackup.set('');
    try {
      let definition: FlowDocument,
        date: string | null = null,
        backup = '';
      if (selection.kind === 'current') {
        const saved = this.current();
        if (!saved) throw Error('Unavailable');
        definition = saved.definition;
        date = saved.createdAt;
        backup = saved.backupStatus;
      } else if (selection.kind === 'published') {
        const saved = await firstValueFrom(this.api.versionStatus(id, selection.version));
        definition = saved.definition;
        date = saved.createdAt;
        backup = saved.backupStatus;
      } else {
        definition = await this.api.draftBackup(id, selection.revision);
        backup = this.backups().find((b) => b.revision === selection.revision)?.backupStatus ?? '';
      }
      if (request !== this.previewGeneration || generation !== this.generation) return;
      this.document.set(definition);
      this.selectedDate.set(date);
      this.selectedBackup.set(backup);
      this.previewState.set('ready');
      this.tab.set(0);
    } catch {
      if (request === this.previewGeneration && generation === this.generation)
        this.previewState.set('error');
    }
  }
  protected async restore(source = this.document()): Promise<void> {
    const current = this.current();
    if (!source || !current || !this.auth.isAdmin() || this.busy()) return;
    const generation = this.generation;
    this.busy.set(true);
    try {
      const confirmed = await firstValueFrom(
        this.dialog
          .open(ConfirmationDialogComponent, {
            data: {
              title: 'Créer un brouillon depuis cet aperçu ?',
              message:
                `Source : « ${source.name} ». Destination : « ${current.definition.name} ». ` +
                (current.draftRevision != null
                  ? 'Le brouillon actuel sera remplacé. Sa sauvegarde historique reste conservée. Les versions publiées et les affectations des bibliothèques restent inchangées.'
                  : 'Cette définition sera copiée dans un brouillon à examiner avant publication. Les affectations des bibliothèques restent inchangées.'),
              confirmLabel: 'Créer le brouillon',
            },
          })
          .afterClosed(),
      );
      if (!confirmed || generation !== this.generation) return;
      const published =
        current.publishedVersion ??
        (current.draftRevision == null ? current.definition.version : 0);
      const saved = await this.api.saveDraft(
        { ...source, id: current.definition.id, version: published + 1 },
        current.draftRevision ?? 0,
      );
      if (generation !== this.generation) return;
      this.current.set(saved);
      this.refreshed.emit();
      this.notifications.show('Brouillon créé. Examinez-le dans l’éditeur avant publication.');
      this.previewGeneration++;
      this.selection.set({ kind: 'current' });
      this.document.set(saved.definition);
      this.selectedDate.set(saved.createdAt);
      this.selectedBackup.set(saved.backupStatus);
      this.previewState.set('ready');
      this.tab.set(0);
      this.preflight.set(null);
      try {
        const result = await this.api.preflight(current.definition.id);
        if (generation === this.generation) this.preflight.set(result);
      } catch {
        if (generation === this.generation) this.fail('validation');
      }
      try {
        const backups = await this.api.draftBackups(current.definition.id);
        if (generation === this.generation) this.backups.set(backups);
      } catch {
        if (generation === this.generation) this.fail('backups');
      }
    } catch (error) {
      this.notifications.show(
        AuthService.errorMessage(
          error,
          'Restauration impossible. Rechargez le panneau si le brouillon a changé.',
        ),
      );
    } finally {
      if (generation === this.generation) this.busy.set(false);
    }
  }
  protected async duplicate(): Promise<void> {
    if (!this.current() || !this.auth.isAdmin() || this.busy()) return;
    const id = this.flowId(),
      generation = this.generation;
    this.busy.set(true);
    try {
      const saved = await this.api.clone(
        id,
        this.current()!.definition.name.slice(0, 116) + ' — Copie',
      );
      this.notifications.show('Flow dupliqué : ' + saved.definition.name);
      this.refreshed.emit();
    } catch (error) {
      this.notifications.show(AuthService.errorMessage(error, 'Duplication impossible.'));
    } finally {
      if (generation === this.generation) this.busy.set(false);
    }
  }
  protected async download(kind: 'json' | 'zip'): Promise<void> {
    try {
      if (kind === 'json' && this.document())
        this.downloads.save(
          new Blob([JSON.stringify(this.document(), null, 2)], { type: 'application/json' }),
          'flow-' + this.flowId() + '.json',
        );
      else if (kind === 'zip')
        this.downloads.save(
          await this.api.exportMany([this.flowId()]),
          'flow-' + this.flowId() + '.zip',
        );
    } catch {
      this.notifications.show('Export impossible.');
    }
  }
  protected async importBackup(file: File): Promise<void> {
    const generation = this.generation;
    try {
      if (file.size > 2 * 1024 * 1024) throw Error('Fichier trop volumineux (2 Mo maximum).');
      const value = JSON.parse(await file.text());
      const definition = value.definition ?? value;
      if (
        !definition ||
        typeof definition !== 'object' ||
        !Array.isArray(definition.nodes) ||
        !Array.isArray(definition.connections) ||
        definition.formatVersion !== 1
      )
        throw Error('Définition de flow JSON invalide.');
      if (generation !== this.generation) return;
      await this.restore(definition as FlowDocument);
    } catch (error) {
      this.notifications.show(error instanceof Error ? error.message : 'Sauvegarde illisible.');
    }
  }
  protected backupLabel(status: string): string {
    return status === 'ready'
      ? 'Vérifiée'
      : status === 'failed'
        ? 'En échec'
        : status === 'pending'
          ? 'En attente'
          : 'En base';
  }
}
