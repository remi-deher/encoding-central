import { IconComponent } from '../../shared/ui/icon.component';
import { BreakpointObserver } from '@angular/cdk/layout';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { map, Observable, Subscription } from 'rxjs';
import { MatChipsModule } from '@angular/material/chips';
import { MatMenuModule } from '@angular/material/menu';
import {
  AI_ASSISTANT_FILE_ACCEPT,
  AiConversationComposerComponent,
} from './components/ai-conversation-composer.component';
import { AiConversationHistoryComponent } from './components/ai-conversation-history.component';
import { aiEffortLabel, aiModelKey } from './components/ai-model-selection';
import { HttpErrorResponse } from '@angular/common/http';
import {
  afterNextRender,
  afterRenderEffect,
  effect,
  DestroyRef,
  ElementRef,
  viewChild,
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  OnDestroy,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatDialog } from '@angular/material/dialog';
import {
  AiNodeAvailability,
  AiAssistantConversation,
  AiAssistantConversationSearchResult,
  AiAssistantConversationDetail,
  AiAssistantActionProposal,
  AiAssistantSource,
  AiAssistantToolCall,
  AiAssistantOptions,
  AiAssistantFileAttachment,
  AiTurnModelSelection,
} from '../../shared/models/ai-tools';
import { ExecutionResponse } from '../../shared/models/execution';
import { LibraryResponse, NodeResponse } from '../../shared/models/cluster';
import {
  FlowDocument,
  FlowSummary,
  FlowVersionResponse,
} from '../../shared/models/flow-validation';
import { FlowComponentDefinition } from '../../shared/models/flow-component';
import { FlowPreviewComponent } from '../../shared/ui/flow-preview/flow-preview';
import { ConfirmationDialogComponent } from '../../shared/ui/confirmation-dialog.component';
import { AiToolsService } from '../../shared/services/ai-tools.service';
import { ClusterApi } from '../../shared/services/cluster-api';
import { ExecutionTracking } from '../../shared/services/execution-tracking';
import { FlowValidation } from '../../shared/services/flow-validation';
import { AuthService } from '../../core/auth/auth.service';
import { createUuid } from '../../shared/utils/uuid';
import { AssistantWorkspaceTemplateComponent } from '../../shared/templates/assistant-workspace-template.component';
import {
  PageShellContextService,
  PageShellSearchResult,
} from '../../core/layout/page-shell-context';

type AssistantResourceType = 'flow' | 'execution' | 'node' | 'library';
interface AssistantResourceOption {
  readonly type: AssistantResourceType;
  readonly id: string;
  readonly label: string;
  readonly version?: number;
}

@Component({
  selector: 'app-ai-assistant-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    IconComponent,
    FormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressSpinnerModule,
    MatSelectModule,
    AiConversationComposerComponent,
    AiConversationHistoryComponent,
    AssistantWorkspaceTemplateComponent,
    MatChipsModule,
    MatMenuModule,
    RouterLink,
    FlowPreviewComponent,
  ],
  templateUrl: './ai-assistant-page.component.html',
  styleUrl: './ai-assistant-page.component.scss',
  host: {
    '[class.assistant-panel-mode]': 'panelMode()',
    '[class.is-dragging-files]': 'draggingFiles()',
    '(dragenter)': 'onDragOver($event)',
    '(dragover)': 'onDragOver($event)',
    '(dragleave)': 'onDragLeave($event)',
    '(drop)': 'onDrop($event)',
  },
})
export class AiAssistantPageComponent implements OnDestroy {
  readonly panelMode = input(false);
  private readonly aiTools = inject(AiToolsService);
  private readonly dialog = inject(MatDialog);
  private readonly flowValidation = inject(FlowValidation);
  private readonly clusterApi = inject(ClusterApi);
  private readonly executionTracking = inject(ExecutionTracking);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  private readonly pageShellContext = inject(PageShellContextService);
  protected readonly compact = toSignal(
    inject(BreakpointObserver)
      .observe('(max-width: 760px)')
      .pipe(map((state) => state.matches)),
    {
      initialValue:
        typeof window !== 'undefined' && window.matchMedia('(max-width: 760px)').matches,
    },
  );
  protected readonly historyOpen = signal(!this.compact());
  protected readonly selectedResourceType = signal<AssistantResourceType>('flow');
  protected readonly selectedResourceId = signal('');
  protected readonly selectedResource = signal<AssistantResourceOption | null>(null);
  protected readonly resourceOptions = computed<readonly AssistantResourceOption[]>(() => {
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
  private readonly followTail = signal(true);
  private readonly messageThread = viewChild<ElementRef<HTMLElement>>('messageThread');
  protected readonly searchTarget = signal<{ turnIndex: number; role: 'user' | 'assistant' } | null>(null);
  protected readonly highlightedTurn = signal<{ turnIndex: number; role: 'user' | 'assistant' } | null>(null);
  protected readonly isAdmin = inject(AuthService).isAdmin;

  protected readonly flows = signal<readonly FlowSummary[]>([]);
  protected readonly executions = signal<readonly ExecutionResponse[]>([]);
  protected readonly nodes = signal<readonly NodeResponse[]>([]);
  protected readonly libraries = signal<readonly LibraryResponse[]>([]);
  protected readonly conversations = signal<readonly AiAssistantConversation[]>([]);
  protected readonly searchQuery = signal('');
  protected readonly searchingConversations = signal(false);
  private readonly assistantSearchResults = signal<readonly AiAssistantConversationSearchResult[]>([]);
  private readonly assistantSearchResultsQuery = signal('');
  protected readonly filteredConversations = computed(() => {
    const query = this.normalizeSearchText(this.searchQuery());
    if (query.length > 120) return [];
    const resultIds = new Set(
      this.assistantSearchResultsQuery() === query
        ? this.assistantSearchResults().map((result) => result.conversationId)
        : [],
    );
    return this.conversations().filter(
      (conversation) =>
        !query ||
        resultIds.has(conversation.id) ||
        [conversation.title, conversation.provider, conversation.source.label].some((value) =>
          this.normalizeSearchText(value).includes(query),
        ),
    );
  });
  protected readonly aiNodes = signal<readonly AiNodeAvailability[]>([]);
  protected readonly detail = signal<AiAssistantConversationDetail | null>(null);
  readonly conversationId = computed(() => this.detail()?.conversation.id ?? null);
  protected readonly assistantOptions = signal<AiAssistantOptions | null>(null);
  protected readonly selectedModelKey = signal('');
  protected readonly selectedReasoningEffort = signal<string | null>(null);
  protected readonly loadingAssistantOptions = signal(true);
  protected readonly prompt = signal('');
  protected readonly modelSettingsOpen = signal(false);
  protected readonly fileAccept = AI_ASSISTANT_FILE_ACCEPT;
  protected readonly draggingFiles = signal(false);
  protected readonly attachments = signal<readonly AiAssistantFileAttachment[]>([]);
  protected readonly attachmentName = computed(
    () =>
      this.attachments()
        .map((file) => file.name)
        .join(', ') || null,
  );
  protected readonly loadingFlows = signal(true);
  protected readonly loadingExecutions = signal(true);
  protected readonly loadingNodes = signal(true);
  protected readonly loadingLibraries = signal(true);
  protected readonly loadingConversations = signal(true);
  protected readonly deletingConversationId = signal<string | null>(null);
  protected readonly loadingDetail = signal(false);
  protected readonly sending = signal(false);
  protected readonly cancelling = signal(false);
  protected readonly selectedReadTools = signal<string[]>([]);
  protected readonly readTools = [
    { name: 'list_flows', label: 'Lister les flows' },
    { name: 'get_flow', label: 'Relire le flow sélectionné' },
    { name: 'list_nodes', label: 'Lister les nœuds' },
    { name: 'list_executions', label: 'Consulter les exécutions récentes' },
    { name: 'get_libraries_overview', label: 'Consulter les bibliothèques' },
    { name: 'list_libraries', label: 'Lister les bibliothèques' },
    { name: 'list_node_groups', label: 'Lister les groupes de nœuds' },
    { name: 'list_scripts', label: 'Lister les scripts' },
    { name: 'list_encoding_profiles', label: 'Lister les profils d’encodage' },
    { name: 'list_templates', label: 'Lister les modèles de flow' },
    { name: 'list_component_packs', label: 'Lister les packs de composants' },
    { name: 'list_plugins', label: 'Lister les plugins et intégrations' },
    { name: 'list_media_tools', label: 'Lister les outils disponibles' },
  ] as const;
  protected readonly errorMessage = signal<string | null>(null);
  protected readonly pendingPrompt = signal<string | null>(null);
  protected readonly hasPendingRequest = signal(false);
  protected readonly resumingRequest = signal(false);
  protected readonly updatingSources = signal(false);
  protected readonly actionBusy = signal(false);
  protected readonly actionMessage = signal<string | null>(null);
  protected readonly pendingActionProposal = signal<AiAssistantActionProposal | null>(null);
  protected readonly confirmingAiAction = signal(false);
  protected readonly requestProgress = signal<string | null>(null);
  protected readonly requestProgressDetail = signal<string | null>(null);
  protected readonly previewOpen = signal(false);
  protected readonly previewTitle = signal('Aperçu');
  protected readonly previewFlow = signal<FlowDocument | null>(null);
  protected readonly previewComponents = signal<readonly FlowComponentDefinition[]>([]);
  protected readonly previewLoading = signal(false);
  protected readonly previewError = signal<string | null>(null);
  private progressPollTimer: ReturnType<typeof setTimeout> | null = null;
  protected readonly hasFlowContext = computed(() => {
    const conversation = this.detail()?.conversation;
    return (
      this.selectedResource()?.type === 'flow' ||
      (conversation
        ? this.conversationSources(conversation).some((source) => source.kind === 'flow')
        : false)
    );
  });
  protected readonly selectedModelOption = computed(
    () =>
      this.assistantOptions()?.models.find(
        (option) => this.modelOptionKey(option) === this.selectedModelKey(),
      ) ?? null,
  );
  protected readonly reasoningEfforts = computed(
    () => this.selectedModelOption()?.reasoningEfforts ?? [],
  );
  protected readonly canSend = computed(
    () =>
      !this.sending() &&
      !this.loadingDetail() &&
      Boolean(this.prompt().trim()) &&
      this.prompt().trim().length <= 4000 &&
      (this.hasPendingRequest() ||
        (!this.loadingAssistantOptions() && Boolean(this.selectedModelOption()))) &&
      (!this.hasPendingRequest() || this.prompt() === this.requestDraftPrompt),
  );

  private requestPrompt = '';
  private requestDraftPrompt = '';
  private requestId = '';
  private requestSelection: AiTurnModelSelection | null = null;
  private pendingPollTimer: ReturnType<typeof setTimeout> | null = null;
  private optionsPollTimer: ReturnType<typeof setTimeout> | null = null;
  private searchTimer: ReturnType<typeof setTimeout> | null = null;
  private searchRequest: Subscription | null = null;

  ngOnDestroy(): void {
    if (this.pendingPollTimer !== null) clearTimeout(this.pendingPollTimer);
    this.stopProgressPolling();
    if (this.optionsPollTimer !== null) clearTimeout(this.optionsPollTimer);
    if (this.searchTimer !== null) clearTimeout(this.searchTimer);
    this.searchRequest?.unsubscribe();
  }

  constructor() {
    effect(() => this.historyOpen.set(!this.compact()));
    afterNextRender(() => {
      // Le même composant vit aussi dans l’assistant flottant : seul l’écran de route
      // possède le contexte de recherche global.
      if (this.panelMode()) return;
      this.destroyRef.onDestroy(
        this.pageShellContext.register({
          searchPlaceholder: 'Rechercher dans les conversations IA…',
          query: this.searchQuery,
          onSearchQueryChange: (query) => this.updateSearchQuery(query),
          searchResults: (query) => this.conversationSearchResults(query),
          actions: [
            {
              id: 'new-assistant-conversation',
              label: 'Nouvelle conversation',
              icon: 'add',
              run: () => {
                this.updateSearchQuery('');
                this.startNewConversation();
              },
            },
          ],
        }),
      );
    });
    this.loadFlows();
    this.loadExecutions();
    this.loadNodes();
    this.loadLibraries();
    this.loadConversations();
    this.loadAiNodes();
    this.loadAssistantOptions();
    afterNextRender(() => {
      if (this.panelMode()) return;
      this.route.queryParamMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((params) => {
        const id = params.get('conversation');
        if (!id) {
          if (this.detail()) this.detail.set(null);
          return;
        }
        if (this.detail()?.conversation.id !== id) this.openConversation(id, false);
      });
    });
    afterRenderEffect(() => {
      this.detail();
      this.pendingPrompt();
      this.sending();
      const target = this.searchTarget();
      const thread = this.messageThread()?.nativeElement;
      if (thread && target) {
        requestAnimationFrame(() => {
          const message = thread.querySelector<HTMLElement>(
            `[data-turn-index="${target.turnIndex}"][data-message-role="${target.role}"]`,
          );
          message?.scrollIntoView({ block: 'center', behavior: 'smooth' });
          this.highlightedTurn.set(target);
          this.followTail.set(false);
          this.searchTarget.set(null);
        });
        return;
      }
      if (thread && this.followTail()) thread.scrollTop = thread.scrollHeight;
    });
  }

  private conversationSearchResults(query: string): readonly PageShellSearchResult[] {
    const normalized = this.normalizeSearchText(query);
    if (normalized.length < 2 || normalized.length > 120) return [];
    const knownMatches = this.conversations()
      .filter((conversation) =>
        [conversation.title, conversation.provider, conversation.source.label].some((value) =>
          this.normalizeSearchText(value).includes(normalized),
        ),
      )
      .slice(0, 8);
    const results: PageShellSearchResult[] = knownMatches.map((conversation) => ({
      id: `assistant-conversation:${conversation.id}:conversation`,
      label: conversation.title,
      description: `${conversation.provider} · ${conversation.source.label}`,
      group: 'Conversations IA',
      glyph: '✦',
      run: () => this.openConversation(conversation.id),
    }));
    const resultIds = new Set(knownMatches.map((conversation) => conversation.id));
    if (this.assistantSearchResultsQuery() !== normalized) return results;
    for (const result of this.assistantSearchResults()) {
      if (resultIds.has(result.conversationId)) continue;
      results.push({
        id: `assistant-conversation:${result.conversationId}:${result.turnIndex ?? 'conversation'}`,
        label: result.title,
        description: result.snippet,
        group: 'Conversations IA',
        glyph: result.turnIndex === null ? '✦' : '❞',
        run: () =>
          this.openConversation(
            result.conversationId,
            true,
            result.turnIndex !== null && result.messageRole
              ? { turnIndex: result.turnIndex, role: result.messageRole }
              : null,
          ),
      });
    }
    return results.slice(0, 8);
  }

  private updateSearchQuery(query: string): void {
    this.searchQuery.set(query);
    const normalized = this.normalizeSearchText(query);
    this.assistantSearchResults.set([]);
    this.assistantSearchResultsQuery.set(normalized);
    this.searchingConversations.set(normalized.length >= 2 && normalized.length <= 120);
    if (this.searchTimer !== null) clearTimeout(this.searchTimer);
    this.searchRequest?.unsubscribe();
    this.searchRequest = null;
    if (normalized.length < 2 || normalized.length > 120) return;

    this.searchTimer = window.setTimeout(() => {
      this.searchRequest = this.aiTools.searchAssistantConversations(query.trim()).subscribe({
        next: (results) => {
          if (this.normalizeSearchText(this.searchQuery()) === normalized) {
            this.assistantSearchResults.set(results);
            this.searchingConversations.set(false);
          }
        },
        error: () => {
          if (this.normalizeSearchText(this.searchQuery()) === normalized) {
            this.assistantSearchResults.set([]);
            this.searchingConversations.set(false);
          }
        },
      });
    }, 240);
  }

  private normalizeSearchText(value: string): string {
    return value
      .normalize('NFD')
      .replace(/[\u0300-\u036f]/g, '')
      .toLocaleLowerCase('fr-FR')
      .trim();
  }

  protected onThreadScroll(): void {
    const thread = this.messageThread()?.nativeElement;
    if (thread)
      this.followTail.set(thread.scrollHeight - thread.clientHeight - thread.scrollTop < 80);
  }

  protected selectType(provider: string): void {
    if (this.sending() || this.hasPendingRequest()) return;
    const options =
      this.assistantOptions()?.models.filter((option) => option.provider === provider) ?? [];
    const selected = options.find((option) => option.isDefault) ?? options[0];
    if (selected) this.selectModel(this.modelOptionKey(selected));
  }

  protected startNewConversation(): void {
    if (this.sending()) return;
    this.closePreview();
    if (this.compact()) this.historyOpen.set(false);
    this.followTail.set(true);
    this.searchTarget.set(null);
    this.highlightedTurn.set(null);
    this.detail.set(null);
    this.selectedResource.set(null);
    this.errorMessage.set(null);
    this.pendingActionProposal.set(null);
    this.prompt.set('');
    this.removeAttachment();
    this.pendingPrompt.set(null);
    this.hasPendingRequest.set(false);
    this.resumingRequest.set(false);
    this.requestPrompt = '';
    this.requestDraftPrompt = '';
    this.requestId = '';
    this.requestSelection = null;
    this.selectedReasoningEffort.set(null);
    this.selectDefaultModel();
    if (this.panelMode()) return;
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { conversation: null },
      queryParamsHandling: 'merge',
    });
  }

  protected readonly modelOptionKey = aiModelKey;
  protected readonly effortLabel = aiEffortLabel;

  protected selectModel(key: string): void {
    this.selectedModelKey.set(key);
    this.selectedReasoningEffort.set(this.selectedModelOption()?.defaultReasoningEffort ?? null);
  }

  protected selectEffort(value: string | null): void {
    this.selectedReasoningEffort.set(value || null);
  }

  protected chooseResource(type: AssistantResourceType): void {
    this.selectResourceType(type);
    this.openWorkspace();
  }

  protected onDragOver(event: DragEvent): void {
    if (!event.dataTransfer?.types.includes('Files')) return;
    event.preventDefault();
    if (this.sending() || this.hasPendingRequest()) {
      event.dataTransfer.dropEffect = 'none';
      return;
    }
    this.draggingFiles.set(true);
  }

  protected onDragLeave(event: DragEvent): void {
    const host = event.currentTarget as HTMLElement;
    if (!event.relatedTarget || !host.contains(event.relatedTarget as Node)) {
      this.draggingFiles.set(false);
    }
  }

  protected async onDrop(event: DragEvent): Promise<void> {
    if (!event.dataTransfer?.types.includes('Files')) return;
    event.preventDefault();
    this.draggingFiles.set(false);
    if (this.sending() || this.hasPendingRequest()) return;
    for (const file of Array.from(event.dataTransfer.files)) await this.selectAttachment(file);
  }

  protected openWorkspace(): void {
    this.previewFlow.set(null);
    this.previewTitle.set('Espace de travail');
    this.previewLoading.set(false);
    this.previewError.set(null);
    this.previewOpen.set(true);
  }

  protected selectResourceType(type: AssistantResourceType): void {
    this.selectedResourceType.set(type);
    this.selectedResourceId.set('');
  }

  protected addResourceToContext(): void {
    const option = this.resourceOptions().find((item) => item.id === this.selectedResourceId());
    if (!option) return;
    const source: AiAssistantSource = {
      kind: option.type,
      resourceId: option.id,
      label: option.label,
      href: {
        flow: '/flows',
        execution: '/activity',
        node: '/nodes',
        library: '/libraries',
      }[option.type],
      version: option.version,
    };
    const current = this.detail()?.conversation;
    const sources = current ? this.conversationSources(current) : [];
    if (
      sources.some((item) => item.kind === source.kind && item.resourceId === source.resourceId) ||
      (!current &&
        this.selectedResource()?.type === source.kind &&
        this.selectedResource()?.id === source.resourceId)
    ) {
      this.errorMessage.set('Cette ressource est déjà jointe à la conversation.');
      return;
    }
    if (sources.length + (this.selectedResource() ? 1 : 0) >= 5) {
      this.errorMessage.set('Vous pouvez joindre jusqu’à cinq ressources.');
      return;
    }
    if (current) this.updateConversationSources([...sources, source]);
    else this.selectedResource.set(option);
    this.selectedResourceId.set('');
    this.errorMessage.set(null);
  }

  protected removeSelectedResource(): void {
    this.selectedResource.set(null);
  }

  protected async selectAttachments(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const files = Array.from(input.files ?? []);
    input.value = '';
    for (const file of files) await this.selectAttachment(file);
  }

  protected async selectAttachment(file: File): Promise<void> {
    if (!this.isSupportedAssistantFile(file.name) || file.size > 8 * 1024 * 1024) {
      this.errorMessage.set(
        `« ${file.name} » n’est pas un format texte pris en charge ou dépasse 8 Mo.`,
      );
      return;
    }
    if (this.attachments().length >= 5) {
      this.errorMessage.set('Vous pouvez joindre jusqu’à cinq fichiers par message.');
      return;
    }
    try {
      const content = await file.text();
      if (this.attachments().some((item) => item.name === file.name)) {
        this.errorMessage.set(`« ${file.name} » est déjà joint.`);
        return;
      }
      const next = [...this.attachments(), { name: file.name, content }];
      const totalBytes = next.reduce(
        (sum, item) => sum + new TextEncoder().encode(item.content).length,
        0,
      );
      if (totalBytes > 16 * 1024 * 1024) {
        this.errorMessage.set('Les fichiers joints dépassent la limite cumulée de 16 Mo.');
        return;
      }
      if (next.reduce((sum, item) => sum + item.content.length, 0) > 1_000_000) {
        this.errorMessage.set(
          'Le contenu joint dépasse la capacité de contexte de 1 000 000 caractères.',
        );
        return;
      }
      this.attachments.set(next);
      this.errorMessage.set(null);
    } catch {
      this.errorMessage.set('Impossible de lire ce fichier.');
    }
  }

  protected removeAttachment(name?: string): void {
    this.attachments.update((files) => (name ? files.filter((file) => file.name !== name) : []));
  }

  private isSupportedAssistantFile(name: string): boolean {
    const lower = name.toLocaleLowerCase();
    if (/^(dockerfile|makefile|\.env(?:\.[\w-]+)?)$/.test(lower)) return true;
    return /\.(txt|md|markdown|rst|csv|tsv|log|out|json|jsonc|json5|ya?ml|xml|toml|ini|conf|config|properties|srt|ass|ssa|vtt|sub|smi|sami|ttml|dfxp|mpl|lrc|sbv|ffmetadata|ffpreset|ffscript|ffconcat|filter|m3u8?|nfo|prx|avs|avsi|vpy|qpfile|chapters|edl|cue|xspf|pls|mpd|csproj|sln|props|targets|editorconfig|gitignore|cs|fs|vb|tsx?|jsx?|mjs|cjs|pyw?|sh|bash|zsh|ps1|psm1|bat|cmd|sql|html?|css|scss|less|proto|graphql|gql)$/i.test(
      lower,
    );
  }

  protected deleteFlowFromAssistant(source: AiAssistantSource): void {
    if (
      !this.isAdmin() ||
      source.kind !== 'flow' ||
      source.version === null ||
      source.version === undefined ||
      this.actionBusy()
    )
      return;
    this.actionBusy.set(true);
    this.actionMessage.set(null);
    this.aiTools
      .prepareAssistantFlowDeletion({
        resourceId: source.resourceId,
        expectedVersion: source.version,
      })
      .subscribe({
        next: (prepared) => {
          if (!window.confirm(prepared.summary)) {
            this.actionBusy.set(false);
            return;
          }
          this.aiTools
            .confirmAssistantFlowDeletion({ confirmationToken: prepared.confirmationToken })
            .subscribe({
              next: () => {
                this.actionBusy.set(false);
                this.actionMessage.set(`Flow supprimé : ${source.label}.`);
                this.loadFlows();
              },
              error: (error: HttpErrorResponse) => {
                this.actionBusy.set(false);
                this.actionMessage.set(
                  this.errorText(
                    error,
                    'La suppression a échoué ou la version a changé. Actualisez le flow.',
                  ),
                );
              },
            });
        },
        error: (error: HttpErrorResponse) => {
          this.actionBusy.set(false);
          this.actionMessage.set(
            this.errorText(
              error,
              'La suppression du flow a échoué. Actualisez son état puis réessayez.',
            ),
          );
        },
      });
  }

  protected hasDraftFor(source: AiAssistantSource): boolean {
    return (
      source.kind === 'flow' &&
      this.flows().some((flow) => flow.id === source.resourceId && flow.isDraft)
    );
  }

  protected canStartLibrary(source: AiAssistantSource): boolean {
    const library =
      source.kind === 'library'
        ? this.libraries().find((item) => item.id === source.resourceId)
        : null;
    return library?.isActive === true && library.flowName != null && source.version != null;
  }

  protected publishFlowFromAssistant(source: AiAssistantSource): void {
    if (!this.isAdmin() || source.kind !== 'flow' || source.version == null || this.actionBusy())
      return;
    this.actionBusy.set(true);
    this.actionMessage.set(null);
    this.aiTools
      .prepareAssistantFlowPublish({
        resourceId: source.resourceId,
        expectedVersion: source.version,
      })
      .subscribe({
        next: (prepared) => {
          if (!window.confirm(prepared.summary)) {
            this.actionBusy.set(false);
            return;
          }
          this.aiTools
            .confirmAssistantFlowPublish({ confirmationToken: prepared.confirmationToken })
            .subscribe({
              next: () => {
                this.actionBusy.set(false);
                this.actionMessage.set(`Brouillon publié pour ${source.label}.`);
                this.loadFlows();
              },
              error: (error: HttpErrorResponse) => {
                this.actionBusy.set(false);
                this.actionMessage.set(
                  this.errorText(
                    error,
                    'La publication a échoué ou le brouillon a changé. Actualisez le flow.',
                  ),
                );
              },
            });
        },
        error: (error: HttpErrorResponse) => {
          this.actionBusy.set(false);
          this.actionMessage.set(this.errorText(error, 'Aucun brouillon publiable pour ce flow.'));
        },
      });
  }

  protected startLibraryFromAssistant(source: AiAssistantSource): void {
    if (!this.isAdmin() || source.kind !== 'library' || source.version == null || this.actionBusy())
      return;
    this.actionBusy.set(true);
    this.actionMessage.set(null);
    this.aiTools
      .prepareAssistantLibraryStart({
        resourceId: source.resourceId,
        expectedVersion: source.version,
      })
      .subscribe({
        next: (prepared) => {
          if (!window.confirm(prepared.summary)) {
            this.actionBusy.set(false);
            return;
          }
          this.aiTools
            .confirmAssistantLibraryStart({ confirmationToken: prepared.confirmationToken })
            .subscribe({
              next: (result) => {
                this.actionBusy.set(false);
                this.actionMessage.set(
                  `${result.queued} exécution(s) mise(s) en file, ${result.skipped} ignorée(s).`,
                );
                this.loadExecutions();
              },
              error: (error: HttpErrorResponse) => {
                this.actionBusy.set(false);
                this.actionMessage.set(
                  this.errorText(
                    error,
                    'La bibliothèque ou sa sélection a changé. Actualisez le contexte.',
                  ),
                );
              },
            });
        },
        error: (error: HttpErrorResponse) => {
          this.actionBusy.set(false);
          this.actionMessage.set(
            this.errorText(error, 'Aucun traitement ne peut être lancé pour cette bibliothèque.'),
          );
        },
      });
  }

  protected cancelExecutionFromAssistant(source: AiAssistantSource): void {
    if (!this.isAdmin() || source.kind !== 'execution' || this.actionBusy()) return;
    const execution = this.executions().find((item) => item.id === source.resourceId);
    if (!execution || !['queued', 'running'].includes(execution.state)) return;
    this.actionBusy.set(true);
    this.actionMessage.set(null);
    this.aiTools
      .prepareAssistantExecutionCancellation({
        resourceId: execution.id,
        expectedVersion: execution.revision,
      })
      .subscribe({
        next: (prepared) => {
          if (!window.confirm(prepared.summary)) {
            this.actionBusy.set(false);
            return;
          }
          this.aiTools
            .confirmAssistantExecutionCancellation({
              confirmationToken: prepared.confirmationToken,
            })
            .subscribe({
              next: () => {
                this.actionBusy.set(false);
                this.actionMessage.set(`Annulation demandée pour ${execution.media}.`);
                this.loadExecutions();
              },
              error: (error: HttpErrorResponse) => {
                this.actionBusy.set(false);
                this.actionMessage.set(
                  this.errorText(
                    error,
                    'L’exécution a changé ou ne peut plus être annulée. Actualisez son état.',
                  ),
                );
              },
            });
        },
        error: (error: HttpErrorResponse) => {
          this.actionBusy.set(false);
          this.actionMessage.set(
            this.errorText(
              error,
              'L’exécution a changé ou ne peut plus être annulée. Actualisez son état.',
            ),
          );
        },
      });
  }

  protected isCancelableExecution(source: AiAssistantSource): boolean {
    const execution =
      source.kind === 'execution'
        ? this.executions().find((item) => item.id === source.resourceId)
        : null;
    return execution != null && ['queued', 'running'].includes(execution.state);
  }

  protected openConversation(
    id: string,
    updateUrl = true,
    target: { turnIndex: number; role: 'user' | 'assistant' } | null = null,
  ): void {
    if (this.loadingDetail()) return;
    this.closePreview();
    this.selectedResource.set(null);
    this.removeAttachment();
    this.pendingActionProposal.set(null);
    if (this.compact()) this.historyOpen.set(false);
    this.followTail.set(true);
    this.searchTarget.set(null);
    this.highlightedTurn.set(null);
    if (this.pendingPollTimer !== null) clearTimeout(this.pendingPollTimer);
    this.loadingDetail.set(true);
    this.errorMessage.set(null);
    this.aiTools.getAssistantConversation(id).subscribe({
      next: (detail) => {
        this.detail.set(detail);
        this.searchTarget.set(target);
        this.highlightedTurn.set(target);
        this.selectedModelKey.set(this.modelOptionKey(detail.conversation));
        this.selectedReasoningEffort.set(detail.conversation.reasoningEffort ?? null);
        this.requestSelection = null;
        if (detail.pendingTurn) {
          this.requestSelection = detail.conversation;
          this.prompt.set(detail.pendingTurn.userPrompt);
          this.requestPrompt = detail.pendingTurn.userPrompt;
          this.requestDraftPrompt = detail.pendingTurn.userPrompt;
          this.requestId = detail.pendingTurn.requestId;
          this.hasPendingRequest.set(true);
          this.resumingRequest.set(!detail.pendingTurn.isRunning);
          if (detail.pendingTurn.isRunning) {
            this.pendingPrompt.set(detail.pendingTurn.userPrompt);
            this.sending.set(true);
            this.pollPendingConversation(id, detail.pendingTurn.requestId);
            this.pollRequestProgress(detail.pendingTurn.requestId);
          } else {
            this.pendingPrompt.set(detail.pendingTurn.userPrompt);
            this.sending.set(false);
            this.errorMessage.set(
              'Une demande interrompue peut être reprise avec le même identifiant.',
            );
          }
        } else {
          this.requestPrompt = '';
          this.requestDraftPrompt = '';
          this.requestId = '';
          this.pendingPrompt.set(null);
          this.hasPendingRequest.set(false);
          this.resumingRequest.set(false);
          this.sending.set(false);
        }
        this.loadingDetail.set(false);
        if (updateUrl) this.setConversationUrl(id);
      },
      error: (error: HttpErrorResponse) => {
        this.loadingDetail.set(false);
        this.errorMessage.set(this.errorText(error, 'Cette conversation est indisponible.'));
      },
    });
  }

  protected confirmDeleteConversation(conversation: AiAssistantConversation): void {
    if (this.sending() || this.deletingConversationId()) return;
    this.dialog
      .open(ConfirmationDialogComponent, {
        data: {
          title: `Supprimer « ${conversation.title} » ?`,
          message: 'Cette conversation et son historique seront supprimés définitivement.',
          confirmLabel: 'Supprimer',
          variant: 'danger',
        },
      })
      .afterClosed()
      .subscribe((confirmed: boolean) => {
        if (confirmed) this.deleteConversation(conversation);
      });
  }

  private deleteConversation(conversation: AiAssistantConversation): void {
    if (this.deletingConversationId()) return;
    this.deletingConversationId.set(conversation.id);
    this.errorMessage.set(null);
    this.aiTools.deleteAssistantConversation(conversation.id).subscribe({
      next: () => {
        this.deletingConversationId.set(null);
        this.conversations.update((items) => items.filter((item) => item.id !== conversation.id));
        if (this.detail()?.conversation.id === conversation.id) this.startNewConversation();
        this.loadConversations();
      },
      error: (error: HttpErrorResponse) => {
        this.deletingConversationId.set(null);
        this.errorMessage.set(this.errorText(error, 'Impossible de supprimer cette conversation.'));
      },
    });
  }

  protected send(): void {
    const message = this.prompt().trim();
    if (!message || !this.canSend()) return;
    const outgoingPrompt =
      this.requestPrompt && this.hasPendingRequest() ? this.requestPrompt : message;
    if (this.requestDraftPrompt !== message || !this.requestId) {
      this.requestDraftPrompt = message;
      this.requestPrompt = outgoingPrompt;
      this.requestId = createUuid();
    }

    const activeConversation = this.detail()?.conversation ?? null;
    const flowId = activeConversation
      ? (this.conversationSources(activeConversation).find((source) => source.kind === 'flow')
          ?.resourceId ??
        (this.selectedResource()?.type === 'flow' ? this.selectedResource()?.id : null))
      : this.selectedResource()?.type === 'flow'
        ? (this.selectedResource()?.id ?? null)
        : null;
    const selectedModel = this.selectedModelOption();
    this.requestSelection ??= selectedModel
      ? {
          provider: selectedModel.provider,
          model: selectedModel.model,
          reasoningEffort: this.selectedReasoningEffort(),
          nodeId: selectedModel.nodeId,
        }
      : activeConversation;
    this.followTail.set(true);
    this.sending.set(true);
    this.cancelling.set(false);
    this.pendingPrompt.set(message);
    this.resumingRequest.set(false);
    this.errorMessage.set(null);
    this.requestProgress.set('preparing');
    this.requestProgressDetail.set('Préparation de votre demande');
    this.pollRequestProgress(this.requestId);
    this.aiTools
      .sendAssistantMessage({
        requestId: this.requestId,
        userPrompt: outgoingPrompt,
        conversationId: activeConversation?.id,
        resourceType: activeConversation ? undefined : this.selectedResource()?.type,
        resourceId: activeConversation ? undefined : this.selectedResource()?.id,
        resourceVersion: activeConversation ? undefined : this.selectedResource()?.version,
        toolCalls: this.selectedReadTools().map(
          (name) =>
            ({
              name,
              ...(name === 'get_flow' ? { flowId } : {}),
            }) as AiAssistantToolCall,
        ),
        provider: this.requestSelection?.provider,
        model: this.requestSelection?.model,
        reasoningEffort: this.requestSelection?.reasoningEffort,
        nodeId: this.requestSelection?.nodeId,
        attachments: this.attachments(),
      })
      .subscribe({
        next: (response) => {
          const oldTurns = this.detail()?.turns ?? [];
          const turns =
            response.success && response.content
              ? oldTurns.some((turn) => turn.requestId === response.requestId)
                ? oldTurns
                : [
                    ...oldTurns,
                    {
                      requestId: response.requestId,
                      userPrompt: message,
                      content: response.content,
                      sources: response.sources,
                      selection: response.selection,
                      actualModel: response.actualModel,
                      executorNodeId: response.executorNodeId,
                    },
                  ]
              : oldTurns;
          this.detail.set({
            conversation: response.conversation,
            turns,
            pendingTurn: response.success
              ? null
              : { requestId: response.requestId, userPrompt: message, isRunning: false },
          });
          this.pendingPrompt.set(null);
          this.sending.set(false);
          this.cancelling.set(false);
          this.stopProgressPolling();
          this.loadConversations();
          this.setConversationUrl(response.conversation.id);
          this.pendingActionProposal.set(
            response.success ? (response.actionProposal ?? null) : null,
          );
          if (response.success) {
            this.prompt.set('');
            this.removeAttachment();
            this.requestPrompt = '';
            this.requestDraftPrompt = '';
            this.requestId = '';
            this.requestSelection = null;
            this.hasPendingRequest.set(false);
            this.resumingRequest.set(false);
            this.selectedReadTools.set([]);
            this.selectedResource.set(null);
          } else {
            this.pendingPrompt.set(message);
            this.hasPendingRequest.set(true);
            this.resumingRequest.set(true);
            this.errorMessage.set(response.errorMessage ?? 'Le fournisseur IA n’a pas répondu.');
          }
        },
        error: (error: HttpErrorResponse) => {
          this.sending.set(false);
          this.cancelling.set(false);
          this.stopProgressPolling();
          this.pendingPrompt.set(null);
          this.errorMessage.set(
            this.errorText(error, 'Impossible d’envoyer le message. Réessayez.'),
          );
          if (error.status === 400 || error.status === 404) {
            this.hasPendingRequest.set(false);
            this.resumingRequest.set(false);
            this.requestId = '';
            this.requestPrompt = '';
            this.requestDraftPrompt = '';
            this.requestSelection = null;
            this.loadAssistantOptions();
          } else {
            this.pendingPrompt.set(message);
            this.hasPendingRequest.set(true);
            this.resumingRequest.set(true);
          }
        },
      });
  }

  protected setSelectedReadTools(tools: readonly string[]): void {
    this.selectedReadTools.set([...new Set(tools)].slice(0, 3));
  }

  protected cancelRequest(): void {
    if (!this.requestId || this.cancelling()) return;
    this.cancelling.set(true);
    this.aiTools.cancelAssistantRequest(this.requestId, this.conversationId()).subscribe({
      next: () => this.cancelling.set(false),
      error: (error: HttpErrorResponse) => {
        this.cancelling.set(false);
        this.errorMessage.set(
          this.errorText(error, 'La demande est terminée ou ne peut plus être interrompue.'),
        );
      },
    });
  }

  protected dismissAiAction(): void {
    this.pendingActionProposal.set(null);
  }

  protected confirmAiAction(): void {
    const proposal = this.pendingActionProposal();
    if (!proposal || this.confirmingAiAction() || !this.isAdmin()) return;
    this.confirmingAiAction.set(true);
    this.actionMessage.set(null);
    const request = { confirmationToken: proposal.confirmationToken };
    const confirmation: Observable<unknown> = (() => {
      switch (proposal.action) {
        case 'publish-flow':
          return this.aiTools.confirmAssistantFlowPublish(request);
        case 'delete-flow':
          return this.aiTools.confirmAssistantFlowDeletion(request);
        case 'start-library':
          return this.aiTools.confirmAssistantLibraryStart(request);
        case 'cancel-execution':
          return this.aiTools.confirmAssistantExecutionCancellation(request);
        case 'assign-library-flow':
          return this.aiTools.confirmAssistantLibraryFlowAssignment(request);
        case 'create-flow-template':
          return this.aiTools.confirmAssistantFlowTemplateCreation(request);
        case 'business-operation':
          return this.aiTools.confirmAssistantBusinessOperation(request);
        default:
          throw new Error('Action IA inconnue.');
      }
    })();
    confirmation.subscribe({
      next: (result) => {
        this.confirmingAiAction.set(false);
        this.pendingActionProposal.set(null);
        const detail =
          proposal.action === 'start-library'
            ? `${(result as { queued: number; skipped: number }).queued} tâche(s) mise(s) en file.`
            : proposal.action === 'cancel-execution'
              ? 'Annulation demandée.'
              : proposal.action === 'delete-flow'
                ? 'Flow supprimé.'
                : proposal.action === 'assign-library-flow'
                  ? 'Flow affecté à la bibliothèque.'
                  : proposal.action === 'create-flow-template'
                    ? 'Modèle de flow créé.'
                    : proposal.action === 'business-operation'
                      ? 'Configuration appliquée.'
                      : 'Flow publié.';
        this.actionMessage.set(detail);
        if (proposal.action === 'start-library') this.loadExecutions();
        if (proposal.action === 'delete-flow' || proposal.action === 'publish-flow')
          this.loadFlows();
        if (proposal.action === 'cancel-execution') this.loadExecutions();
        if (proposal.action === 'assign-library-flow') this.loadLibraries();
      },
      error: (error: HttpErrorResponse) => {
        this.confirmingAiAction.set(false);
        this.errorMessage.set(
          this.errorText(
            error,
            'L’action a expiré ou la ressource a changé. Demandez une nouvelle proposition.',
          ),
        );
      },
    });
  }

  protected previewSource(source: AiAssistantSource): void {
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

  protected closePreview(): void {
    this.previewOpen.set(false);
    this.previewFlow.set(null);
    this.previewLoading.set(false);
    this.previewError.set(null);
  }

  private pollRequestProgress(requestId: string): void {
    this.stopProgressPolling();
    const poll = () =>
      this.aiTools.getAssistantActivity(requestId).subscribe({
        next: (activity) => {
          if (activity) {
            this.requestProgress.set(activity.progress ?? activity.status);
            this.requestProgressDetail.set(
              activity.progressDetail ?? this.progressLabel(activity.progress ?? activity.status),
            );
          }
          if (this.sending() && this.requestId === requestId)
            this.progressPollTimer = window.setTimeout(poll, activity ? 900 : 500);
        },
        error: () => {
          if (this.sending() && this.requestId === requestId)
            this.progressPollTimer = window.setTimeout(poll, 1800);
        },
      });
    this.progressPollTimer = window.setTimeout(poll, 350);
  }

  private stopProgressPolling(): void {
    if (this.progressPollTimer !== null) clearTimeout(this.progressPollTimer);
    this.progressPollTimer = null;
  }

  private progressLabel(progress: string): string {
    switch (progress) {
      case 'context':
        return 'Recherche des informations utiles dans Encoding';
      case 'reading':
        return 'Consultation des ressources nécessaires';
      case 'generating':
        return 'Rédaction de la réponse';
      case 'completed':
        return 'Réponse prête';
      case 'cancelled':
        return 'Demande interrompue';
      case 'failed':
        return 'La demande a échoué';
      default:
        return 'Préparation de votre demande';
    }
  }

  private loadFlows(): void {
    this.flowValidation.list().subscribe({
      next: (flows) => {
        this.flows.set(flows.filter((flow) => flow.version > 0));
        this.loadingFlows.set(false);
      },
      error: () => this.loadingFlows.set(false),
    });
  }

  protected conversationSources(
    conversation: AiAssistantConversation,
  ): readonly AiAssistantSource[] {
    return [conversation.source, ...(conversation.additionalSources ?? [])];
  }

  protected visibleConversationSources(
    conversation: AiAssistantConversation,
  ): readonly AiAssistantSource[] {
    return this.conversationSources(conversation).filter((source) => source.kind !== 'application');
  }

  protected removeConversationSource(source: AiAssistantSource): void {
    const current = this.detail()?.conversation;
    if (!current || this.updatingSources() || this.hasPendingRequest() || this.sending()) return;
    const sources = this.conversationSources(current);
    if (sources.length <= 1) return;
    this.updateConversationSources(sources.filter((candidate) => candidate !== source));
  }

  private updateConversationSources(sources: readonly AiAssistantSource[]): void {
    const conversation = this.detail()?.conversation;
    if (!conversation) return;
    this.updatingSources.set(true);
    this.errorMessage.set(null);
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
          this.detail.update((detail) => (detail ? { ...detail, conversation: updated } : detail));
          this.updatingSources.set(false);
        },
        error: (error: HttpErrorResponse) => {
          this.updatingSources.set(false);
          this.errorMessage.set(
            this.errorText(error, 'Impossible de modifier les ressources du contexte.'),
          );
        },
      });
  }

  private loadExecutions(): void {
    this.executionTracking.overview({ limit: 100 }).subscribe({
      next: (overview) => {
        this.executions.set(overview.executions);
        this.loadingExecutions.set(false);
      },
      error: () => this.loadingExecutions.set(false),
    });
  }

  private loadNodes(): void {
    this.clusterApi.listNodes().subscribe({
      next: (nodes) => {
        this.nodes.set(nodes);
        this.loadingNodes.set(false);
      },
      error: () => this.loadingNodes.set(false),
    });
  }

  private loadLibraries(): void {
    this.clusterApi.listLibraries().subscribe({
      next: (libraries) => {
        this.libraries.set(libraries);
        this.loadingLibraries.set(false);
      },
      error: () => this.loadingLibraries.set(false),
    });
  }

  private loadAiNodes(): void {
    this.aiTools.getCluster().subscribe({
      next: (view) => this.aiNodes.set(view.nodes),
      error: () => this.aiNodes.set([]),
    });
  }

  /** Nœud qui coordonne la discussion, pour l'indication « Discussion du nœud … ». */
  protected coordinatorLabel(conversation: AiAssistantConversation): string | null {
    if (!conversation.coordinatorId) return null;
    const node = this.aiNodes().find(
      (candidate) => candidate.nodeId === conversation.coordinatorId,
    );
    return node ? `Discussion du nœud ${node.name}` : 'Discussion d’un autre nœud';
  }

  /** Vrai quand le coordinateur ne répond plus : le nœud courant reprendra la discussion au prochain message. */
  protected coordinatorLost(conversation: AiAssistantConversation): boolean {
    return this.aiNodes().some(
      (candidate) => candidate.nodeId === conversation.coordinatorId && !candidate.reachable,
    );
  }

  private loadConversations(): void {
    this.loadingConversations.set(true);
    this.aiTools.listAssistantConversations().subscribe({
      next: (conversations) => {
        this.conversations.set(conversations);
        this.loadingConversations.set(false);
      },
      error: () => this.loadingConversations.set(false),
    });
  }

  protected loadAssistantOptions(): void {
    if (this.optionsPollTimer !== null) clearTimeout(this.optionsPollTimer);
    this.optionsPollTimer = null;
    this.loadingAssistantOptions.set(true);
    this.aiTools.getAssistantOptions().subscribe({
      next: (options) => {
        this.assistantOptions.set(options);
        const catalogLoading =
          options.catalogs?.some((node) =>
            node.providers.some((catalog) => catalog.state === 'loading'),
          ) ?? false;
        this.loadingAssistantOptions.set(catalogLoading);
        if (!catalogLoading && !this.detail()) this.selectDefaultModel();
        if (catalogLoading)
          this.optionsPollTimer = window.setTimeout(() => this.loadAssistantOptions(), 750);
      },
      error: () => this.loadingAssistantOptions.set(false),
    });
  }

  private selectDefaultModel(): void {
    const options = this.assistantOptions();
    if (!options) return;
    const preferredModel = options.models.find(
      (option) =>
        option.provider === options.defaultProvider &&
        option.model === options.defaultModel &&
        (!options.defaultReasoningEffort ||
          option.reasoningEfforts.includes(options.defaultReasoningEffort)),
    );
    const selected =
      preferredModel ??
      options.models.find(
        (option) =>
          option.provider === options.defaultProvider && option.model === options.defaultModel,
      ) ??
      options.models.find((option) => option.provider === options.defaultProvider) ??
      options.models[0];
    this.selectedModelKey.set(selected ? this.modelOptionKey(selected) : '');
    this.selectedReasoningEffort.set(
      selected?.reasoningEfforts.includes(options.defaultReasoningEffort ?? '')
        ? (options.defaultReasoningEffort ?? null)
        : (selected?.defaultReasoningEffort ?? null),
    );
  }

  private setConversationUrl(id: string): void {
    if (this.panelMode()) return;
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { conversation: id },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  private pollPendingConversation(conversationId: string, requestId: string): void {
    this.pendingPollTimer = window.setTimeout(() => {
      this.pendingPollTimer = null;
      if (
        !this.detail() ||
        this.detail()?.conversation.id !== conversationId ||
        this.detail()?.pendingTurn?.requestId !== requestId
      )
        return;
      this.aiTools.getAssistantConversation(conversationId).subscribe({
        next: (detail) => {
          if (this.detail()?.conversation.id !== conversationId) return;
          this.detail.set(detail);
          if (detail.turns.some((turn) => turn.requestId === requestId)) {
            this.sending.set(false);
            this.pendingPrompt.set(null);
            this.hasPendingRequest.set(false);
            this.resumingRequest.set(false);
            this.prompt.set('');
            this.requestPrompt = '';
            this.requestDraftPrompt = '';
            this.requestId = '';
            this.requestSelection = null;
            this.loadConversations();
            return;
          }
          if (detail.pendingTurn?.requestId === requestId && detail.pendingTurn.isRunning) {
            this.pollPendingConversation(conversationId, requestId);
          } else {
            this.sending.set(false);
            this.pendingPrompt.set(detail.pendingTurn?.userPrompt ?? this.prompt());
            this.hasPendingRequest.set(true);
            this.resumingRequest.set(true);
            this.errorMessage.set(
              'La demande est interrompue ou son résultat reste incertain. Vous pouvez la reprendre.',
            );
          }
        },
        error: () => this.pollPendingConversation(conversationId, requestId),
      });
    }, 2000);
  }

  private errorText(error: HttpErrorResponse, fallback: string): string {
    const title: unknown = error.error?.title;
    if (typeof title === 'string' && title.trim()) return title;
    if (error.status === 0)
      return 'Connexion au serveur impossible. Vérifiez votre réseau puis réessayez.';
    if (error.status === 401) return 'Votre session a expiré. Reconnectez-vous pour continuer.';
    if (error.status === 403) return 'Vous n’avez pas les droits nécessaires pour cette action.';
    if (error.status === 408 || error.status === 504)
      return 'La demande a dépassé le délai prévu. Vous pouvez la reprendre après vérification de son état.';
    if (error.status === 429)
      return 'Trop de demandes sont en cours. Patientez un instant avant de réessayer.';
    if (error.status === 502 || error.status === 503)
      return 'Le service IA est momentanément indisponible. Vérifiez le fournisseur sélectionné ou réessayez plus tard.';
    if (error.status >= 500)
      return 'Le serveur n’a pas pu terminer la demande. Elle peut être reprise avec le même message.';
    return fallback;
  }
}
