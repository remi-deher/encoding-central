import { StatusBadgeComponent } from '../../shared/ui/status-badge.component';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  signal,
} from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { MatTabsModule } from '@angular/material/tabs';
import { ClusterMaintenanceTemplateComponent } from '../../shared/templates/cluster-maintenance-template.component';
import { CategoryTabOption, CategoryTabsComponent } from '../../shared/ui/category-tabs.component';
import { ComponentConfigurationFormComponent } from '../../shared/ui/component-configuration-form.component';
import { FlowValidation } from '../../shared/services/flow-validation';
import { PluginConfigurationApi } from '../../shared/services/plugin-configuration';
import { NotificationService } from '../../shared/services/notification.service';
import {
  FlowComponentConfiguration,
  FlowComponentConfigurationSchema,
} from '../../shared/models/flow-component';
import {
  FlowPluginCatalog,
  PluginConfigurationResponse,
} from '../../shared/models/flow-validation';
import { FlowComponentDefinition } from '../../shared/models/flow-component';
import { FlowComponentListComponent } from '../../shared/ui/flow-component-list.component';
import { PlexSetupAssistantComponent } from './plex-setup-assistant.component';
import {
  ResourceSheetComponent,
  ResourceSheetSection,
} from '../../shared/ui/resource-sheet.component';

import { MatButtonModule } from '@angular/material/button';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { ClusterExtensionsService } from '../../shared/services/cluster-extensions.service';
import {
  ClusterTool,
  ExtensionTab,
  RejectedPlugin,
  StorePackage,
  StoreRepository,
} from '../../shared/models/cluster-extensions';
import {
  PageShellContextService,
  PageShellSearchResult,
} from '../../core/layout/page-shell-context';

type PluginFamily = 'base' | 'cluster' | 'media' | 'automation' | 'custom';

interface PluginRecord {
  readonly id: string;
  readonly name: string;
  readonly family: PluginFamily;
  readonly familyLabel: string;
  readonly category: string;
  readonly version: string;
  readonly components: string;
  readonly runtime: string;
  readonly configuration: string;
  readonly state: string;
  readonly iconUrl: string;
  readonly enabled: boolean;
  readonly canDisable: boolean;
}

type StaticPluginRecord = Omit<PluginRecord, 'enabled' | 'canDisable'>;

const PLUGIN_RECORDS: readonly StaticPluginRecord[] = [
  {
    id: 'base',
    name: 'Base',
    family: 'base',
    familyLabel: 'Noyau',
    category: 'Composants de flow',
    version: 'Intégré',
    components:
      'Composants généraux, attente durable (délai, état d’un worker, charge, condition de plugin)',
    runtime: 'Fourni par le serveur',
    configuration: 'Aucune connexion externe requise',
    state: 'Actif',
    iconUrl: '/plugins/base.svg',
  },
  {
    id: 'nodes',
    name: 'Nœuds',
    family: 'cluster',
    familyLabel: 'Cluster',
    category: 'Orchestration des workers',
    version: '1.1.0',
    components: 'Transférer la suite, choisir un nœud ou exiger une capacité',
    runtime: 'Intégré au répartiteur et au worker',
    configuration: 'Cible par défaut, avec dérogations par flow et par bloc',
    state: 'Actif par défaut',
    iconUrl: '/plugins/nodes.svg',
  },
  {
    id: 'conditions',
    name: 'Conditions',
    family: 'base',
    familyLabel: 'Noyau',
    category: 'Aiguillage des flows',
    version: '1.0.0',
    components: 'Règles et groupes ET / OU imbriqués',
    runtime: 'Évaluation native sur les workers',
    configuration: 'Conditions définies sur chaque bloc',
    state: 'Actif par défaut',
    iconUrl: '/plugins/conditions.svg',
  },
  {
    id: 'ai',
    name: 'Intelligence Artificielle',
    family: 'base',
    familyLabel: 'Noyau',
    category: 'Génération & Traduction LLM',
    version: '1.0.0',
    components: 'Traduction de sous-titres, enrichissement des métadonnées, résumé de contenu',
    runtime: 'Intégré (CLI / Ollama / OpenAI)',
    configuration:
      'Fournisseur actif, URL Ollama, clés API et modèles configurables dans Paramètres',
    state: 'Actif par défaut',
    iconUrl: '/plugins/ai.svg',
  },
  {
    id: 'video',
    name: 'Vidéo',
    family: 'media',
    familyLabel: 'Média',
    category: 'Traitement vidéo',
    version: '1.0.0',
    components:
      'Encodage HEVC/AV1/H.264, redimensionnement, recadrage automatique, désentrelacement, protection de taille',
    runtime: 'FFmpeg (encodeurs CPU / GPU)',
    configuration: 'Profils d’encodage et options par bloc',
    state: 'Actif par défaut',
    iconUrl: '/plugins/video.svg',
  },
  {
    id: 'audio',
    name: 'Audio',
    family: 'media',
    familyLabel: 'Média',
    category: 'Traitement audio',
    version: '1.0.0',
    components:
      'Encodage AAC/AC-3/E-AC-3/FLAC, normalisation R128/crête, règles de mixage 5.1/stéréo, filtrage des langues, suppression des commentaires',
    runtime: 'FFmpeg',
    configuration: 'Règles et options par bloc',
    state: 'Actif par défaut',
    iconUrl: '/plugins/audio.svg',
  },
  {
    id: 'subtitles',
    name: 'Sous-titres',
    family: 'media',
    familyLabel: 'Média',
    category: 'Gestion des sous-titres',
    version: '1.0.0',
    components:
      'Gestion SRT/ASS, polices intégrées, normalisation des noms de pistes MKV, extraction et synchronisation',
    runtime: 'FFmpeg & MKVToolNix',
    configuration: 'Options par bloc',
    state: 'Actif par défaut',
    iconUrl: '/plugins/subtitles.svg',
  },
  {
    id: 'mkv',
    name: 'Matroska & MKV',
    family: 'media',
    familyLabel: 'Média',
    category: 'Conteneur MKV',
    version: '1.0.0',
    components: 'Remux MKV, pistes audio/sous-titres, métadonnées, chapitres, pièces jointes',
    runtime: 'MKVToolNix (mkvmerge, mkvextract, mkvpropedit)',
    configuration: 'Options par bloc',
    state: 'Actif par défaut',
    iconUrl: '/plugins/mkv.svg',
  },
  {
    id: 'images',
    name: 'Images',
    family: 'media',
    familyLabel: 'Média',
    category: 'Traitement graphique',
    version: '1.0.0',
    components: 'Conversion de format, redimensionnement et recadrage d’images',
    runtime: 'ImageMagick (magick / convert)',
    configuration: 'Options par bloc',
    state: 'Actif par défaut',
    iconUrl: '/plugins/images.svg',
  },
  {
    id: 'probe',
    name: 'Sondes & Analyse',
    family: 'media',
    familyLabel: 'Média',
    category: 'Inspection multimédia',
    version: '1.0.0',
    components:
      'Sondes FFprobe (flux, vidéo, audio) et inspection MediaInfo (HDR, pistes, rapport JSON)',
    runtime: 'FFprobe & MediaInfo',
    configuration: 'Options par bloc',
    state: 'Actif par défaut',
    iconUrl: '/plugins/probe.svg',
  },
  {
    id: 'plex',
    name: 'Plex',
    family: 'media',
    familyLabel: 'Média',
    category: 'Serveur multimédia',
    version: '1.0.0',
    components: 'Scan, attente et vérification de présence',
    runtime: 'Processus plugin livré avec l’image worker (déclaré dans Runtime:Plugins)',
    configuration:
      'Assistant : connexion Plex, choix du serveur et correspondance des bibliothèques',
    state: 'Catalogue intégré',
    iconUrl: '/plugins/plex.svg',
  },
  {
    id: 'jellyfin',
    name: 'Jellyfin',
    family: 'media',
    familyLabel: 'Média',
    category: 'Serveur multimédia',
    version: '1.0.0',
    components: 'Actualisation et vérification des éléments',
    runtime: 'Processus plugin livré avec l’image worker (déclaré dans Runtime:Plugins)',
    configuration: 'Connexion, correspondance des chemins et test de connexion',
    state: 'Catalogue intégré',
    iconUrl: '/plugins/jellyfin.svg',
  },
  {
    id: 'sonarr',
    name: 'Sonarr',
    family: 'automation',
    familyLabel: 'Automatisation',
    category: 'Intégration *ARR',
    version: '1.0.0',
    components: 'Actualisation, vérification et recherche d’épisodes',
    runtime: 'Processus plugin livré avec l’image worker (déclaré dans Runtime:Plugins)',
    configuration:
      'Connexion, correspondance des chemins, déclencheur d’import (webhook) et test de connexion',
    state: 'Catalogue intégré',
    iconUrl: '/plugins/sonarr.svg',
  },
  {
    id: 'radarr',
    name: 'Radarr',
    family: 'automation',
    familyLabel: 'Automatisation',
    category: 'Intégration *ARR',
    version: '1.0.0',
    components: 'Actualisation, vérification et recherche de films',
    runtime: 'Processus plugin livré avec l’image worker (déclaré dans Runtime:Plugins)',
    configuration:
      'Connexion, correspondance des chemins, déclencheur d’import (webhook) et test de connexion',
    state: 'Catalogue intégré',
    iconUrl: '/plugins/radarr.svg',
  },
];

const PLUGIN_FAMILY_OPTIONS: readonly { readonly value: PluginFamily; readonly label: string }[] = [
  { value: 'base', label: 'Noyau' },
  { value: 'cluster', label: 'Cluster' },
  { value: 'media', label: 'Média' },
  { value: 'automation', label: 'Automatisation' },
  { value: 'custom', label: 'Tiers' },
];

const PLUGIN_FAMILY_DESCRIPTIONS: Readonly<Record<PluginFamily, string>> = {
  base: 'Composants natifs et logique des flows',
  cluster: 'Répartition et circulation des traitements entre workers',
  media: 'Traitement, lecture et analyse des médias',
  automation: 'Intégrations avec les services de médiathèque',
  custom: 'Extensions installées depuis le Store',
};

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-plugin-catalog-page',
  imports: [
    StatusBadgeComponent,
    ResourceSheetComponent,
    ClusterMaintenanceTemplateComponent,
    CategoryTabsComponent,
    ComponentConfigurationFormComponent,
    FlowComponentListComponent,
    PlexSetupAssistantComponent,
    MatTabsModule,
    MatButtonModule,
    MatSlideToggleModule,
  ],
  templateUrl: './plugin-catalog-page.component.html',
  styleUrl: './plugin-catalog-page.component.scss',
})
export class PluginCatalogPageComponent {
  private readonly destroyRef = inject(DestroyRef);
  private readonly pageShellContext = inject(PageShellContextService);
  private readonly flowValidation = inject(FlowValidation);
  private readonly configurationApi = inject(PluginConfigurationApi);
  private readonly extensionsService = inject(ClusterExtensionsService);
  private readonly notifications = inject(NotificationService);
  private readonly route = inject(ActivatedRoute);
  private readonly routeData = toSignal(this.route.data, {
    initialValue: this.route.snapshot.data,
  });

  protected readonly title = computed(
    () => (this.routeData()['title'] as string) ?? 'Plugins & Outils',
  );
  protected readonly activeTab = signal<ExtensionTab>('plugins');
  protected readonly clusterTools = signal<readonly ClusterTool[]>([]);
  protected readonly storeRepositories = signal<readonly StoreRepository[]>([]);
  protected readonly storePackages = signal<readonly StorePackage[]>([]);
  protected readonly selectedStoreCategory = signal('all');
  protected readonly searchQuery = signal('');

  protected readonly extensionTabs = computed<readonly CategoryTabOption[]>(() => [
    { value: 'plugins', label: 'Plugins', count: this.pluginRecords().length },
    { value: 'tools', label: 'Outils média', count: this.clusterTools().length },
    { value: 'store', label: 'Store & Dépôts', count: this.storePackages().length },
  ]);

  protected readonly storeCategoryOptions = computed<readonly CategoryTabOption[]>(() => [
    { value: 'all', label: 'Tous', count: this.storePackages().length },
    {
      value: 'subtitles',
      label: 'Sous-titres',
      count: this.storePackages().filter((p) => p.category === 'subtitles').length,
    },
    {
      value: 'audio',
      label: 'Audio',
      count: this.storePackages().filter((p) => p.category === 'audio').length,
    },
    {
      value: 'tools',
      label: 'Outils Média',
      count: this.storePackages().filter((p) => p.category === 'tools').length,
    },
  ]);

  protected readonly filteredStorePackages = computed(() => {
    const cat = this.selectedStoreCategory();
    const query = this.searchQuery().trim().toLocaleLowerCase('fr-FR');
    const pkgs = this.storePackages();
    return pkgs.filter(
      (pkg) =>
        (cat === 'all' || pkg.category === cat) &&
        (!query ||
          [
            pkg.name,
            pkg.id,
            pkg.author,
            pkg.description,
            pkg.categoryLabel,
            pkg.requiredTools.join(' '),
          ]
            .join(' ')
            .toLocaleLowerCase('fr-FR')
            .includes(query)),
    );
  });
  protected readonly filteredTools = computed(() => {
    const query = this.searchQuery().trim().toLocaleLowerCase('fr-FR');
    return this.clusterTools().filter(
      (tool) =>
        !query ||
        [
          tool.name,
          tool.id,
          tool.role,
          tool.provider,
          tool.stockVersion,
          tool.executables.join(' '),
          tool.mediaTypes.join(' '),
        ]
          .join(' ')
          .toLocaleLowerCase('fr-FR')
          .includes(query),
    );
  });
  protected readonly filteredRepositories = computed(() => {
    const query = this.searchQuery().trim().toLocaleLowerCase('fr-FR');
    return this.storeRepositories().filter(
      (repo) =>
        !query ||
        [repo.name, repo.url, repo.description, repo.lastError ?? '']
          .join(' ')
          .toLocaleLowerCase('fr-FR')
          .includes(query),
    );
  });

  protected readonly rejectedPlugins = signal<readonly RejectedPlugin[]>([]);
  protected readonly selectedPlugin = signal<PluginRecord | null>(null);
  private readonly pluginCatalog = signal<readonly FlowPluginCatalog[]>([]);
  private readonly pluginEnabledOverrides = signal<Readonly<Record<string, boolean>>>({});
  protected readonly activationBusy = signal<ReadonlySet<string>>(new Set());
  /** Fiches connues du front, versions réelles du serveur, et plugins tiers déclarés par le catalogue. */
  protected readonly pluginRecords = computed<readonly PluginRecord[]>(() => {
    const catalog = this.pluginCatalog();
    const overrides = this.pluginEnabledOverrides();
    const known = PLUGIN_RECORDS.map((record) => {
      const live = catalog.find((plugin) => plugin.id === record.id);
      const enabled = overrides[record.id] ?? live?.enabled ?? true;
      return {
        ...record,
        ...(live ? { version: live.version } : {}),
        enabled,
        canDisable: record.id !== 'base' && record.id !== 'nodes',
        state: enabled ? record.state : 'Désactivé',
      };
    });
    const custom = catalog
      .filter((plugin) => !PLUGIN_RECORDS.some((record) => record.id === plugin.id))
      .map<PluginRecord>((plugin) => ({
        id: plugin.id,
        name: plugin.name,
        family: 'custom',
        familyLabel: 'Tiers',
        category: 'Plugin tiers',
        version: plugin.version,
        components: plugin.components.map((component) => component.name).join(', '),
        runtime: 'Processus isolé installé sur chaque worker',
        configuration: plugin.configurationSchema
          ? `${plugin.configurationSchema.fields.length} champ(s) déclaré(s)`
          : 'Aucune configuration globale',
        state: plugin.enabled === false ? 'Désactivé' : 'Actif',
        iconUrl: '/plugins/base.svg',
        enabled: overrides[plugin.id] ?? plugin.enabled ?? true,
        canDisable: true,
      }));
    return [...known, ...custom];
  });
  protected readonly configurationValues = signal<FlowComponentConfiguration>({});
  protected readonly configuredSecrets = signal<readonly string[]>([]);
  protected readonly configurationRevision = signal(0);
  protected readonly configurationBusy = signal(false);
  protected readonly connectionTestBusy = signal(false);
  protected readonly connectionTestMessage = signal('');
  protected readonly connectionTestSuccess = signal(false);
  private configurationOperation = 0;
  private connectionTestOperation = 0;
  protected readonly configurationSchema = computed<FlowComponentConfigurationSchema | undefined>(
    () => {
      const pluginId = this.selectedPlugin()?.id;
      const schema = this.pluginCatalog().find(
        (plugin) => plugin.id === pluginId,
      )?.configurationSchema;
      if (!schema) return undefined;
      const secrets = this.configuredSecrets();
      return {
        ...schema,
        fields: schema.fields.map((field) =>
          field.type === 'secret' && secrets.includes(field.id)
            ? { ...field, required: false }
            : field,
        ),
      };
    },
  );
  protected readonly detailTitle = computed(() => this.selectedPlugin()?.name ?? 'Plugin');
  protected readonly detailDescription = computed(
    () => this.selectedPlugin()?.familyLabel ?? 'Détails du plugin',
  );
  protected readonly selectedComponents = computed<readonly FlowComponentDefinition[]>(() => {
    const plugin = this.selectedPlugin();
    if (!plugin) return [];
    return this.pluginComponents(plugin.id).map((component) => ({
      ...component,
      pluginId: plugin.id,
      pluginName: plugin.name,
    }));
  });
  protected readonly pluginSheet = computed<{
    readonly title: string;
    readonly subtitle: string;
    readonly iconUrl?: string;
    readonly sections: readonly ResourceSheetSection[];
  } | null>(() => {
    const plugin = this.selectedPlugin();
    if (!plugin) return null;

    const components = this.pluginComponents(plugin.id);
    const schema = this.configurationSchema();
    const active = plugin.state.toLocaleLowerCase('fr-FR').includes('actif');
    const mediaTypes = [...new Set(components.map((component) => component.mediaType))];

    const sections: readonly ResourceSheetSection[] = [
      {
        id: 'plugin-identity',
        title: 'Identité du plugin',
        items: [
          {
            label: plugin.name,
            detail: plugin.category,
            badges: [
              { label: plugin.state, tone: active ? 'success' : 'accent' },
              { label: plugin.familyLabel, tone: 'accent' },
              { label: `Version ${plugin.version}`, tone: 'neutral' },
            ],
          },
        ],
        fields: [
          { label: 'Famille', value: plugin.familyLabel },
          { label: 'Type', value: plugin.category },
          { label: 'Version', value: plugin.version },
        ],
      },
      {
        id: 'plugin-runtime',
        title: 'Exécution et configuration',
        caption: schema ? `${schema.fields.length} champ(s)` : 'Sans schéma',
        fields: [
          { label: 'Mode d’exécution', value: plugin.runtime },
          { label: 'Configuration', value: plugin.configuration },
        ],
      },
      {
        id: 'plugin-components',
        title: 'Catalogue des composants',
        caption: `${components.length} composant(s)`,
        items: [
          {
            label: 'Blocs déclarés',
            detail: `${components.length} composant(s) · consulter l’onglet Composants`,
            badges: [
              {
                label: components.length ? 'Catalogue chargé' : 'Aucun bloc',
                tone: components.length ? 'success' : 'warning',
              },
              ...mediaTypes.map((mediaType) => ({ label: mediaType, tone: 'accent' as const })),
            ],
          },
        ],
      },
    ];

    return {
      title: plugin.name,
      subtitle: `${plugin.familyLabel} · ${plugin.category}`,
      iconUrl: plugin.iconUrl,
      sections,
    };
  });

  protected readonly filteredPlugins = computed(() => {
    const query = this.searchQuery().trim().toLocaleLowerCase('fr-FR');
    return this.pluginRecords().filter(
      (plugin) =>
        !query ||
        [
          plugin.name,
          plugin.familyLabel,
          plugin.category,
          plugin.components,
          plugin.runtime,
          plugin.configuration,
          plugin.state,
        ]
          .join(' ')
          .toLocaleLowerCase('fr-FR')
          .includes(query),
    );
  });

  protected readonly pluginGroups = computed(() => {
    const plugins = this.filteredPlugins();
    const groups = PLUGIN_FAMILY_OPTIONS.map((family) => ({
      ...family,
      description: PLUGIN_FAMILY_DESCRIPTIONS[family.value],
      plugins: plugins.filter((plugin) => plugin.family === family.value),
    }));
    const hasQuery = !!this.searchQuery().trim();
    if (plugins.length) {
      const visible = groups.filter((group) => group.plugins.length > 0);
      const customFamily = groups.find((group) => group.value === 'custom');
      if (!hasQuery && customFamily?.plugins.length === 0) visible.push(customFamily);
      return visible;
    }
    if (hasQuery) return [];
    const customFamily = groups.find((group) => group.value === 'custom');
    return customFamily ? [customFamily] : [];
  });

  private pluginComponents(pluginId: string) {
    return this.pluginCatalog().find((plugin) => plugin.id === pluginId)?.components ?? [];
  }

  constructor() {
    this.destroyRef.onDestroy(
      this.pageShellContext.register({
        searchPlaceholder: 'Rechercher dans Plugins & outils…',
        query: this.searchQuery,
        onSearchQueryChange: (query) => this.searchQuery.set(query),
        searchResults: (query) => this.extensionSearchResults(query),
        actions: [
          {
            id: 'add-repository',
            label: 'Ajouter un dépôt',
            icon: 'add',
            visibleWhen: () => this.activeTab() === 'store',
            run: () => this.addRepositoryFromShell(),
          },
          {
            id: 'import-store-archive',
            label: 'Importer une archive',
            icon: 'upload',
            visibleWhen: () => this.activeTab() === 'store',
            run: () => document.querySelector<HTMLInputElement>('.store-archive-file')?.click(),
          },
        ],
      }),
    );
    void this.flowValidation
      .catalog()
      .then((plugins) => {
        this.pluginCatalog.set(plugins);
        const selected = this.selectedPlugin();
        if (selected) void this.loadConfiguration(selected.id);
      })
      .catch(() =>
        this.notifications.show('Le catalogue de configuration des plugins est indisponible.'),
      );

    void this.extensionsService
      .listRejectedPlugins()
      .then((rejected) => this.rejectedPlugins.set(rejected))
      .catch(() => {});

    void this.extensionsService
      .listTools()
      .then((tools) => this.clusterTools.set(tools))
      .catch(() => {});

    void this.extensionsService
      .listRepositories()
      .then((repos) => this.storeRepositories.set(repos))
      .catch(() => {});

    void this.extensionsService
      .listPackages()
      .then((pkgs) => this.storePackages.set(pkgs))
      .catch(() => {});
  }

  protected onExtensionTabChange(tab: string): void {
    this.activeTab.set(tab as ExtensionTab);
  }

  protected onStoreCategoryChange(cat: string): void {
    this.selectedStoreCategory.set(cat);
  }

  protected isActivationBusy(kind: 'plugin' | 'tool', id: string): boolean {
    return this.activationBusy().has(`${kind}:${id}`);
  }

  protected async setPluginEnabled(plugin: PluginRecord, enabled: boolean): Promise<void> {
    const busyKey = `plugin:${plugin.id}`;
    if (!plugin.canDisable || this.activationBusy().has(busyKey)) return;
    this.setActivationBusy(busyKey, true);
    this.setPluginActivationView(plugin.id, enabled);
    try {
      const result = await this.extensionsService.setPluginEnabled(plugin.id, enabled);
      this.setPluginActivationView(plugin.id, result.enabled);
      this.notifications.show(
        result.enabled
          ? `Plugin « ${plugin.name} » activé pour le cluster.`
          : `Plugin « ${plugin.name} » désactivé pour les nouveaux traitements du cluster.`,
      );
    } catch (error) {
      this.setPluginActivationView(plugin.id, plugin.enabled);
      this.notifications.show(this.storeError(error, 'Impossible de modifier l’activation du plugin.'));
    } finally {
      this.setActivationBusy(busyKey, false);
    }
  }

  protected async setToolEnabled(tool: ClusterTool, enabled: boolean): Promise<void> {
    const busyKey = `tool:${tool.id}`;
    if (this.activationBusy().has(busyKey)) return;
    this.setActivationBusy(busyKey, true);
    this.setToolActivationView(tool.id, enabled);
    try {
      const result = await this.extensionsService.setToolEnabled(tool.id, enabled);
      this.setToolActivationView(tool.id, result.enabled);
      this.notifications.show(
        result.enabled
          ? `Outil « ${tool.name} » activé pour le cluster.`
          : `Outil « ${tool.name} » désactivé pour les nouveaux traitements du cluster.`,
      );
    } catch (error) {
      this.setToolActivationView(tool.id, tool.enabled);
      this.notifications.show(this.storeError(error, 'Impossible de modifier l’activation de l’outil.'));
    } finally {
      this.setActivationBusy(busyKey, false);
    }
  }

  private setPluginActivationView(id: string, enabled: boolean): void {
    this.pluginEnabledOverrides.update((current) => ({ ...current, [id]: enabled }));
    const defaultState = PLUGIN_RECORDS.find((record) => record.id === id)?.state ?? 'Actif';
    this.selectedPlugin.update((current) =>
      current?.id === id
        ? { ...current, enabled, state: enabled ? defaultState : 'Désactivé' }
        : current,
    );
  }

  private setToolActivationView(id: string, enabled: boolean): void {
    this.clusterTools.update((tools) =>
      tools.map((tool) => (tool.id === id ? { ...tool, enabled } : tool)),
    );
  }

  private setActivationBusy(key: string, busy: boolean): void {
    this.activationBusy.update((current) => {
      const next = new Set(current);
      if (busy) next.add(key);
      else next.delete(key);
      return next;
    });
  }

  protected readonly repositoryUrl = signal('');
  protected readonly storeBusy = signal(false);

  private storeError(error: unknown, fallback: string): string {
    return (error as { error?: { title?: string } }).error?.title ?? fallback;
  }

  private async refreshStore(): Promise<void> {
    const [repositories, packages] = await Promise.all([
      this.extensionsService.listRepositories(),
      this.extensionsService.listPackages(),
    ]);
    this.storeRepositories.set(repositories);
    this.storePackages.set(packages);
  }

  private async runStoreAction(action: () => Promise<unknown>, success: string): Promise<void> {
    this.storeBusy.set(true);
    try {
      await action();
      await this.refreshStore();
      this.notifications.show(success);
    } catch (error) {
      this.notifications.show(this.storeError(error, 'L’opération sur le Store a échoué.'));
    } finally {
      this.storeBusy.set(false);
    }
  }

  protected onRepositoryUrlInput(event: Event): void {
    this.repositoryUrl.set((event.target as HTMLInputElement).value);
  }

  protected installPackage(pkg: StorePackage): void {
    void this.runStoreAction(
      () => pkg.kind === 'tool'
        ? this.extensionsService.installToolPackage(pkg.repositoryId, pkg.id, pkg.version)
        : this.extensionsService.installPackage(pkg.repositoryId, pkg.id, pkg.version),
      `« ${pkg.name} » ${pkg.version} est installé ; les workers le téléchargent dans la minute.`,
    );
  }

  protected removePackage(pkg: StorePackage): void {
    void this.runStoreAction(
      () => pkg.kind === 'tool'
        ? this.extensionsService.removeToolPackage(pkg.id, pkg.platform ?? 'unsupported')
        : this.extensionsService.removePackage(pkg.id),
      `« ${pkg.name} » est retiré.`,
    );
  }

  protected addCustomRepository(): void {
    const url = this.repositoryUrl().trim();
    if (!url) return;
    void this.runStoreAction(async () => {
      await this.extensionsService.addRepository(url);
      this.repositoryUrl.set('');
    }, 'Dépôt ajouté.');
  }

  protected syncRepository(repo: StoreRepository): void {
    void this.runStoreAction(
      () => this.extensionsService.syncRepository(repo.id),
      'Dépôt synchronisé.',
    );
  }

  protected toggleRepository(repo: StoreRepository): void {
    void this.runStoreAction(
      () => this.extensionsService.setRepositoryEnabled(repo.id, !repo.enabled),
      repo.enabled ? 'Dépôt désactivé.' : 'Dépôt activé.',
    );
  }

  protected removeRepository(repo: StoreRepository): void {
    void this.runStoreAction(
      () => this.extensionsService.removeRepository(repo.id),
      'Dépôt supprimé.',
    );
  }

  protected onArchiveSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    void this.runStoreAction(
      () => this.extensionsService.importArchive(file),
      `Archive « ${file.name} » installée.`,
    );
  }

  protected onPluginOpen(pluginId: string): void {
    const plugin = this.pluginRecords().find((candidate) => candidate.id === pluginId) ?? null;
    this.selectedPlugin.set(plugin);
    this.configurationValues.set({});
    this.configuredSecrets.set([]);
    this.configurationRevision.set(0);
    this.connectionTestMessage.set('');
    this.connectionTestOperation++;
    this.connectionTestBusy.set(false);
    if (plugin && this.pluginCatalog().some((candidate) => candidate.id === plugin.id)) {
      void this.loadConfiguration(plugin.id);
    }
  }

  private extensionSearchResults(query: string): readonly PageShellSearchResult[] {
    const normalized = query.trim().toLocaleLowerCase('fr-FR');
    const matches = (values: readonly string[]) =>
      !normalized || values.join(' ').toLocaleLowerCase('fr-FR').includes(normalized);
    if (this.activeTab() === 'plugins') {
      return this.pluginRecords()
        .filter((plugin) =>
          matches([
            plugin.name,
            plugin.familyLabel,
            plugin.category,
            plugin.components,
            plugin.runtime,
            plugin.state,
          ]),
        )
        .slice(0, 8)
        .map((plugin) => ({
          id: `plugin:${plugin.id}`,
          label: plugin.name,
          description: `${plugin.familyLabel} · ${plugin.category}`,
          group: 'Plugins',
          glyph: '◇',
          run: () => this.onPluginOpen(plugin.id),
        }));
    }
    if (this.activeTab() === 'tools') {
      return this.clusterTools()
        .filter((tool) =>
          matches([
            tool.name,
            tool.id,
            tool.role,
            tool.provider,
            ...tool.executables,
            ...tool.mediaTypes,
          ]),
        )
        .slice(0, 8)
        .map((tool) => ({
          id: `tool:${tool.id}`,
          label: tool.name,
          description: `${tool.clusterReadiness} · ${tool.executables.join(', ')}`,
          group: 'Outils média',
          glyph: '⚙',
          run: () =>
            document.getElementById(`cluster-tool-${tool.id}`)?.scrollIntoView({ block: 'center' }),
        }));
    }
    const repositories = this.storeRepositories()
      .filter((repo) => matches([repo.name, repo.url, repo.description, repo.lastError ?? '']))
      .slice(0, 4)
      .map<PageShellSearchResult>((repo) => ({
        id: `repository:${repo.id}`,
        label: repo.name,
        description: repo.url,
        group: 'Dépôts',
        glyph: '↗',
        run: () =>
          document
            .getElementById(`store-repository-${repo.id}`)
            ?.scrollIntoView({ block: 'center' }),
      }));
    const packages = this.storePackages()
      .filter((pkg) => matches([pkg.name, pkg.id, pkg.author, pkg.description, pkg.categoryLabel]))
      .slice(0, 4)
      .map<PageShellSearchResult>((pkg) => ({
        id: `package:${pkg.id}`,
        label: pkg.name,
        description: `${pkg.categoryLabel} · v${pkg.version}`,
        group: 'Store',
        glyph: pkg.icon,
        run: () =>
          document.getElementById(`store-package-${pkg.id}`)?.scrollIntoView({ block: 'center' }),
      }));
    return [...repositories, ...packages].slice(0, 8);
  }

  private addRepositoryFromShell(): void {
    if (this.repositoryUrl().trim()) {
      this.addCustomRepository();
      return;
    }
    document.querySelector<HTMLInputElement>('.store-url')?.focus();
  }

  private async loadConfiguration(pluginId: string): Promise<void> {
    const operation = ++this.configurationOperation;
    this.configurationBusy.set(true);
    try {
      const result = await this.configurationApi.getGlobal(pluginId);
      if (operation !== this.configurationOperation || this.selectedPlugin()?.id !== pluginId)
        return;
      this.configurationValues.set(result.values);
      this.configuredSecrets.set(result.configuredSecrets);
      this.configurationRevision.set(result.revision);
    } catch {
      if (operation === this.configurationOperation)
        this.notifications.show('Impossible de charger la configuration du plugin.');
    } finally {
      if (operation === this.configurationOperation) this.configurationBusy.set(false);
    }
  }

  protected async saveConfiguration(
    values: FlowComponentConfiguration,
    clearSecrets: readonly string[] = [],
  ): Promise<void> {
    const plugin = this.selectedPlugin();
    if (!plugin || this.configurationBusy()) return;
    const operation = ++this.configurationOperation;
    this.configurationBusy.set(true);
    try {
      const saved = await this.configurationApi.saveGlobal(
        plugin.id,
        this.configurationRevision(),
        values,
        clearSecrets,
      );
      if (operation !== this.configurationOperation || this.selectedPlugin()?.id !== plugin.id)
        return;
      this.configurationValues.set(saved.values);
      this.configuredSecrets.set(saved.configuredSecrets);
      this.configurationRevision.set(saved.revision);
      this.notifications.show('Configuration globale du plugin enregistrée.');
    } catch {
      if (operation === this.configurationOperation)
        this.notifications.show('Configuration non enregistrée. Rechargez-la avant de réessayer.');
    } finally {
      if (operation === this.configurationOperation) this.configurationBusy.set(false);
    }
  }

  /** L'assistant Plex a enregistré la configuration : le formulaire reflète les valeurs et la révision du serveur. */
  protected onAssistantSaved(saved: PluginConfigurationResponse): void {
    if (this.selectedPlugin()?.id !== saved.pluginId) return;
    this.configurationValues.set(saved.values);
    this.configuredSecrets.set(saved.configuredSecrets);
    this.configurationRevision.set(saved.revision);
  }

  protected onConfigurationCancelled(): void {
    const plugin = this.selectedPlugin();
    if (plugin) void this.loadConfiguration(plugin.id);
  }

  protected async testConnection(values: FlowComponentConfiguration): Promise<void> {
    const plugin = this.selectedPlugin();
    if (!plugin || this.connectionTestBusy()) return;
    const operation = ++this.connectionTestOperation;
    this.connectionTestBusy.set(true);
    this.connectionTestMessage.set('');
    try {
      const result = await this.configurationApi.testConnection(plugin.id, values);
      if (operation !== this.connectionTestOperation || this.selectedPlugin()?.id !== plugin.id)
        return;
      this.connectionTestSuccess.set(result.success);
      this.connectionTestMessage.set(result.message);
    } catch (error) {
      if (operation === this.connectionTestOperation && this.selectedPlugin()?.id === plugin.id) {
        this.connectionTestSuccess.set(false);
        const validationMessage =
          error instanceof HttpErrorResponse && typeof error.error?.title === 'string'
            ? error.error.title
            : undefined;
        this.connectionTestMessage.set(
          validationMessage ?? 'Test impossible. Vérifiez les champs puis réessayez.',
        );
      }
    } finally {
      if (operation === this.connectionTestOperation) this.connectionTestBusy.set(false);
    }
  }

  protected onDetailClosed(): void {
    this.configurationOperation++;
    this.connectionTestOperation++;
    this.configurationBusy.set(false);
    this.connectionTestBusy.set(false);
    this.selectedPlugin.set(null);
  }
}
