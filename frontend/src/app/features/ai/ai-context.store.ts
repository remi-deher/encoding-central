import { HttpErrorResponse } from '@angular/common/http';
import { computed, inject, Injectable, signal } from '@angular/core';
import { AiToolsService } from '../../shared/services/ai-tools.service';
import { CentralApi } from '../../shared/services/central-api';
import { ExecutionTracking } from '../../shared/services/execution-tracking';
import { FlowValidation } from '../../shared/services/flow-validation';
import {
  AiAssistantFileAttachment,
  AiAssistantSource,
} from '../../shared/models/ai-tools';
import { ExecutionResponse } from '../../shared/models/execution';
import { LibraryResponse, WorkerResponse } from '../../shared/models/central';
import {
  FlowDocument,
  FlowSummary,
  FlowVersionResponse,
} from '../../shared/models/flow-validation';
import { FlowComponentDefinition } from '../../shared/models/flow-component';
import { AI_ASSISTANT_FILE_ACCEPT } from './components/ai-conversation-composer.component';
import { AiConversationStore, AssistantResourceType } from './ai-conversation.store';

export type { AssistantResourceType };

export interface AssistantResourceOption {
  readonly type: AssistantResourceType;
  readonly id: string;
  readonly label: string;
  readonly version?: number;
}

/**
 * Contexte de l’assistant : ressources joignables (flows, exécutions, workers,
 * bibliothèques), pièces jointes, outils de lecture, aperçu de flow et
 * actions sur les sources (publier/supprimer/démarrer/annuler).
 */
@Injectable()
export class AiContextStore {
  private readonly aiTools = inject(AiToolsService);
  private readonly flowValidation = inject(FlowValidation);
  private readonly clusterApi = inject(CentralApi);
  private readonly executionTracking = inject(ExecutionTracking);
  readonly conversations = inject(AiConversationStore);

  readonly flows = signal<readonly FlowSummary[]>([]);
  readonly executions = signal<readonly ExecutionResponse[]>([]);
  readonly nodes = signal<readonly WorkerResponse[]>([]);
  readonly libraries = signal<readonly LibraryResponse[]>([]);
  readonly loadingFlows = signal(true);
  readonly loadingExecutions = signal(true);
  readonly loadingNodes = signal(true);
  readonly loadingLibraries = signal(true);

  readonly selectedResourceType = signal<AssistantResourceType>('flow');
  readonly selectedResourceId = signal('');
  readonly selectedResource = signal<AssistantResourceOption | null>(null);
  readonly attachments = signal<readonly AiAssistantFileAttachment[]>([]);
  readonly draggingFiles = signal(false);
  readonly selectedReadTools = signal<string[]>([]);
  readonly updatingSources = signal(false);
  readonly fileAccept = AI_ASSISTANT_FILE_ACCEPT;

  readonly previewOpen = signal(false);
  readonly previewTitle = signal('Aperçu');
  readonly previewFlow = signal<FlowDocument | null>(null);
  readonly previewComponents = signal<readonly FlowComponentDefinition[]>([]);
  readonly previewLoading = signal(false);
  readonly previewError = signal<string | null>(null);

  readonly readTools = [
    { name: 'list_flows', label: 'Lister les flows' },
    { name: 'get_flow', label: 'Relire le flow sélectionné' },
    { name: 'list_workers', label: 'Lister les workers' },
    { name: 'list_executions', label: 'Consulter les exécutions récentes' },
    { name: 'get_libraries_overview', label: 'Consulter les bibliothèques' },
    { name: 'list_libraries', label: 'Lister les bibliothèques' },
    { name: 'list_worker_groups', label: 'Lister les groupes de workers' },
    { name: 'list_scripts', label: 'Lister les scripts' },
    { name: 'list_encoding_profiles', label: 'Lister les profils d’encodage' },
    { name: 'list_templates', label: 'Lister les modèles de flow' },
    { name: 'list_component_packs', label: 'Lister les packs de composants' },
    { name: 'list_plugins', label: 'Lister les plugins et intégrations' },
    { name: 'list_media_tools', label: 'Lister les outils disponibles' },
  ] as const;

  readonly resourceOptions = computed<readonly AssistantResourceOption[]>(() => {
    switch (this.selectedResourceType()) {
      case 'flow':
        return this.flows()
          .filter((flow) => !flow.isDraft)
          .map((flow) => ({
            type: 'flow',
            id: flow.id,
            label: `${flow.name} · v${flow.version}`,
            version: flow.version,
          }));
      case 'execution':
        return this.executions().map((item) => ({
          type: 'execution',
          id: item.id,
          label: `${item.flow} · ${item.state}`,
        }));
      case 'node':
        return this.nodes().map((item) => ({
          type: 'node',
          id: item.id,
          label: item.displayName || item.name,
        }));
      case 'library':
        return this.libraries().map((item) => ({
          type: 'library',
          id: item.id,
          label: item.name,
        }));
    }
  });

  readonly attachmentName = computed(
    () =>
      this.attachments()
        .map((file) => file.name)
        .join(', ') || null,
  );

  readonly hasFlowContext = computed(() => {
    const conversation = this.conversations.detail()?.conversation;
    return (
      this.selectedResource()?.type === 'flow' ||
      (conversation
        ? this.conversations
            .conversationSources(conversation)
            .some((source) => source.kind === 'flow')
        : false)
    );
  });

  loadFlows(): void {
    this.flowValidation.list().subscribe({
      next: (flows) => {
        this.flows.set(flows.filter((flow) => flow.version > 0));
        this.loadingFlows.set(false);
      },
      error: () => this.loadingFlows.set(false),
    });
  }

  loadExecutions(): void {
    this.executionTracking.overview({ limit: 100 }).subscribe({
      next: (overview) => {
        this.executions.set(overview.executions);
        this.loadingExecutions.set(false);
      },
      error: () => this.loadingExecutions.set(false),
    });
  }

  loadNodes(): void {
    this.clusterApi.listNodes().subscribe({
      next: (nodes) => {
        this.nodes.set(nodes);
        this.loadingNodes.set(false);
      },
      error: () => this.loadingNodes.set(false),
    });
  }

  loadLibraries(): void {
    this.clusterApi.listLibraries().subscribe({
      next: (libraries) => {
        this.libraries.set(libraries);
        this.loadingLibraries.set(false);
      },
      error: () => this.loadingLibraries.set(false),
    });
  }

  selectResourceType(type: AssistantResourceType): void {
    this.selectedResourceType.set(type);
    this.selectedResourceId.set('');
  }

  addResourceToContext(): void {
    const option = this.resourceOptions().find((item) => item.id === this.selectedResourceId());
    if (!option) return;
    const source: AiAssistantSource = {
      kind: option.type,
      resourceId: option.id,
      label: option.label,
      href: {
        flow: '/flows',
        execution: '/activity',
        node: '/workers',
        library: '/libraries',
      }[option.type],
      version: option.version,
    };
    const current = this.conversations.detail()?.conversation;
    const sources = current ? this.conversations.conversationSources(current) : [];
    if (
      sources.some((item) => item.kind === source.kind && item.resourceId === source.resourceId) ||
      (!current &&
        this.selectedResource()?.type === source.kind &&
        this.selectedResource()?.id === source.resourceId)
    ) {
      this.conversations.errorMessage.set('Cette ressource est déjà jointe à la conversation.');
      return;
    }
    if (sources.length + (this.selectedResource() ? 1 : 0) >= 5) {
      this.conversations.errorMessage.set('Vous pouvez joindre jusqu’à cinq ressources.');
      return;
    }
    if (current) this.updateConversationSources([...sources, source]);
    else this.selectedResource.set(option);
    this.selectedResourceId.set('');
    this.conversations.errorMessage.set(null);
  }

  removeSelectedResource(): void {
    this.selectedResource.set(null);
  }

  removeConversationSource(source: AiAssistantSource): void {
    const current = this.conversations.detail()?.conversation;
    if (
      !current ||
      this.updatingSources() ||
      this.conversations.hasPendingRequest() ||
      this.conversations.sending()
    )
      return;
    const sources = this.conversations.conversationSources(current);
    if (sources.length <= 1) return;
    this.updateConversationSources(sources.filter((candidate) => candidate !== source));
  }

  updateConversationSources(sources: readonly AiAssistantSource[]): void {
    const conversation = this.conversations.detail()?.conversation;
    if (!conversation) return;
    this.updatingSources.set(true);
    this.conversations.errorMessage.set(null);
    this.aiTools
      .updateAssistantSources(conversation.id, {
        sources: sources.map((source) => ({
          type: source.kind,
          id: source.resourceId,
          version: source.version,
        })),
      })
      .subscribe({
        next: (updated) => {
          this.conversations.detail.update((detail) =>
            detail ? { ...detail, conversation: updated } : detail,
          );
          this.updatingSources.set(false);
        },
        error: (error: HttpErrorResponse) => {
          this.updatingSources.set(false);
          this.conversations.errorMessage.set(
            this.conversations.errorText(
              error,
              'Impossible de modifier les ressources du contexte.',
            ),
          );
        },
      });
  }

  async selectAttachments(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const files = Array.from(input.files ?? []);
    input.value = '';
    for (const file of files) await this.selectAttachment(file);
  }

  async selectAttachment(file: File): Promise<void> {
    if (!this.isSupportedAssistantFile(file.name) || file.size > 8 * 1024 * 1024) {
      this.conversations.errorMessage.set(
        `« ${file.name} » n’est pas un format texte pris en charge ou dépasse 8 Mo.`,
      );
      return;
    }
    if (this.attachments().length >= 5) {
      this.conversations.errorMessage.set('Vous pouvez joindre jusqu’à cinq fichiers par message.');
      return;
    }
    try {
      const content = await file.text();
      if (this.attachments().some((item) => item.name === file.name)) {
        this.conversations.errorMessage.set(`« ${file.name} » est déjà joint.`);
        return;
      }
      const next = [...this.attachments(), { name: file.name, content }];
      const totalBytes = next.reduce(
        (sum, item) => sum + new TextEncoder().encode(item.content).length,
        0,
      );
      if (totalBytes > 16 * 1024 * 1024) {
        this.conversations.errorMessage.set('Les fichiers joints dépassent la limite cumulée de 16 Mo.');
        return;
      }
      if (next.reduce((sum, item) => sum + item.content.length, 0) > 1_000_000) {
        this.conversations.errorMessage.set(
          'Le contenu joint dépasse la capacité de contexte de 1 000 000 caractères.',
        );
        return;
      }
      this.attachments.set(next);
      this.conversations.errorMessage.set(null);
    } catch {
      this.conversations.errorMessage.set('Impossible de lire ce fichier.');
    }
  }

  removeAttachment(name?: string): void {
    this.attachments.update((files) => (name ? files.filter((file) => file.name !== name) : []));
  }

  isSupportedAssistantFile(name: string): boolean {
    const lower = name.toLocaleLowerCase();
    if (/^(dockerfile|makefile|\.env(?:\.[\w-]+)?)$/.test(lower)) return true;
    return /\.(txt|md|markdown|rst|csv|tsv|log|out|json|jsonc|json5|ya?ml|xml|toml|ini|conf|config|properties|srt|ass|ssa|vtt|sub|smi|sami|ttml|dfxp|mpl|lrc|sbv|ffmetadata|ffpreset|ffscript|ffconcat|filter|m3u8?|nfo|prx|avs|avsi|vpy|qpfile|chapters|edl|cue|xspf|pls|mpd|csproj|sln|props|targets|editorconfig|gitignore|cs|fs|vb|tsx?|jsx?|mjs|cjs|pyw?|sh|bash|zsh|ps1|psm1|bat|cmd|sql|html?|css|scss|less|proto|graphql|gql)$/i.test(
      lower,
    );
  }

  setSelectedReadTools(tools: readonly string[]): void {
    this.selectedReadTools.set([...new Set(tools)].slice(0, 3));
  }

  openWorkspace(): void {
    this.previewFlow.set(null);
    this.previewTitle.set('Espace de travail');
    this.previewLoading.set(false);
    this.previewError.set(null);
    this.previewOpen.set(true);
  }

  previewSource(source: AiAssistantSource): void {
    this.previewTitle.set(source.label);
    this.previewOpen.set(true);
    this.previewError.set(null);
    this.previewFlow.set(null);
    if (source.kind !== 'flow' || source.version == null) {
      this.previewLoading.set(false);
      this.previewError.set('L’aperçu visuel est disponible pour les flows publiés.');
      return;
    }
    this.previewLoading.set(true);
    this.flowValidation.versionStatus(source.resourceId, source.version).subscribe({
      next: (saved: FlowVersionResponse) => {
        this.previewFlow.set(saved.definition);
        this.previewLoading.set(false);
        void this.flowValidation
          .catalog()
          .then((catalog) =>
            this.previewComponents.set(
              catalog.flatMap((plugin) =>
                plugin.components.map((component) => ({
                  ...component,
                  pluginId: plugin.id,
                  pluginName: plugin.name,
                })),
              ),
            ),
          )
          .catch(() =>
            this.previewError.set('Les descriptions des blocs n’ont pas pu être chargées.'),
          );
      },
      error: () => {
        this.previewLoading.set(false);
        this.previewError.set(
          'Impossible de charger cet aperçu. Vérifiez que le flow est toujours disponible.',
        );
      },
    });
  }

  closePreview(): void {
    this.previewOpen.set(false);
    this.previewFlow.set(null);
    this.previewLoading.set(false);
    this.previewError.set(null);
  }

  hasDraftFor(source: AiAssistantSource): boolean {
    return (
      source.kind === 'flow' &&
      this.flows().some((flow) => flow.id === source.resourceId && flow.isDraft)
    );
  }

  canStartLibrary(source: AiAssistantSource): boolean {
    const library =
      source.kind === 'library'
        ? this.libraries().find((item) => item.id === source.resourceId)
        : null;
    return library?.isActive === true && library.flowName != null && source.version != null;
  }

  isCancelableExecution(source: AiAssistantSource): boolean {
    const execution =
      source.kind === 'execution'
        ? this.executions().find((item) => item.id === source.resourceId)
        : null;
    return execution != null && ['queued', 'running'].includes(execution.state);
  }

  deleteFlowFromAssistant(source: AiAssistantSource, isAdmin: boolean): void {
    if (
      !isAdmin ||
      source.kind !== 'flow' ||
      source.version === null ||
      source.version === undefined ||
      this.conversations.actionBusy()
    )
      return;
    const conversations = this.conversations;
    conversations.actionBusy.set(true);
    conversations.actionMessage.set(null);
    this.aiTools
      .prepareAssistantFlowDeletion({
        resourceId: source.resourceId,
        expectedVersion: source.version,
      })
      .subscribe({
        next: (prepared) => {
          if (!window.confirm(prepared.summary)) {
            conversations.actionBusy.set(false);
            return;
          }
          this.aiTools
            .confirmAssistantFlowDeletion({ confirmationToken: prepared.confirmationToken })
            .subscribe({
              next: () => {
                conversations.actionBusy.set(false);
                conversations.actionMessage.set(`Flow supprimé : ${source.label}.`);
                this.loadFlows();
              },
              error: (error: HttpErrorResponse) => {
                conversations.actionBusy.set(false);
                conversations.actionMessage.set(
                  conversations.errorText(
                    error,
                    'La suppression a échoué ou la version a changé. Actualisez le flow.',
                  ),
                );
              },
            });
        },
        error: (error: HttpErrorResponse) => {
          conversations.actionBusy.set(false);
          conversations.actionMessage.set(
            conversations.errorText(
              error,
              'La suppression du flow a échoué. Actualisez son état puis réessayez.',
            ),
          );
        },
      });
  }

  publishFlowFromAssistant(source: AiAssistantSource, isAdmin: boolean): void {
    if (!isAdmin || source.kind !== 'flow' || source.version == null || this.conversations.actionBusy())
      return;
    const conversations = this.conversations;
    conversations.actionBusy.set(true);
    conversations.actionMessage.set(null);
    this.aiTools
      .prepareAssistantFlowPublish({
        resourceId: source.resourceId,
        expectedVersion: source.version,
      })
      .subscribe({
        next: (prepared) => {
          if (!window.confirm(prepared.summary)) {
            conversations.actionBusy.set(false);
            return;
          }
          this.aiTools
            .confirmAssistantFlowPublish({ confirmationToken: prepared.confirmationToken })
            .subscribe({
              next: () => {
                conversations.actionBusy.set(false);
                conversations.actionMessage.set(`Brouillon publié pour ${source.label}.`);
                this.loadFlows();
              },
              error: (error: HttpErrorResponse) => {
                conversations.actionBusy.set(false);
                conversations.actionMessage.set(
                  conversations.errorText(
                    error,
                    'La publication a échoué ou le brouillon a changé. Actualisez le flow.',
                  ),
                );
              },
            });
        },
        error: (error: HttpErrorResponse) => {
          conversations.actionBusy.set(false);
          conversations.actionMessage.set(
            conversations.errorText(error, 'Aucun brouillon publiable pour ce flow.'),
          );
        },
      });
  }

  startLibraryFromAssistant(source: AiAssistantSource, isAdmin: boolean): void {
    if (!isAdmin || source.kind !== 'library' || source.version == null || this.conversations.actionBusy())
      return;
    const conversations = this.conversations;
    conversations.actionBusy.set(true);
    conversations.actionMessage.set(null);
    this.aiTools
      .prepareAssistantLibraryStart({
        resourceId: source.resourceId,
        expectedVersion: source.version,
      })
      .subscribe({
        next: (prepared) => {
          if (!window.confirm(prepared.summary)) {
            conversations.actionBusy.set(false);
            return;
          }
          this.aiTools
            .confirmAssistantLibraryStart({ confirmationToken: prepared.confirmationToken })
            .subscribe({
              next: (result) => {
                conversations.actionBusy.set(false);
                conversations.actionMessage.set(
                  `${result.queued} exécution(s) mise(s) en file, ${result.skipped} ignorée(s).`,
                );
                this.loadExecutions();
              },
              error: (error: HttpErrorResponse) => {
                conversations.actionBusy.set(false);
                conversations.actionMessage.set(
                  conversations.errorText(
                    error,
                    'La bibliothèque ou sa sélection a changé. Actualisez le contexte.',
                  ),
                );
              },
            });
        },
        error: (error: HttpErrorResponse) => {
          conversations.actionBusy.set(false);
          conversations.actionMessage.set(
            conversations.errorText(
              error,
              'Aucun traitement ne peut être lancé pour cette bibliothèque.',
            ),
          );
        },
      });
  }

  cancelExecutionFromAssistant(source: AiAssistantSource, isAdmin: boolean): void {
    if (!isAdmin || source.kind !== 'execution' || this.conversations.actionBusy()) return;
    const execution = this.executions().find((item) => item.id === source.resourceId);
    if (!execution || !['queued', 'running'].includes(execution.state)) return;
    const conversations = this.conversations;
    conversations.actionBusy.set(true);
    conversations.actionMessage.set(null);
    this.aiTools
      .prepareAssistantExecutionCancellation({
        resourceId: execution.id,
        expectedVersion: execution.revision,
      })
      .subscribe({
        next: (prepared) => {
          if (!window.confirm(prepared.summary)) {
            conversations.actionBusy.set(false);
            return;
          }
          this.aiTools
            .confirmAssistantExecutionCancellation({
              confirmationToken: prepared.confirmationToken,
            })
            .subscribe({
              next: () => {
                conversations.actionBusy.set(false);
                conversations.actionMessage.set(`Annulation demandée pour ${execution.media}.`);
                this.loadExecutions();
              },
              error: (error: HttpErrorResponse) => {
                conversations.actionBusy.set(false);
                conversations.actionMessage.set(
                  conversations.errorText(
                    error,
                    'L’exécution a changé ou ne peut plus être annulée. Actualisez son état.',
                  ),
                );
              },
            });
        },
        error: (error: HttpErrorResponse) => {
          conversations.actionBusy.set(false);
          conversations.actionMessage.set(
            conversations.errorText(
              error,
              'L’exécution a changé ou ne peut plus être annulée. Actualisez son état.',
            ),
          );
        },
      });
  }
}
