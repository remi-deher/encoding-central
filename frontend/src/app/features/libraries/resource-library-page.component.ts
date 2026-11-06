import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { ActivatedRoute, Router } from '@angular/router';
import { map } from 'rxjs';
import { FlowResourceDetails, FlowResourceSummary } from '../../shared/models/flow-resources';
import { formatShortDateTime } from '../../shared/models/date-format';
import { FlowResources } from '../../shared/services/flow-resources';
import { PageShellContextService } from '../../core/layout/page-shell-context';
import { NotificationService } from '../../shared/services/notification.service';
import { ResourceCatalogTemplateComponent } from '../../shared/templates/resource-catalog-template.component';
import { ConfirmationDialogComponent } from '../../shared/ui/confirmation-dialog.component';
import { DrawerActionButtonComponent } from '../../shared/ui/drawer-action-button/drawer-action-button.component';
import { ResourceListItem } from '../../shared/ui/resource-list.component';
import { LibrarySectionTabsComponent } from './library-section-tabs.component';
import {
  ResourceSheetComponent,
  ResourceSheetSection,
} from '../../shared/ui/resource-sheet.component';
import { SubnavComponent, SubnavOption } from '../../shared/ui/subnav.component';
import { SubtitleProfileEditorComponent } from './subtitle-profile-editor.component';

/** Taille lisible d'un fichier ou d'une ressource. */
export function formatResourceSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} o`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} Ko`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} Mo`;
}

type EditorMode = 'create' | 'version';
type ResourceSection = 'font' | 'subtitle-profile' | 'subtitle';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-resource-library-page',
  imports: [
    ResourceCatalogTemplateComponent,
    LibrarySectionTabsComponent,
    DrawerActionButtonComponent,
    ResourceSheetComponent,
    SubnavComponent,
    SubtitleProfileEditorComponent,
    ReactiveFormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
  ],
  template: `
    <app-resource-catalog-template
      [title]="title()"
      [items]="items()"
      [showToolbar]="false"
      resourceIcon="copy"
      [listEmptyMessage]="emptyMessage()"
      openLabel="Ouvrir"
      (open)="openResource($event)"
      [catalogState]="catalogState()"
      [catalogMessage]="catalogMessage()"
      [actionOpen]="editorMode() !== null"
      [actionTitle]="actionTitle()"
      [actionDescription]="editorDescription()"
      [detailOpen]="selected() !== null"
      [detailTitle]="selected()?.summary?.name ?? 'Ressource'"
      [detailDescription]="selected()?.summary?.description ?? ''"
      detailLayout="drawer"
      (detailClosed)="closeDetail()"
      (actionClosed)="closeEditor()"
    >
      <ng-template #catalogueNavigation>
        <app-library-section-tabs catalogue-navigation selected="resources" />
        <app-subnav
          label="Type de ressource"
          presentation="capsule"
          [options]="kindOptions"
          [selected]="section()"
          (selectedChange)="selectSection($event)"
        />
      </ng-template>

      <ng-template #detailContent catalogue-detail>
        @if (selected(); as details) {
          <app-resource-sheet
            ariaLabel="Versions et fichiers de la ressource"
            [title]="details.summary.name"
            [subtitle]="selectedSubtitle()"
            [sections]="sections()"
          />
        }
      </ng-template>

      <ng-template #detailHeaderActions>
        @if (selected(); as details) {
          <app-drawer-action-button label="Nouvelle version" (activated)="openVersion()" />
          <app-drawer-action-button
            label="Supprimer"
            tone="danger"
            variant="outlined"
            (activated)="confirmDelete(details)"
          />
        }
      </ng-template>

      <ng-template #actionContent>
        @if (section() === 'subtitle-profile') {
          <app-subtitle-profile-editor
            [busy]="busy()"
            [mode]="editorMode() === 'version' ? 'version' : 'create'"
            [documentText]="profileDocument()"
            (submitted)="saveProfile($event)"
            (cancelled)="closeEditor()"
          />
          @if (error()) {
            <p class="error" role="alert">{{ error() }}</p>
          }
        } @else {
          <form class="resource-form" [formGroup]="form" (ngSubmit)="save()">
            @if (editorMode() === 'create') {
              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label>Description</mat-label>
                <textarea
                  matInput
                  rows="2"
                  formControlName="description"
                  maxlength="1000"
                  [readonly]="busy()"
                ></textarea>
              </mat-form-field>
            }
            <input
              #picker
              hidden
              type="file"
              [multiple]="editorMode() === 'create'"
              [accept]="section() === 'subtitle' ? '.srt,.ass,.ssa,.vtt' : '.ttf,.otf,.ttc'"
              (change)="pick(picker)"
            />
            <button matButton="outlined" type="button" [disabled]="busy()" (click)="picker.click()">
              {{ fileButton() }}
            </button>
            <ul class="chosen" aria-label="Fichiers choisis">
              @for (chosen of chosenFiles(); track chosen.file.name) {
                <li>
                  <span>{{ chosen.file.name }}</span>
                  <span class="size">{{ chosen.size }}</span>
                  <button
                    matButton="text"
                    type="button"
                    [disabled]="busy()"
                    [attr.aria-label]="'Retirer ' + chosen.file.name"
                    (click)="removeFile(chosen.file)"
                  >
                    Retirer
                  </button>
                </li>
              }
            </ul>
            <p class="hint">{{ fileHint() }}</p>
            @if (error()) {
              <p class="error" role="alert">{{ error() }}</p>
            }
            <div class="actions">
              <button matButton="text" type="button" [disabled]="busy()" (click)="closeEditor()">
                Annuler
              </button>
              <button matButton="filled" type="submit" [disabled]="!canSave()">
                {{
                  busy()
                    ? 'Envoi en cours…'
                    : editorMode() === 'version'
                      ? 'Publier la version'
                      : 'Ajouter'
                }}
              </button>
            </div>
          </form>
        }
      </ng-template>
    </app-resource-catalog-template>
  `,
  styles: `
    .resource-form {
      display: grid;
      gap: var(--space-3);
    }
    .chosen {
      display: grid;
      gap: var(--space-1);
      list-style: none;
      margin: 0;
      padding: 0;
    }
    .chosen li {
      align-items: center;
      display: flex;
      gap: var(--space-3);
      justify-content: space-between;
    }
    .size,
    .hint {
      color: var(--text-secondary);
      font-size: var(--font-body);
    }
    .error {
      color: var(--danger);
    }
    .actions {
      display: flex;
      gap: var(--space-2);
      justify-content: flex-end;
    }
  `,
})
export class ResourceLibraryPageComponent {
  private readonly api = inject(FlowResources);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly dialog = inject(MatDialog);
  private readonly destroyRef = inject(DestroyRef);
  private readonly pageShellContext = inject(PageShellContextService);
  private readonly notifications = inject(NotificationService);
  private readonly routeData = toSignal(this.route.data, {
    initialValue: this.route.snapshot.data,
  });
  protected readonly title = computed(() => (this.routeData()['title'] as string) ?? 'Ressources');
  protected readonly kindOptions: readonly SubnavOption[] = [
    { value: 'font', label: 'Polices' },
    { value: 'subtitle-profile', label: 'Profils de sous-titres' },
    { value: 'subtitle', label: 'Sous-titres' },
  ];
  private readonly sectionParam = toSignal(
    this.route.queryParamMap.pipe(map((params) => params.get('section'))),
    { initialValue: this.route.snapshot.queryParamMap.get('section') },
  );
  protected readonly section = computed<ResourceSection>(() => {
    const value = this.sectionParam();
    return value === 'subtitle-profile' || value === 'subtitle' ? value : 'font';
  });
  protected readonly profileDocument = signal<string | null>(null);
  protected readonly emptyMessage = computed(() => {
    switch (this.section()) {
      case 'subtitle-profile':
        return 'Aucun profil. Créez-en un : le moteur mettra ses mesures à l’échelle de chaque vidéo.';
      case 'subtitle':
        return 'Aucun fichier de sous-titres. Chaque fichier ajouté devient une ressource réutilisable.';
      default:
        return 'Aucune police. Chaque fichier ajouté devient une ressource réutilisable.';
    }
  });
  protected readonly actionTitle = computed(() => {
    if (this.editorMode() === 'version') return 'Nouvelle version';
    switch (this.section()) {
      case 'subtitle-profile':
        return 'Nouveau profil';
      case 'subtitle':
        return 'Nouveaux sous-titres';
      default:
        return 'Nouvelles polices';
    }
  });
  protected readonly fileButton = computed(() =>
    this.section() === 'subtitle'
      ? this.editorMode() === 'version'
        ? 'Choisir le nouveau fichier (.srt, .ass, .ssa, .vtt)'
        : 'Choisir des sous-titres (.srt, .ass, .ssa, .vtt)'
      : this.editorMode() === 'version'
        ? 'Choisir la nouvelle police (.ttf, .otf, .ttc)'
        : 'Choisir des polices (.ttf, .otf, .ttc)',
  );
  protected readonly fileHint = computed(() =>
    this.section() === 'subtitle'
      ? '20 Mo au plus par fichier. Chaque fichier est une ressource. Une nouvelle version concerne ce fichier seul ; un flow garde la version qu’il cite.'
      : '20 Mo au plus par fichier. Chaque fichier est une ressource, nommée comme le fichier. Une nouvelle version concerne ce fichier seul ; un flow garde la version qu’il cite.',
  );
  protected readonly catalogState = signal<'ready' | 'loading' | 'error'>('loading');
  protected readonly catalogMessage = signal('Chargement des ressources…');
  protected readonly resources = signal<readonly FlowResourceSummary[]>([]);
  protected readonly selected = signal<FlowResourceDetails | null>(null);
  protected readonly query = signal('');
  protected readonly editorMode = signal<EditorMode | null>(null);
  protected readonly files = signal<readonly File[]>([]);
  protected readonly busy = signal(false);
  protected readonly error = signal('');
  protected readonly form = new FormGroup({
    description: new FormControl('', { nonNullable: true }),
  });
  protected readonly editorDescription = computed(() => {
    if (this.editorMode() === 'version')
      return 'La nouvelle version de ce fichier sert aux prochains flows. Les flows déjà publiés gardent la leur.';
    switch (this.section()) {
      case 'subtitle-profile':
        return 'Les mesures sont celles du canevas de référence. Les workers appliquent le profil cité par le flow.';
      case 'subtitle':
        return 'Chaque fichier de sous-titres est une ressource, puis copié sur les workers.';
      default:
        return 'Chaque police est envoyée comme une ressource séparée, puis copiée sur les workers.';
    }
  });
  protected readonly chosenFiles = computed(() =>
    this.files().map((file) => ({ file, size: formatResourceSize(file.size) })),
  );
  protected readonly selectedSubtitle = computed(() => {
    const details = this.selected();
    return details
      ? `${this.kindLabel(details.summary.kind)} · version ${details.summary.latestVersion}`
      : '';
  });
  protected readonly canSave = computed(
    () =>
      !this.busy() &&
      this.files().length > 0 &&
      (this.editorMode() === 'create' || this.files().length === 1),
  );
  protected readonly items = computed<readonly ResourceListItem[]>(() => {
    const query = this.query().trim().toLocaleLowerCase();
    return this.resources()
      .filter((item) => item.kind === this.section())
      .filter(
        (item) =>
          !query ||
          item.name.toLocaleLowerCase().includes(query) ||
          item.description.toLocaleLowerCase().includes(query),
      )
      .map((item) => ({
        id: item.id,
        name: item.name,
        subtitle: item.description || undefined,
        tags: [
          { label: this.kindLabel(item.kind) },
          { label: `v${item.latestVersion}`, tone: 'ok' as const },
          { label: formatResourceSize(item.totalBytes) },
        ],
      }));
  });
  protected readonly sections = computed<readonly ResourceSheetSection[]>(() => {
    const details = this.selected();
    if (!details) return [];
    const summary = details.summary;
    return [
      {
        id: 'resource-summary',
        title: 'Ressource',
        caption: 'Partagée par tout le central',
        fields: [
          { label: 'Nom', value: summary.name },
          { label: 'Description', value: summary.description || 'Aucune description.' },
          { label: 'Identifiant', value: summary.id, mono: true },
          {
            label: 'Mise à jour',
            value: formatShortDateTime(summary.updatedAt),
          },
        ],
      },
      ...details.versions.map((version) => ({
        id: `version-${version.version}`,
        title: `Version ${version.version}`,
        caption:
          (version.version === summary.latestVersion ? 'Dernière · ' : '') +
          `${formatResourceSize(version.totalBytes)} · ${formatShortDateTime(version.createdAt)}`,
        items: version.files.map((file) => ({
          label: file.name,
          detail: `SHA-256 ${file.sha256.slice(0, 16)}…`,
          badges: [{ label: formatResourceSize(file.bytes) }],
        })),
      })),
    ];
  });

  private readonly itemParam = toSignal(
    this.route.queryParamMap.pipe(map((params) => params.get('resource'))),
    { initialValue: this.route.snapshot.queryParamMap.get('resource') },
  );

  constructor() {
    this.destroyRef.onDestroy(
      this.pageShellContext.register({
        searchPlaceholder: 'Rechercher dans les ressources…',
        query: this.query,
        onSearchQueryChange: (query) => this.query.set(query),
        actions: [
          {
            id: 'add-fonts-resource',
            label: 'Ajouter',
            icon: 'add',
            run: () => this.openCreate(),
          },
        ],
      }),
    );
    void this.load();
    // La fiche est portée par l'URL (?resource=<id>), comme pour les modèles de flow.
    effect(() => {
      const id = this.itemParam();
      untracked(() => {
        if (!id) {
          if (this.selected()) this.selected.set(null);
          return;
        }
        if (this.selected()?.summary.id === id) return;
        void this.loadDetails(id);
      });
    });
  }

  protected openResource(id: string): void {
    void this.router.navigate([], { relativeTo: this.route, queryParams: { resource: id } });
  }

  protected closeDetail(): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { resource: null },
      queryParamsHandling: 'merge',
    });
  }

  protected kindLabel(kind: string): string {
    switch (kind) {
      case 'subtitle-profile':
        return 'Profil de sous-titres';
      case 'subtitle':
        return 'Sous-titre';
      default:
        return 'Police';
    }
  }

  protected selectSection(value: string): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { section: value === 'font' ? null : value, resource: null },
      queryParamsHandling: 'merge',
    });
    this.closeEditor();
  }

  protected openCreate(): void {
    this.form.reset({ description: '' });
    this.files.set([]);
    this.profileDocument.set(null);
    this.error.set('');
    this.editorMode.set('create');
  }

  protected openVersion(): void {
    this.files.set([]);
    this.error.set('');
    this.profileDocument.set(null);
    this.editorMode.set('version');
    const details = this.selected();
    if (this.section() === 'subtitle-profile' && details)
      void this.loadProfile(details.summary.id, details.summary.latestVersion);
  }

  protected closeEditor(): void {
    if (this.busy()) return;
    this.editorMode.set(null);
  }

  protected pick(input: HTMLInputElement): void {
    const picked = Array.from(input.files ?? []);
    this.files.set(
      this.editorMode() === 'version'
        ? picked.slice(0, 1)
        : [
            ...this.files(),
            ...picked.filter((file) => !this.files().some((item) => item.name === file.name)),
          ],
    );
    input.value = '';
  }

  protected removeFile(file: File): void {
    this.files.update((current) => current.filter((item) => item !== file));
  }

  protected async save(): Promise<void> {
    if (!this.canSave()) return;
    this.busy.set(true);
    this.error.set('');
    try {
      const chosen = this.files();
      if (this.editorMode() === 'version' && this.selected()) {
        const id = this.selected()!.summary.id;
        const uploaded = await this.api.uploadFile(chosen[0], this.section());
        await this.api.addVersion(id, { name: uploaded.name, sha256: uploaded.sha256 });
        this.editorMode.set(null);
        await this.load();
        await this.loadDetails(id);
        this.notifications.show('Nouvelle version publiée.');
      } else {
        const description = this.form.controls.description.value.trim();
        let lastId = '';
        for (const file of chosen) {
          const uploaded = await this.api.uploadFile(file, this.section());
          const created = await this.api.create(
            uploaded.name,
            description,
            {
              name: uploaded.name,
              sha256: uploaded.sha256,
            },
            this.section(),
          );
          lastId = created.summary.id;
        }
        this.editorMode.set(null);
        await this.load();
        if (lastId) this.openResource(lastId);
        this.notifications.show(
          chosen.length > 1
            ? `${chosen.length} fichiers ajoutés. Les workers les récupèrent automatiquement.`
            : 'Ressource ajoutée. Les workers la récupèrent automatiquement.',
        );
      }
    } catch (error) {
      this.error.set(this.message(error, 'L’envoi a échoué.'));
    } finally {
      this.busy.set(false);
    }
  }

  protected async saveProfile(event: {
    name: string;
    description: string;
    file: File;
  }): Promise<void> {
    this.busy.set(true);
    this.error.set('');
    try {
      const uploaded = await this.api.uploadFile(event.file, 'subtitle-profile');
      if (this.editorMode() === 'version' && this.selected()) {
        const id = this.selected()!.summary.id;
        await this.api.addVersion(id, { name: uploaded.name, sha256: uploaded.sha256 });
        this.editorMode.set(null);
        await this.load();
        await this.loadDetails(id);
        this.notifications.show('Nouvelle version publiée.');
      } else {
        const created = await this.api.create(
          event.name,
          event.description,
          {
            name: uploaded.name,
            sha256: uploaded.sha256,
          },
          'subtitle-profile',
        );
        this.editorMode.set(null);
        await this.load();
        this.openResource(created.summary.id);
        this.notifications.show('Profil ajouté. Les workers le récupèrent automatiquement.');
      }
    } catch (error) {
      this.error.set(this.message(error, 'L’envoi a échoué.'));
    } finally {
      this.busy.set(false);
    }
  }

  private async loadProfile(id: string, version: number): Promise<void> {
    try {
      const content = await this.api.content(id, version);
      this.profileDocument.set(await content.text());
    } catch {
      this.profileDocument.set(null);
    }
  }

  protected confirmDelete(details: FlowResourceDetails): void {
    this.dialog
      .open(ConfirmationDialogComponent, {
        data: {
          title: `Supprimer « ${details.summary.name} » ?`,
          message:
            'Toutes ses versions et leurs fichiers sont supprimés. Impossible si un flow l’utilise.',
          confirmLabel: 'Supprimer',
          variant: 'danger',
        },
      })
      .afterClosed()
      .subscribe((confirmed: boolean | undefined) => {
        if (confirmed) void this.delete(details);
      });
  }

  private async delete(details: FlowResourceDetails): Promise<void> {
    try {
      await this.api.delete(details.summary.id);
      this.notifications.show('Ressource supprimée.');
      await this.load();
      void this.router.navigate([], { relativeTo: this.route, queryParams: {} });
    } catch (error) {
      this.dialog.open(ConfirmationDialogComponent, {
        data: {
          title: 'Suppression impossible',
          message: this.message(error, 'La ressource n’a pas pu être supprimée.'),
          mode: 'information',
          variant: 'danger',
        },
      });
    }
  }

  private async load(): Promise<void> {
    try {
      this.resources.set(await this.api.list());
      this.catalogState.set('ready');
      this.catalogMessage.set('');
    } catch (error) {
      this.catalogState.set('error');
      this.catalogMessage.set(this.message(error, 'Les ressources ne sont pas disponibles.'));
    }
  }

  private async loadDetails(id: string): Promise<void> {
    try {
      this.selected.set(await this.api.get(id));
    } catch {
      this.selected.set(null);
      this.notifications.show('Cette ressource est introuvable.');
    }
  }

  private message(error: unknown, fallback: string): string {
    return error instanceof HttpErrorResponse ? (error.error?.title ?? fallback) : fallback;
  }
}
