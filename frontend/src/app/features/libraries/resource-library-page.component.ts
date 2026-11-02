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
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { ActivatedRoute, Router } from '@angular/router';
import { map } from 'rxjs';
import { FlowResourceDetails, FlowResourceSummary } from '../../shared/models/flow-resources';
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

/** Taille lisible d'un fichier ou d'une ressource. */
export function formatResourceSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} o`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} Ko`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} Mo`;
}

type EditorMode = 'create' | 'version';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-resource-library-page',
  imports: [
    ResourceCatalogTemplateComponent,
    LibrarySectionTabsComponent,
    DrawerActionButtonComponent,
    ResourceSheetComponent,
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
      listEmptyMessage="Aucune ressource. Ajoutez des polices pour les partager avec tous les nœuds."
      openLabel="Ouvrir"
      (open)="openResource($event)"
      [catalogState]="catalogState()"
      [catalogMessage]="catalogMessage()"
      [actionOpen]="editorMode() !== null"
      [actionTitle]="editorMode() === 'version' ? 'Nouvelle version' : 'Nouvelle ressource'"
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
      </ng-template>

      <ng-template #detailContent catalogue-detail>
        @if (selected(); as details) {
          <app-resource-sheet
            ariaLabel="Versions et fichiers de la ressource"
            [title]="details.summary.name"
            [subtitle]="'Polices · version ' + details.summary.latestVersion"
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
        <form class="resource-form" [formGroup]="form" (ngSubmit)="save()">
          @if (editorMode() === 'create') {
            <mat-form-field appearance="outline" subscriptSizing="dynamic">
              <mat-label>Nom de la ressource</mat-label>
              <input matInput formControlName="name" maxlength="128" [readonly]="busy()" />
            </mat-form-field>
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
            multiple
            accept=".ttf,.otf,.ttc"
            (change)="pick(picker)"
          />
          <button matButton="outlined" type="button" [disabled]="busy()" (click)="picker.click()">
            Choisir des polices (.ttf, .otf, .ttc)
          </button>
          <ul class="chosen" aria-label="Fichiers choisis">
            @for (file of files(); track file.name) {
              <li>
                <span>{{ file.name }}</span>
                <span class="size">{{ size(file.size) }}</span>
                <button
                  matButton="text"
                  type="button"
                  [disabled]="busy()"
                  [attr.aria-label]="'Retirer ' + file.name"
                  (click)="removeFile(file)"
                >
                  Retirer
                </button>
              </li>
            }
          </ul>
          <p class="hint">
            20 Mo au plus par fichier, 100 Mo par version. Chaque version est conservée : un flow
            garde la version qu’il utilise.
          </p>
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
                    : 'Créer'
              }}
            </button>
          </div>
        </form>
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
    name: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    description: new FormControl('', { nonNullable: true }),
  });
  protected readonly editorDescription = computed(() =>
    this.editorMode() === 'version'
      ? 'La nouvelle version remplace la précédente pour les prochains flows ; les flows existants gardent la leur.'
      : 'Les polices sont envoyées au serveur puis copiées automatiquement sur les nœuds.',
  );
  protected readonly canSave = computed(
    () =>
      !this.busy() &&
      this.files().length > 0 &&
      (this.editorMode() === 'version' || this.form.controls.name.value.trim().length > 0),
  );
  protected readonly items = computed<readonly ResourceListItem[]>(() => {
    const query = this.query().trim().toLocaleLowerCase();
    return this.resources()
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
          { label: 'Polices' },
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
        caption: 'Partagée par tout le cluster',
        fields: [
          { label: 'Nom', value: summary.name },
          { label: 'Description', value: summary.description || 'Aucune description.' },
          { label: 'Identifiant', value: summary.id, mono: true },
          {
            label: 'Mise à jour',
            value: new Date(summary.updatedAt).toLocaleString('fr-FR'),
          },
        ],
      },
      ...details.versions.map((version) => ({
        id: `version-${version.version}`,
        title: `Version ${version.version}`,
        caption:
          (version.version === summary.latestVersion ? 'Dernière · ' : '') +
          `${formatResourceSize(version.totalBytes)} · ${new Date(version.createdAt).toLocaleString('fr-FR')}`,
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
            label: 'Ajouter des polices',
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

  protected size(bytes: number): string {
    return formatResourceSize(bytes);
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

  protected openCreate(): void {
    this.form.reset({ name: '', description: '' });
    this.files.set([]);
    this.error.set('');
    this.editorMode.set('create');
  }

  protected openVersion(): void {
    this.files.set([]);
    this.error.set('');
    this.editorMode.set('version');
  }

  protected closeEditor(): void {
    if (this.busy()) return;
    this.editorMode.set(null);
  }

  protected pick(input: HTMLInputElement): void {
    const picked = Array.from(input.files ?? []);
    this.files.update((current) => [
      ...current,
      ...picked.filter((file) => !current.some((item) => item.name === file.name)),
    ]);
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
      const uploaded = [];
      for (const file of this.files()) uploaded.push(await this.api.uploadFile(file));
      const references = uploaded.map(({ name, sha256 }) => ({ name, sha256 }));
      if (this.editorMode() === 'version' && this.selected()) {
        const id = this.selected()!.summary.id;
        await this.api.addVersion(id, references);
        this.editorMode.set(null);
        await this.load();
        await this.loadDetails(id);
        this.notifications.show('Nouvelle version publiée.');
      } else {
        const created = await this.api.create(
          this.form.controls.name.value.trim(),
          this.form.controls.description.value.trim(),
          references,
        );
        this.editorMode.set(null);
        await this.load();
        this.openResource(created.summary.id);
        this.notifications.show('Ressource créée. Les nœuds la récupèrent automatiquement.');
      }
    } catch (error) {
      this.error.set(this.message(error, 'L’envoi a échoué.'));
    } finally {
      this.busy.set(false);
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
