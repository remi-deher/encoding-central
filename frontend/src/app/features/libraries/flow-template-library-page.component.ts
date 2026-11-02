import { ResourceListItem } from '../../shared/ui/resource-list.component';
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
import { ActivatedRoute, Router } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { firstValueFrom, map } from 'rxjs';
import { DataTableRow } from '../../shared/ui/data-table.component';
import {
  ResourceSheetComponent,
  ResourceSheetSection,
} from '../../shared/ui/resource-sheet.component';
import { DrawerActionButtonComponent } from '../../shared/ui/drawer-action-button/drawer-action-button.component';
import { ResourceCatalogTemplateComponent } from '../../shared/templates/resource-catalog-template.component';
import { LibrarySectionTabsComponent } from './library-section-tabs.component';
import { RecordFormComponent, RecordFormData } from '../../shared/ui/record-form.component';
import { FlowTemplates } from '../../shared/services/flow-templates';
import { FlowTemplateSummary } from '../../shared/models/flow-templates';
import { MEDIA_TYPE_ICONS, MEDIA_TYPE_OPTIONS } from '../../shared/models/media-type';
import { AuthService } from '../../core/auth/auth.service';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { ComponentConfigurationFormComponent } from '../../shared/ui/component-configuration-form.component';
import { FlowPreviewComponent } from '../../shared/ui/flow-preview/flow-preview';
import { FlowValidation } from '../../shared/services/flow-validation';
import { NotificationService } from '../../shared/services/notification.service';
import {
  PageShellContextService,
  PageShellSearchResult,
} from '../../core/layout/page-shell-context';
import {
  FlowComponentConfiguration,
  FlowComponentConfigurationSchema,
  FlowComponentDefinition,
} from '../../shared/models/flow-component';
import {
  isTemplateConfigurableField,
  FlowTemplateDetails,
} from '../../shared/models/flow-templates';

interface FlowTemplateRow extends DataTableRow {
  readonly id: string;
  readonly name: string;
  readonly mediaType: string;
  readonly mediaTypeValue: FlowTemplateSummary['mediaType'];
  readonly source: string;
  readonly sourceVersion: string;
  readonly components: string;
  readonly description: string;
}

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-flow-template-library-page',
  imports: [
    ResourceCatalogTemplateComponent,
    LibrarySectionTabsComponent,
    DrawerActionButtonComponent,
    RecordFormComponent,
    ResourceSheetComponent,
    MatButtonModule,
    MatCheckboxModule,
    ComponentConfigurationFormComponent,
    FlowPreviewComponent,
  ],
  template: `
    <app-resource-catalog-template
      [title]="title()"
      [items]="items()"
      [rowActions]="rowActions"
      filterLabel="Modèles de flow"
      searchLabel=""
      [showToolbar]="false"
      resourceIcon="copy"
      listEmptyMessage="Aucun modèle. Depuis Flows, ouvrez le menu d’un flow publié puis choisissez Créer un modèle."
      openLabel="Ouvrir"
      (open)="openTemplate({ id: $event })"
      (action)="onRowAction($event)"
      [catalogState]="catalogState()"
      [catalogMessage]="catalogMessage()"
      [actionOpen]="actionOpen()"
      [actionTitle]="actionForm().title"
      [actionDescription]="actionForm().description"
      [detailOpen]="selectedTemplate() !== null"
      [detailTitle]="selectedTemplate()?.name ?? 'Modèle'"
      [detailDescription]="selectedTemplate()?.sourceFlowName ?? ''"
      detailLayout="drawer"
      (detailClosed)="closeTemplateDetail()"
      (actionClosed)="closeCreateFlow()"
    >
      <ng-template #catalogueNavigation>
        <app-library-section-tabs catalogue-navigation selected="templates" />
      </ng-template>

      <ng-template #detailContent catalogue-detail>
        @if (selectedTemplate(); as template) {
          <app-resource-sheet
            ariaLabel="Résumé et origine du modèle"
            [title]="template.name"
            [subtitle]="templateSubtitle()"
            [sections]="templateSections()"
          />
        }
      </ng-template>

      <ng-template #detailHeaderActions>
        @if (selectedTemplate(); as template) {
          <app-drawer-action-button
            label="Créer un flow depuis ce modèle"
            (activated)="openCreateFlow(template)"
          />
          @if (auth.isAdmin()) {
            <app-drawer-action-button
              label="Modifier le modèle"
              (activated)="openEditTemplate(template)"
            />
          }
        }
      </ng-template>

      <ng-template #actionContent>
        @if (templateEditing()) {
          @if (!templateEditReady()) {
            <app-record-form
              [data]="templateEditForm()"
              (submitted)="continueTemplateEdit($event)"
              (cancelled)="closeCreateFlow()"
            />
          } @else if (templateDetails(); as details) {
            <section
              class="template-deployment-setup"
              aria-label="Modifier la configuration du modèle"
            >
              <p>
                Les valeurs saisies deviennent les valeurs par défaut. Les paramètres cochés seront
                proposés lors des prochains flows créés depuis ce modèle.
              </p>
              @for (block of templateEditBlocks(); track block.nodeId) {
                <section class="template-setup-block">
                  <h3>{{ block.name }}</h3>
                  @for (field of block.fields; track field.id) {
                    <mat-checkbox
                      [checked]="templateEditFieldSelected(block.nodeId, field.id)"
                      (change)="toggleTemplateEditField(block.nodeId, field.id, $event.checked)"
                      >{{ field.label }}</mat-checkbox
                    >
                  }
                  <app-component-configuration-form
                    [schema]="block.schema"
                    [values]="block.values"
                    [showActions]="false"
                    (configurationChange)="saveTemplateDefaults(block.nodeId, $event)"
                  />
                </section>
              }
              <div class="template-setup-actions">
                <button matButton="text" type="button" (click)="closeCreateFlow()">Annuler</button>
                <button
                  matButton="filled"
                  type="button"
                  [disabled]="setupBusy()"
                  (click)="saveTemplate()"
                >
                  {{ setupBusy() ? 'Enregistrement…' : 'Enregistrer le modèle' }}
                </button>
              </div>
            </section>
          }
        } @else if (setupLoading()) {
          <p role="status">Chargement des paramètres du modèle…</p>
        } @else if (!setupStarted()) {
          <app-record-form
            [data]="actionForm()"
            (submitted)="continueToFlow($event)"
            (cancelled)="closeCreateFlow()"
          />
        } @else if (templateDetails(); as details) {
          <section class="template-deployment-setup" aria-label="Configuration du modèle">
            <p>
              Les paramètres sont préremplis avec les valeurs du modèle. Vous pouvez ajuster
              uniquement ceux qu’il expose. Les chemins et fichiers locaux seront à renseigner dans
              l’éditeur du flow.
            </p>
            <section class="template-setup-block" aria-label="Flow qui sera créé">
              <h3>Flow qui sera créé</h3>
              <p>
                « {{ setupName() }} » partira du modèle « {{ details.template.name }} » (révision
                {{ details.template.revision }}). Vérifiez son contenu avant de créer le brouillon.
              </p>
              <app-flow-preview
                [flow]="details.definition"
                [components]="setupComponents()"
                mode="flow"
              />
            </section>
            @for (block of setupBlocks(); track block.nodeId) {
              <section class="template-setup-block">
                <h3>{{ block.name }}</h3>
                <app-component-configuration-form
                  [schema]="block.schema"
                  [values]="block.values"
                  [showActions]="false"
                  (configurationChange)="saveSetupValues(block.nodeId, $event)"
                />
              </section>
            }
            <div class="template-setup-actions">
              <button matButton="text" type="button" (click)="setupStarted.set(false)">
                Retour
              </button>
              <button
                matButton="filled"
                type="button"
                [disabled]="setupBusy()"
                (click)="createConfiguredFlow()"
              >
                {{ setupBusy() ? 'Création…' : 'Ouvrir le flow' }}
              </button>
            </div>
          </section>
        }
      </ng-template>
    </app-resource-catalog-template>
  `,
  styles: [
    `
      .template-deployment-setup {
        display: grid;
        gap: var(--space-4);
      }
      .template-deployment-setup > p {
        margin: 0;
        color: var(--text-secondary);
      }
      .template-setup-block {
        display: grid;
        gap: var(--space-3);
        padding: var(--space-4);
        border-radius: var(--radius-lg);
        background: var(--surface-container-low);
      }
      .template-setup-block h3 {
        margin: 0;
      }
      .template-setup-actions {
        display: flex;
        justify-content: flex-end;
        gap: var(--space-2);
      }
    `,
  ],
})
export class FlowTemplateLibraryPageComponent {
  protected readonly auth = inject(AuthService);
  private readonly api = inject(FlowTemplates);
  private readonly destroyRef = inject(DestroyRef);
  private readonly pageShellContext = inject(PageShellContextService);
  private readonly flowValidation = inject(FlowValidation);
  private readonly notifications = inject(NotificationService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly routeData = toSignal(this.route.data, {
    initialValue: this.route.snapshot.data,
  });
  protected readonly title = computed(
    () => (this.routeData()['title'] as string) ?? 'Modèles de flow',
  );
  protected readonly catalogState = signal<'ready' | 'loading' | 'error'>('loading');
  protected readonly catalogMessage = signal('Chargement des modèles…');
  protected readonly templates = signal<readonly FlowTemplateSummary[]>([]);
  protected readonly selectedTemplate = signal<FlowTemplateSummary | null>(null);
  protected readonly templateForCreation = signal<FlowTemplateSummary | null>(null);
  protected readonly templateDetails = signal<FlowTemplateDetails | null>(null);
  protected readonly setupComponents = signal<readonly FlowComponentDefinition[]>([]);
  protected readonly setupStarted = signal(false);
  protected readonly setupLoading = signal(false);
  protected readonly setupBusy = signal(false);
  protected readonly setupName = signal('');
  protected readonly setupValues = signal<Readonly<Record<string, FlowComponentConfiguration>>>({});
  protected readonly templateEditing = signal(false);
  protected readonly templateEditReady = signal(false);
  protected readonly templateEditForm = computed<RecordFormData>(() => {
    const template = this.templateDetails()?.template;
    return {
      title: 'Modifier un modèle',
      description:
        'Le modèle détermine les valeurs par défaut et les paramètres proposés aux prochains flows.',
      submitLabel: 'Configurer le modèle',
      fields: [
        { id: 'name', label: 'Nom du modèle', type: 'text', required: true, maxLength: 128 },
        { id: 'description', label: 'Description', type: 'text', maxLength: 1000 },
      ],
      initialValues: { name: template?.name ?? '', description: template?.description ?? '' },
    };
  });
  protected readonly templateEditMeta = signal({ name: '', description: '' });
  protected readonly templateEditSelection = signal<Readonly<Record<string, readonly string[]>>>(
    {},
  );
  protected readonly templateEditValues = signal<
    Readonly<Record<string, FlowComponentConfiguration>>
  >({});
  protected readonly templateEditBlocks = computed(() => {
    const details = this.templateDetails();
    if (!details) return [];
    return details.definition.nodes.flatMap((node) => {
      const component = this.setupComponents().find(
        (candidate) =>
          candidate.id === node.component.componentId &&
          candidate.pluginId === node.component.pluginId,
      );
      const fields =
        component?.configurationSchema?.fields.filter(isTemplateConfigurableField) ?? [];
      const schema = component?.configurationSchema;
      return fields.length && schema
        ? [
            {
              nodeId: node.id,
              name: node.name || component.name,
              fields,
              schema: { ...schema, fields },
              values: node.configuration,
            },
          ]
        : [];
    });
  });
  protected readonly setupBlocks = computed(() => {
    const details = this.templateDetails();
    if (!details) return [];
    return details.configuration.nodes.flatMap((configured) => {
      const node = details.definition.nodes.find((candidate) => candidate.id === configured.nodeId);
      const component =
        node &&
        this.setupComponents().find(
          (candidate) =>
            candidate.id === node.component.componentId &&
            candidate.pluginId === node.component.pluginId,
        );
      const schema = component?.configurationSchema;
      const fields = schema?.fields.filter((field) => configured.fieldIds.includes(field.id)) ?? [];
      if (!node || !component || !schema || fields.length === 0) return [];
      return [
        {
          nodeId: node.id,
          name: node.name || component.name,
          schema: { ...schema, fields } as FlowComponentConfigurationSchema,
          values: node.configuration,
        },
      ];
    });
  });
  protected readonly actionOpen = signal(false);
  protected readonly actionForm = computed<RecordFormData>(() => {
    if (this.templateEditing()) return this.templateEditForm();
    const template = this.templateForCreation();
    return {
      title: 'Nom du nouveau flow',
      description: template
        ? `Il partira du modèle « ${template.name} » et sera enregistré comme un flow distinct.`
        : 'Choisissez le nom de ce nouveau flow.',
      submitLabel: 'Ouvrir l’éditeur',
      initialValues: { name: template ? `${template.name} — Copie` : '' },
      fields: [{ id: 'name', label: 'Nom du flow', type: 'text', required: true, maxLength: 128 }],
    };
  });
  protected readonly query = signal('');
  protected readonly rowActions = [{ id: 'create-flow', label: 'Créer un flow' }];
  protected readonly rows = computed<readonly FlowTemplateRow[]>(() =>
    this.templates().map((template) => ({
      id: template.id,
      name: template.name,
      mediaType:
        MEDIA_TYPE_OPTIONS.find((media) => media.value === template.mediaType)?.label ??
        template.mediaType,
      mediaTypeValue: template.mediaType,
      source: template.sourceFlowName,
      sourceVersion: `v${template.sourceVersion}`,
      components: String(template.nodeCount),
      description: template.description,
    })),
  );
  protected readonly filteredRows = computed(() => {
    const query = this.query().trim().toLocaleLowerCase();
    return query
      ? this.rows().filter(
          (row) =>
            row.name.toLocaleLowerCase().includes(query) ||
            row.source.toLocaleLowerCase().includes(query) ||
            row.description.toLocaleLowerCase().includes(query),
        )
      : this.rows();
  });
  protected readonly items = computed<readonly ResourceListItem[]>(() =>
    this.filteredRows().map((row) => ({
      id: row.id,
      name: row.name,
      icon: MEDIA_TYPE_ICONS[row.mediaTypeValue],
      subtitle: row.description || undefined,
      tags: [
        { label: row.mediaType },
        { label: `${row.components} composant(s)` },
        {
          label: `${this.templates().find((template) => template.id === row.id)?.configurableFieldCount ?? 0} paramètre(s)`,
        },
        { label: `${row.source} · ${row.sourceVersion}`, tone: 'ok' as const },
      ],
    })),
  );
  protected readonly templateSubtitle = computed(() => {
    const template = this.selectedTemplate();
    if (!template) return '';
    return (
      MEDIA_TYPE_OPTIONS.find((media) => media.value === template.mediaType)?.label ??
      template.mediaType
    );
  });
  protected readonly templateSections = computed<readonly ResourceSheetSection[]>(() => {
    const template = this.selectedTemplate();
    if (!template) return [];
    const mediaType =
      MEDIA_TYPE_OPTIONS.find((media) => media.value === template.mediaType)?.label ??
      template.mediaType;
    const createdAt = new Date(template.createdAt).toLocaleString('fr-FR');

    return [
      {
        id: 'template-summary',
        title: 'Modèle',
        caption: mediaType,
        items: [
          {
            label: template.name,
            detail: template.description || 'Aucune description.',
            badges: [
              { label: `Révision ${template.revision}`, tone: 'accent' },
              { label: `${template.nodeCount} composant(s)` },
              { label: `${template.configurableFieldCount} paramètre(s) proposés` },
              { label: `Créé le ${createdAt}`, tone: 'neutral' },
            ],
          },
        ],
      },
      {
        id: 'template-origin',
        title: 'Flow source',
        caption: `Version publiée v${template.sourceVersion}`,
        fields: [
          { label: 'Flow', value: template.sourceFlowName },
          { label: 'Identifiant', value: template.sourceFlowId, mono: true },
          { label: 'Version', value: `v${template.sourceVersion}` },
        ],
      },
    ];
  });

  private readonly itemParam = toSignal(
    this.route.queryParamMap.pipe(map((params) => params.get('template'))),
    { initialValue: this.route.snapshot.queryParamMap.get('template') },
  );
  private readonly configureParam = toSignal(
    this.route.queryParamMap.pipe(map((params) => params.get('configure') === 'true')),
    { initialValue: this.route.snapshot.queryParamMap.get('configure') === 'true' },
  );
  private readonly setupNameParam = toSignal(
    this.route.queryParamMap.pipe(map((params) => params.get('name') ?? '')),
    { initialValue: this.route.snapshot.queryParamMap.get('name') ?? '' },
  );
  private readonly setupRequestHandled = signal<string | null>(null);

  constructor() {
    this.destroyRef.onDestroy(
      this.pageShellContext.register({
        searchPlaceholder: 'Rechercher dans les modèles de flow…',
        query: this.query,
        onSearchQueryChange: (query) => this.query.set(query),
        searchResults: (query) => this.templateSearchResults(query),
      }),
    );
    void this.loadTemplates();
    // La fiche est portée par l'URL (?template=<id>) : le menu latéral et le bouton précédent
    // du navigateur ramènent à la liste.
    effect(() => {
      const id = this.itemParam();
      const items = this.templates();
      const configure = this.configureParam();
      untracked(() => {
        if (!id) {
          if (this.selectedTemplate()) this.selectedTemplate.set(null);
          this.setupRequestHandled.set(null);
          return;
        }
        const item = items.find((candidate) => candidate.id === id);
        if (item && this.selectedTemplate()?.id !== id) this.selectedTemplate.set(item);
        if (!configure) {
          this.setupRequestHandled.set(null);
          return;
        }
        if (item && this.setupRequestHandled() !== id) {
          this.setupRequestHandled.set(id);
          this.openCreateFlow(item, this.setupNameParam() || `${item.name} — Copie`);
        }
      });
    });
  }

  protected openTemplate(row: DataTableRow): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { template: String(row['id']) },
    });
  }

  private templateSearchResults(query: string): readonly PageShellSearchResult[] {
    const normalized = query.trim().toLocaleLowerCase('fr-FR');
    if (!normalized) return [];
    return this.templates()
      .filter((template) =>
        [template.name, template.description, template.sourceFlowName]
          .join(' ')
          .toLocaleLowerCase('fr-FR')
          .includes(normalized),
      )
      .slice(0, 8)
      .map((template) => ({
        id: `flow-template:${template.id}`,
        label: template.name,
        description: `${template.sourceFlowName} · ${template.nodeCount} composant(s)`,
        group: 'Modèles de flow',
        glyph: '◫',
        run: () => this.openTemplate({ id: template.id }),
      }));
  }

  protected closeTemplateDetail(): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { template: null },
      queryParamsHandling: 'merge',
    });
  }

  protected onRowAction(event: { readonly actionId: string; readonly row: DataTableRow }): void {
    if (event.actionId !== 'create-flow') return;
    const template = this.templates().find((candidate) => candidate.id === event.row['id']);
    if (template) this.openCreateFlow(template);
  }

  protected openCreateFlow(template: FlowTemplateSummary, initialName?: string): void {
    this.templateForCreation.set(template);
    this.templateEditing.set(false);
    this.actionOpen.set(true);
    if (initialName) void this.loadSetup(template, initialName);
  }

  protected async openEditTemplate(template: FlowTemplateSummary): Promise<void> {
    try {
      const [details, plugins] = await Promise.all([
        this.api.get(template.id),
        this.flowValidation.catalog(),
      ]);
      this.templateForCreation.set(null);
      this.templateDetails.set(details);
      this.setupComponents.set(
        plugins.flatMap((plugin) =>
          plugin.components.map((component) => ({
            ...component,
            pluginId: plugin.id,
            pluginName: plugin.name,
          })),
        ),
      );
      this.templateEditSelection.set(
        Object.fromEntries(details.configuration.nodes.map((node) => [node.nodeId, node.fieldIds])),
      );
      this.templateEditValues.set({});
      this.templateEditReady.set(false);
      this.templateEditing.set(true);
      this.actionOpen.set(true);
    } catch (error) {
      this.notifications.show(
        error instanceof HttpErrorResponse
          ? (error.error?.title ?? 'Impossible de charger ce modèle.')
          : 'Impossible de charger ce modèle.',
      );
    }
  }

  protected continueTemplateEdit(values: Record<string, string>): void {
    this.templateEditMeta.set({ name: values['name'], description: values['description'] ?? '' });
    this.templateEditReady.set(true);
  }

  protected templateEditFieldSelected(nodeId: string, fieldId: string): boolean {
    return this.templateEditSelection()[nodeId]?.includes(fieldId) ?? false;
  }

  protected toggleTemplateEditField(nodeId: string, fieldId: string, selected: boolean): void {
    const selection = this.templateEditSelection();
    const fields = new Set(selection[nodeId] ?? []);
    if (selected) fields.add(fieldId);
    else fields.delete(fieldId);
    this.templateEditSelection.set({ ...selection, [nodeId]: [...fields] });
  }

  protected saveTemplateDefaults(nodeId: string, values: FlowComponentConfiguration): void {
    this.templateEditValues.set({ ...this.templateEditValues(), [nodeId]: values });
  }

  protected async saveTemplate(): Promise<void> {
    const details = this.templateDetails();
    if (!details || this.setupBusy()) return;
    this.setupBusy.set(true);
    try {
      const definition = {
        ...details.definition,
        name: this.templateEditMeta().name,
        nodes: details.definition.nodes.map((node) => ({
          ...node,
          configuration: this.templateEditValues()[node.id] ?? node.configuration,
        })),
      };
      const updated = await this.api.update(details.template.id, {
        expectedRevision: details.template.revision,
        name: this.templateEditMeta().name,
        description: this.templateEditMeta().description,
        definition,
        configuration: {
          nodes: Object.entries(this.templateEditSelection())
            .filter(([, fieldIds]) => fieldIds.length > 0)
            .map(([nodeId, fieldIds]) => ({ nodeId, fieldIds })),
        },
      });
      this.templates.update((items) =>
        items.map((item) => (item.id === updated.template.id ? updated.template : item)),
      );
      this.selectedTemplate.set(updated.template);
      this.notifications.show('Modèle mis à jour pour les prochains flows.');
      this.closeCreateFlow();
    } catch (error) {
      this.notifications.show(
        error instanceof HttpErrorResponse
          ? (error.error?.title ?? 'Impossible d’enregistrer les modifications du modèle.')
          : 'Impossible d’enregistrer les modifications du modèle.',
      );
    } finally {
      this.setupBusy.set(false);
    }
  }

  protected closeCreateFlow(): void {
    this.actionOpen.set(false);
    this.templateForCreation.set(null);
    this.templateDetails.set(null);
    this.setupComponents.set([]);
    this.setupStarted.set(false);
    this.setupLoading.set(false);
    this.setupValues.set({});
    this.templateEditing.set(false);
    this.templateEditReady.set(false);
    this.templateEditValues.set({});
  }

  protected async continueToFlow(values: Record<string, string>): Promise<void> {
    const template = this.templateForCreation();
    if (!template) return;
    await this.loadSetup(template, values['name']);
  }

  private async loadSetup(template: FlowTemplateSummary, name: string): Promise<void> {
    this.setupLoading.set(true);
    try {
      const [details, plugins] = await Promise.all([
        this.api.get(template.id),
        this.flowValidation.catalog(),
      ]);
      this.templateDetails.set(details);
      this.templateForCreation.set(details.template);
      this.templates.update((items) =>
        items.map((item) => (item.id === details.template.id ? details.template : item)),
      );
      this.setupComponents.set(
        plugins.flatMap((plugin) =>
          plugin.components.map((component) => ({
            ...component,
            pluginId: plugin.id,
            pluginName: plugin.name,
          })),
        ),
      );
      this.setupValues.set({});
      this.setupName.set(name);
      this.setupStarted.set(true);
    } catch (error) {
      this.notifications.show(
        error instanceof HttpErrorResponse
          ? (error.error?.title ?? 'Impossible de charger les paramètres du modèle.')
          : 'Impossible de charger les paramètres du modèle.',
      );
    } finally {
      this.setupLoading.set(false);
    }
  }

  protected saveSetupValues(nodeId: string, values: FlowComponentConfiguration): void {
    this.setupValues.set({ ...this.setupValues(), [nodeId]: values });
  }

  protected async createConfiguredFlow(): Promise<void> {
    const template = this.templateForCreation();
    if (!template || this.setupBusy()) return;
    this.setupBusy.set(true);
    try {
      const result = await this.api.createFlow(
        template.id,
        this.setupName(),
        template.revision,
        Object.entries(this.setupValues()).flatMap(([nodeId, values]) => {
          const fields =
            this.templateDetails()?.configuration.nodes.find((node) => node.nodeId === nodeId)
              ?.fieldIds ?? [];
          if (!fields.length) return [];
          return [
            {
              nodeId,
              values: Object.fromEntries(
                fields.filter((field) => field in values).map((field) => [field, values[field]]),
              ),
            },
          ];
        }),
      );
      await this.router.navigate(['/flows', result.definition.id, 'edit'], {
        info: { flowEditorDirection: 'forward' },
      });
      this.notifications.show('Brouillon créé à partir du modèle.');
      this.closeCreateFlow();
    } catch (error) {
      this.notifications.show(
        error instanceof HttpErrorResponse
          ? (error.error?.title ?? 'Impossible de créer le flow depuis ce modèle.')
          : 'Impossible de créer le flow depuis ce modèle.',
      );
    } finally {
      this.setupBusy.set(false);
    }
  }

  private async loadTemplates(): Promise<void> {
    try {
      this.templates.set(await firstValueFrom(this.api.list()));
      this.catalogState.set('ready');
      this.catalogMessage.set('');
    } catch (error) {
      this.catalogState.set('error');
      this.catalogMessage.set(
        error instanceof HttpErrorResponse
          ? (error.error?.title ?? 'Les modèles ne sont pas disponibles.')
          : 'Les modèles ne sont pas disponibles.',
      );
    }
  }
}
