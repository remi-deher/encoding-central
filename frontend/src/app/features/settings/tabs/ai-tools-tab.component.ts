import { AiModelCatalogComponent } from './ai-model-catalog.component';
import { AiQuotaPanelComponent } from './ai-quota-panel.component';
import { AiReplicationPanelComponent } from './ai-replication-panel.component';
import { AiUsagePanelComponent } from './ai-usage-panel.component';
import { AiSessionHistoryComponent } from './ai-session-history.component';
import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnDestroy,
  OnInit,
  computed,
  inject,
  signal,
  TemplateRef,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { interval } from 'rxjs';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { IconComponent } from '../../../shared/ui/icon.component';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatDialog } from '@angular/material/dialog';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { ConfigurationSectionComponent } from '../../../shared/ui/configuration-section.component';
import {
  SettingGroupComponent,
  SettingRowComponent,
} from '../../../shared/ui/settings/setting-row.component';
import { SettingsNoticeComponent } from '../../../shared/ui/settings/settings-notice.component';
import { SettingsSaveBarComponent } from '../../../shared/ui/settings/settings-save-bar.component';
import { SettingsEditor } from '../settings-editor';
import { AiToolsService } from '../../../shared/services/ai-tools.service';
import { NotificationService } from '../../../shared/services/notification.service';
import {
  AiSettings,
  AiSettingsUpdateRequest,
  AiToolInfo,
  AiClusterView,
  AiClusterModelCatalog,
  AiDiscoveredModel,
} from '../../../shared/models/ai-tools';
import { ConfirmationDialogComponent } from '../../../shared/ui/confirmation-dialog.component';
import { DeviceAuthDialogComponent } from '../../../shared/ui/device-auth-dialog.component';
import { AuthService } from '../../../core/auth/auth.service';
import { aiEffortLabel, aiTypeLabel } from '../../ai/components/ai-model-selection';

@Component({
  selector: 'app-ai-tools-tab',
  host: { '(window:beforeunload)': 'beforeUnload($event)' },
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ConfigurationSectionComponent,
    SettingGroupComponent,
    SettingRowComponent,
    SettingsNoticeComponent,
    SettingsSaveBarComponent,
    AiQuotaPanelComponent,
    AiReplicationPanelComponent,
    AiUsagePanelComponent,
    AiModelCatalogComponent,
    AiSessionHistoryComponent,
    FormsModule,
    MatButtonModule,
    IconComponent,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatProgressSpinnerModule,
    MatExpansionModule,
    MatSlideToggleModule,
  ],
  templateUrl: './ai-tools-tab.component.html',
  styleUrl: './ai-tools-tab.component.scss',
})
export class AiToolsTabComponent implements OnInit, OnDestroy {
  protected readonly editor = inject(SettingsEditor);
  private readonly providerEditor = viewChild.required<TemplateRef<unknown>>('providerEditor');
  readonly settingsError = signal(false);
  private savedSettings: AiSettings | null = null;
  openProvider(id: string): void {
    this.selectedProvider.set(id);
    void this.editor.open({
      title: this.selectedName(),
      content: this.providerEditor(),
      dirty: () => this.hasUnsavedChanges(),
      busy: () => this.savingSettings() || this.connecting(),
      onClosed: () => this.discardChanges(),
    });
  }
  discardChanges(): void {
    if (this.savedSettings) this.applySettings(this.savedSettings);
  }
  providerStatus(id: string): string {
    if (id === 'ollama')
      return this.ollamaBaseUrl() && this.ollamaModel() ? 'Configuré' : 'Non configuré';
    if (id === 'openai')
      return this.openAiBaseUrl() && this.openAiModel() ? 'Configuré' : 'Non configuré';
    if (id === 'anthropic')
      return this.anthropicModel() && (this.hasAnthropicApiKey() || this.anthropicApiKey())
        ? 'Configuré'
        : 'Non configuré';
    const tool = this.tools().find((item) => item.id === id);
    return tool ? this.authLabel(tool.authStatus.state) : 'État indisponible';
  }
  providerEnabled(id: string): boolean {
    if (id === 'ollama') return this.ollamaEnabled();
    if (id === 'openai') return this.openAiEnabled();
    if (id === 'anthropic') return this.anthropicEnabled();
    return id === 'codex-cli' ? this.codexEnabled() : this.claudeEnabled();
  }
  setProviderEnabled(id: string, enabled: boolean): void {
    if (id === 'ollama') this.ollamaEnabled.set(enabled);
    else if (id === 'openai') this.openAiEnabled.set(enabled);
    else if (id === 'anthropic') this.anthropicEnabled.set(enabled);
    else if (id === 'codex-cli') this.codexEnabled.set(enabled);
    else this.claudeEnabled.set(enabled);
  }
  removeProvider(id: string): void {
    if (id === 'ollama' || id === 'openai' || id === 'anthropic')
      this.deleteProviderConfiguration(id);
    else this.revoke(id);
  }
  deleteProviderConfiguration(id: string): void {
    if (id === 'ollama') {
      this.ollamaEnabled.set(false);
      this.ollamaBaseUrl.set('');
      this.ollamaModel.set('');
    } else if (id === 'openai') {
      this.openAiEnabled.set(false);
      this.openAiBaseUrl.set('');
      this.openAiModel.set('');
      this.openAiApiKey.set('');
    } else if (id === 'anthropic') {
      this.anthropicEnabled.set(false);
      this.anthropicModel.set('');
      this.anthropicApiKey.set('');
    }
  }
  private readonly aiService = inject(AiToolsService);
  private readonly notifications = inject(NotificationService);
  private readonly dialog = inject(MatDialog);
  private readonly destroyRef = inject(DestroyRef);
  readonly isAdmin = inject(AuthService).isAdmin;
  readonly providers = [
    { id: 'ollama', name: 'Ollama' },
    { id: 'openai', name: 'OpenAI / API compatible' },
    { id: 'anthropic', name: 'API Anthropic' },
    { id: 'codex-cli', name: 'Codex CLI' },
    { id: 'claude-cli', name: 'Claude Code' },
  ];
  readonly usageOptions = ['assistant', 'flow'] as const;
  readonly tools = signal<readonly AiToolInfo[]>([]);
  readonly cluster = signal<AiClusterView | null>(null);
  readonly modelCatalog = signal<AiClusterModelCatalog | null>(null);
  readonly loadingModelCatalog = signal(false);
  readonly quotaRevision = signal(0);
  readonly loadingSettings = signal(true);
  readonly savingSettings = signal(false);
  readonly connecting = signal(false);
  readonly selectedProvider = signal('codex-cli');
  readonly selectedName = computed(
    () => this.providers.find((p) => p.id === this.selectedProvider())?.name ?? 'Fournisseur',
  );
  readonly selectedTool = computed(() =>
    this.tools().find((t) => t.id === this.selectedProvider()),
  );
  readonly defaultProvider = signal('codex-cli');
  readonly defaultModel = signal<string | null>(null);
  readonly defaultReasoningEffort = signal<string | null>(null);
  readonly ollamaBaseUrl = signal('');
  readonly ollamaModel = signal('');
  readonly openAiBaseUrl = signal('');
  readonly openAiModel = signal('');
  readonly openAiApiKey = signal('');
  readonly hasOpenAiApiKey = signal(false);
  readonly anthropicModel = signal('');
  readonly anthropicApiKey = signal('');
  readonly hasAnthropicApiKey = signal(false);
  readonly anthropicEnabled = signal(true);
  readonly ollamaEnabled = signal(true);
  readonly openAiEnabled = signal(true);
  readonly codexEnabled = signal(true);
  readonly claudeEnabled = signal(true);
  readonly timeoutSeconds = signal(120);
  readonly subtitleBatchSize = signal(80);
  readonly assistantConversationRetentionDays = signal(90);
  readonly assistantActivityRetentionDays = signal(30);
  readonly assistantProvider = signal<string | null>(null);
  readonly flowProvider = signal<string | null>(null);
  readonly assistantModel = signal<string | null>(null);
  readonly assistantReasoningEffort = signal<string | null>(null);
  readonly flowModel = signal<string | null>(null);
  readonly flowReasoningEffort = signal<string | null>(null);
  readonly assistantLocalOnly = signal(false);
  readonly flowLocalOnly = signal(false);

  private savedFingerprint = '';
  private modelCatalogPollTimer: ReturnType<typeof setTimeout> | null = null;
  private fingerprint(): string {
    return JSON.stringify([
      this.defaultProvider(),
      this.defaultModel(),
      this.defaultReasoningEffort(),
      this.ollamaBaseUrl(),
      this.ollamaModel(),
      this.openAiBaseUrl(),
      this.openAiModel(),
      this.openAiApiKey(),
      this.anthropicModel(),
      this.anthropicApiKey(),
      this.anthropicEnabled(),
      this.ollamaEnabled(),
      this.openAiEnabled(),
      this.codexEnabled(),
      this.claudeEnabled(),
      this.timeoutSeconds(),
      this.subtitleBatchSize(),
      this.assistantConversationRetentionDays(),
      this.assistantActivityRetentionDays(),
      this.assistantProvider(),
      this.assistantModel(),
      this.assistantReasoningEffort(),
      this.flowProvider(),
      this.flowModel(),
      this.flowReasoningEffort(),
      this.assistantLocalOnly(),
      this.flowLocalOnly(),
    ]);
  }
  hasUnsavedChanges(): boolean {
    return !this.loadingSettings() && this.fingerprint() !== this.savedFingerprint;
  }
  beforeUnload(event: BeforeUnloadEvent): void {
    if (this.hasUnsavedChanges()) event.preventDefault();
  }
  ngOnInit(): void {
    this.refreshTools();
    this.loadSettings();
    interval(15_000)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.loadConnections(true));
  }
  ngOnDestroy(): void {
    if (this.modelCatalogPollTimer !== null) clearTimeout(this.modelCatalogPollTimer);
  }
  loadSettings(): void {
    this.settingsError.set(false);
    this.aiService.getSettings().subscribe({
      next: (settings) => {
        this.applySettings(settings);
        this.selectedProvider.set(settings.defaultProvider);
        this.loadingSettings.set(false);
      },
      error: () => {
        this.settingsError.set(true);
        this.notifications.show('Impossible de charger les paramètres IA.');
      },
    });
  }
  refreshTools(): void {
    this.quotaRevision.update((value) => value + 1);
    this.loadModelCatalog();
    this.loadConnections();
  }
  private loadConnections(silent = false): void {
    this.aiService.listTools().subscribe({
      next: (tools) => {
        this.tools.set(tools);
      },
      error: () => {
        if (!silent) this.notifications.show('Impossible de charger les connexions locales.');
      },
    });
    this.aiService.getCluster().subscribe({
      next: (view) => this.cluster.set(view),
      error: () => {},
    });
  }
  private applySettings(settings: AiSettings): void {
    this.savedSettings = settings;
    this.openAiApiKey.set('');
    this.anthropicApiKey.set('');
    this.defaultProvider.set(settings.defaultProvider);
    this.defaultModel.set(settings.defaultModel ?? null);
    this.defaultReasoningEffort.set(settings.defaultReasoningEffort ?? null);
    this.ollamaBaseUrl.set(settings.ollamaBaseUrl);
    this.ollamaModel.set(settings.ollamaModel);
    this.openAiBaseUrl.set(settings.openAiBaseUrl);
    this.openAiModel.set(settings.openAiModel);
    this.hasOpenAiApiKey.set(settings.hasOpenAiApiKey ?? false);
    this.anthropicModel.set(settings.anthropicModel ?? '');
    this.hasAnthropicApiKey.set(settings.hasAnthropicApiKey ?? false);
    this.anthropicEnabled.set(settings.anthropicEnabled ?? Boolean(settings.hasAnthropicApiKey));
    this.ollamaEnabled.set(settings.ollamaEnabled ?? Boolean(settings.ollamaModel));
    this.openAiEnabled.set(settings.openAiEnabled ?? Boolean(settings.openAiModel));
    this.codexEnabled.set(settings.codexEnabled ?? true);
    this.claudeEnabled.set(settings.claudeEnabled ?? true);
    this.timeoutSeconds.set(settings.timeoutSeconds);
    this.subtitleBatchSize.set(settings.subtitleBatchSize);
    this.assistantConversationRetentionDays.set(settings.assistantConversationRetentionDays ?? 90);
    this.assistantActivityRetentionDays.set(settings.assistantActivityRetentionDays ?? 30);
    this.assistantProvider.set(settings.assistantProvider ?? null);
    this.assistantModel.set(settings.assistantModel ?? null);
    this.assistantReasoningEffort.set(settings.assistantReasoningEffort ?? null);
    this.flowProvider.set(settings.flowProvider ?? null);
    this.flowModel.set(settings.flowModel ?? null);
    this.flowReasoningEffort.set(settings.flowReasoningEffort ?? null);
    this.assistantLocalOnly.set(settings.assistantLocalOnly ?? false);
    this.flowLocalOnly.set(settings.flowLocalOnly ?? false);
    this.savedFingerprint = this.fingerprint();
  }
  saveSettings(): void {
    if (!this.isAdmin() || this.savingSettings()) return;
    if (
      !Number.isInteger(this.timeoutSeconds()) ||
      !Number.isInteger(this.subtitleBatchSize()) ||
      !Number.isInteger(this.assistantConversationRetentionDays()) ||
      !Number.isInteger(this.assistantActivityRetentionDays()) ||
      this.timeoutSeconds() < 10 ||
      this.timeoutSeconds() > 600 ||
      this.subtitleBatchSize() < 5 ||
      this.subtitleBatchSize() > 200 ||
      this.assistantConversationRetentionDays() < 1 ||
      this.assistantConversationRetentionDays() > 3650 ||
      this.assistantActivityRetentionDays() < 1 ||
      this.assistantActivityRetentionDays() > 3650
    ) {
      this.notifications.show('Vérifiez les limites du cluster.');
      return;
    }
    if (
      (this.assistantLocalOnly() &&
        (this.assistantProvider() ?? this.defaultProvider()) !== 'ollama') ||
      (this.flowLocalOnly() && (this.flowProvider() ?? this.defaultProvider()) !== 'ollama')
    ) {
      this.notifications.show(
        'Le mode local uniquement exige Ollama comme moteur effectif pour cet usage.',
      );
      return;
    }
    if (
      (this.defaultProvider() === 'ollama' && !this.ollamaEnabled()) ||
      (this.defaultProvider() === 'openai' && !this.openAiEnabled()) ||
      (this.defaultProvider() === 'anthropic' && !this.anthropicEnabled())
    ) {
      this.notifications.show('Le moteur par défaut doit être activé avant l’enregistrement.');
      return;
    }
    this.savingSettings.set(true);
    const request: AiSettingsUpdateRequest = {
      defaultProvider: this.defaultProvider(),
      ollamaBaseUrl: this.ollamaBaseUrl().trim(),
      ollamaModel: this.ollamaModel().trim(),
      openAiBaseUrl: this.openAiBaseUrl().trim(),
      openAiModel: this.openAiModel().trim(),
      openAiApiKey: this.openAiApiKey().trim() || null,
      anthropicModel: this.anthropicModel().trim(),
      anthropicApiKey: this.anthropicApiKey().trim() || null,
      anthropicEnabled: this.anthropicEnabled(),
      ollamaEnabled: this.ollamaEnabled(),
      openAiEnabled: this.openAiEnabled(),
      codexEnabled: this.codexEnabled(),
      claudeEnabled: this.claudeEnabled(),
      timeoutSeconds: this.timeoutSeconds(),
      subtitleBatchSize: this.subtitleBatchSize(),
      assistantConversationRetentionDays: this.assistantConversationRetentionDays(),
      assistantActivityRetentionDays: this.assistantActivityRetentionDays(),
      assistantProvider: this.assistantProvider() ?? 'default',
      flowProvider: this.flowProvider() ?? 'default',
      defaultModel: this.defaultModel() ?? '',
      defaultReasoningEffort: this.defaultReasoningEffort() ?? '',
      assistantModel: this.assistantModel() ?? '',
      assistantReasoningEffort: this.assistantReasoningEffort() ?? '',
      flowModel: this.flowModel() ?? '',
      flowReasoningEffort: this.flowReasoningEffort() ?? '',
      assistantLocalOnly: this.assistantLocalOnly(),
      flowLocalOnly: this.flowLocalOnly(),
    };
    this.aiService.updateSettings(request).subscribe({
      next: (settings) => {
        this.applySettings(settings);
        this.openAiApiKey.set('');
        this.anthropicApiKey.set('');
        this.savedFingerprint = this.fingerprint();
        this.savingSettings.set(false);
        this.notifications.show('Configuration IA enregistrée avec succès.');
        this.editor.reset();
        this.refreshTools();
      },
      error: (error: HttpErrorResponse) => {
        this.savingSettings.set(false);
        const title: unknown = error.error?.title;
        this.notifications.show(
          typeof title === 'string' && title.trim()
            ? title
            : 'Impossible d’enregistrer les paramètres IA.',
        );
      },
    });
  }

  private loadModelCatalog(): void {
    if (this.modelCatalogPollTimer !== null) clearTimeout(this.modelCatalogPollTimer);
    this.modelCatalogPollTimer = null;
    this.loadingModelCatalog.set(true);
    this.aiService.getSettingsModelCatalog().subscribe({
      next: (catalog) => {
        this.modelCatalog.set(catalog);
        const catalogLoading = catalog.nodes.some((node) =>
          node.providers.some((provider) => provider.state === 'loading'),
        );
        this.loadingModelCatalog.set(catalogLoading);
        if (catalogLoading)
          this.modelCatalogPollTimer = window.setTimeout(() => this.loadModelCatalog(), 750);
      },
      error: () => {
        this.modelCatalog.set(null);
        this.loadingModelCatalog.set(false);
      },
    });
  }

  providerChoices(
    usage: 'default' | 'assistant' | 'flow' = 'default',
  ): readonly { id: string; name: string }[] {
    return this.providers
      .filter((provider) => this.modelChoices(provider.id, usage).length > 0)
      .map((provider) => ({ ...provider, name: aiTypeLabel(provider.id) }));
  }

  private modelAnnouncements(
    providerId: string,
    usage: 'default' | 'assistant' | 'flow',
  ): readonly AiDiscoveredModel[] {
    const catalog = this.modelCatalog();
    if (!catalog) return [];
    const disabled = new Set(
      catalog.policy.disabledModels.map((model) => `${model.provider}\u0000${model.model}`),
    );
    const localOnly =
      usage === 'assistant'
        ? this.assistantLocalOnly()
        : usage === 'flow'
          ? this.flowLocalOnly()
          : false;
    const nodes = localOnly ? catalog.nodes.slice(0, 1) : catalog.nodes;
    return nodes.flatMap((node) =>
      node.providers
        .filter((provider) => provider.provider === providerId && provider.state === 'available')
        .flatMap((provider) => provider.models)
        .filter((model) => !disabled.has(`${providerId}\u0000${model.id}`)),
    );
  }

  modelChoices(
    providerId: string,
    usage: 'default' | 'assistant' | 'flow' = 'default',
  ): readonly AiDiscoveredModel[] {
    const models = this.modelAnnouncements(providerId, usage);
    const choices = new Map<string, AiDiscoveredModel>();
    for (const model of models) {
      const existing = choices.get(model.id);
      choices.set(
        model.id,
        existing
          ? {
              ...existing,
              reasoningEfforts: [
                ...new Set([...existing.reasoningEfforts, ...model.reasoningEfforts]),
              ],
              defaultReasoningEffort:
                existing.defaultReasoningEffort ?? model.defaultReasoningEffort,
              isDefault: existing.isDefault || model.isDefault,
              reasoningKnown: existing.reasoningKnown || model.reasoningKnown,
            }
          : model,
      );
    }
    return [...choices.values()];
  }

  reasoningChoices(
    providerId: string,
    modelId: string | null,
    usage: 'default' | 'assistant' | 'flow' = 'default',
  ): readonly string[] {
    if (!modelId) return [];
    return [
      ...new Set(
        this.modelAnnouncements(providerId, usage)
          .filter((model) => model.id === modelId && model.reasoningKnown)
          .flatMap((model) => model.reasoningEfforts),
      ),
    ];
  }

  effectiveProvider(usage: 'assistant' | 'flow'): string {
    return (
      (usage === 'assistant' ? this.assistantProvider() : this.flowProvider()) ??
      this.defaultProvider()
    );
  }

  modelValue(usage: 'default' | 'assistant' | 'flow'): string {
    const selected =
      usage === 'default'
        ? this.defaultModel()
        : usage === 'assistant'
          ? this.assistantModel()
          : this.flowModel();
    if (selected) return selected;
    const providerId = usage === 'default' ? this.defaultProvider() : this.effectiveProvider(usage);
    if (
      usage !== 'default' &&
      (usage === 'assistant' ? this.assistantProvider() : this.flowProvider()) === null
    ) {
      const inherited = this.defaultModel();
      if (inherited && providerId === this.defaultProvider()) return inherited;
    }
    return this.modelChoices(providerId, usage).find((model) => model.isDefault)?.id ?? '';
  }

  effortValue(usage: 'default' | 'assistant' | 'flow'): string {
    const selected =
      usage === 'default'
        ? this.defaultReasoningEffort()
        : usage === 'assistant'
          ? this.assistantReasoningEffort()
          : this.flowReasoningEffort();
    if (selected) return selected;
    const providerId = usage === 'default' ? this.defaultProvider() : this.effectiveProvider(usage);
    const modelId = this.modelValue(usage);
    if (
      usage === 'assistant' &&
      this.assistantProvider() === null &&
      (this.assistantModel() === null || this.assistantModel() === this.defaultModel())
    )
      return this.defaultReasoningEffort() ?? '';
    if (
      usage === 'flow' &&
      this.flowProvider() === null &&
      (this.flowModel() === null || this.flowModel() === this.defaultModel())
    )
      return this.defaultReasoningEffort() ?? '';
    return (
      this.modelChoices(providerId, usage).find((model) => model.id === modelId)
        ?.defaultReasoningEffort ?? ''
    );
  }

  changeDefaultProvider(providerId: string): void {
    this.defaultProvider.set(providerId);
    this.defaultModel.set(null);
    this.defaultReasoningEffort.set(null);
    if (this.assistantProvider() === null) {
      this.assistantModel.set(null);
      this.assistantReasoningEffort.set(null);
    }
    if (this.flowProvider() === null) {
      this.flowModel.set(null);
      this.flowReasoningEffort.set(null);
    }
  }

  changeUsageProvider(usage: 'assistant' | 'flow', provider: string | null): void {
    (usage === 'assistant' ? this.assistantProvider : this.flowProvider).set(provider);
    (usage === 'assistant' ? this.assistantModel : this.flowModel).set(null);
    (usage === 'assistant' ? this.assistantReasoningEffort : this.flowReasoningEffort).set(null);
  }

  changeModel(usage: 'default' | 'assistant' | 'flow', modelId: string): void {
    const modelSignal =
      usage === 'default'
        ? this.defaultModel
        : usage === 'assistant'
          ? this.assistantModel
          : this.flowModel;
    const effortSignal =
      usage === 'default'
        ? this.defaultReasoningEffort
        : usage === 'assistant'
          ? this.assistantReasoningEffort
          : this.flowReasoningEffort;
    modelSignal.set(modelId || null);
    effortSignal.set(null);
  }

  changeEffort(usage: 'default' | 'assistant' | 'flow', effort: string): void {
    const modelSignal =
      usage === 'default'
        ? this.defaultModel
        : usage === 'assistant'
          ? this.assistantModel
          : this.flowModel;
    if (effort && !modelSignal()) modelSignal.set(this.modelValue(usage) || null);
    (usage === 'default'
      ? this.defaultReasoningEffort
      : usage === 'assistant'
        ? this.assistantReasoningEffort
        : this.flowReasoningEffort
    ).set(effort || null);
  }
  startAuth(tool: AiToolInfo): void {
    if (!this.isAdmin() || !tool.isInstalled) return;
    this.connecting.set(true);
    this.aiService.startDeviceAuth(tool.id).subscribe({
      next: (response) => {
        this.connecting.set(false);
        const ref = this.dialog.open(DeviceAuthDialogComponent, {
          data: {
            toolId: tool.id,
            toolName: tool.name,
            prompt: response.prompt,
            attemptId: response.attemptId,
          },
          width: '520px',
          maxWidth: 'calc(100vw - 32px)',
        });
        ref.afterClosed().subscribe(() => this.refreshTools());
      },
      error: (error: HttpErrorResponse) => {
        this.connecting.set(false);
        this.notifications.show(this.authError(error));
        this.refreshTools();
      },
    });
  }
  private authError(error: HttpErrorResponse): string {
    const title: unknown = error.error?.title;
    return typeof title === 'string' && title.trim()
      ? title
      : 'Impossible de joindre le service de connexion. Consultez les journaux système de ce nœud.';
  }
  revoke(id: string): void {
    if (!this.isAdmin()) return;
    const count = this.cluster()?.nodes.length ?? 1;
    const message =
      id === 'codex-cli' || id === 'claude-cli'
        ? count > 1
          ? `La connexion sera supprimée sur les ${count} nœuds du cluster, y compris ceux qui sont injoignables : ils la perdront à leur retour. Les flows qui utilisent ce fournisseur échoueront tant qu’une nouvelle connexion n’est pas enregistrée.`
          : 'La connexion sera supprimée. Les flows qui utilisent ce fournisseur échoueront tant qu’une nouvelle connexion n’est pas enregistrée.'
        : 'La connexion sera supprimée.';
    this.dialog
      .open(ConfirmationDialogComponent, {
        data: {
          title: `Déconnecter ${this.providerName(id)} ?`,
          eyebrow: count > 1 ? 'Action sur le cluster' : undefined,
          message,
          confirmLabel: 'Déconnecter',
          variant: 'danger',
        },
      })
      .afterClosed()
      .subscribe((confirmed) => {
        if (confirmed === true) this.disconnect(id);
      });
  }
  private disconnect(id: string): void {
    this.connecting.set(true);
    this.aiService.revokeAuth(id).subscribe({
      next: () => {
        this.connecting.set(false);
        this.notifications.show('Connexion supprimée ; la suppression se propage au cluster.');
        this.refreshTools();
      },
      error: () => {
        this.connecting.set(false);
        this.notifications.show('Impossible de supprimer la configuration locale.');
      },
    });
  }
  clusterLabel(id: string): string {
    const view = this.cluster();
    if (!view) return 'Disponibilité à vérifier';
    const count = view.nodes.filter((n) => n.providers.includes(id)).length;
    return count
      ? `${count} nœud${count > 1 ? 's' : ''} configuré${count > 1 ? 's' : ''}`
      : 'Aucune connexion déclarée';
  }
  providerNames(ids: readonly string[]): string {
    return ids.map((id) => this.providers.find((p) => p.id === id)?.name ?? id).join(', ');
  }
  providerName(id: string): string {
    return aiTypeLabel(id);
  }
  effortName(value: string): string {
    return aiEffortLabel(value);
  }
  authLabel(state: string): string {
    return (
      (
        {
          authorized: 'Identifiants reconnus',
          checking: 'Vérification en cours…',
          pending: 'Autorisation en attente',
          expired: 'Connexion expirée',
          failed: 'Échec de connexion',
        } as Record<string, string>
      )[state] ?? 'Non associé'
    );
  }
}
