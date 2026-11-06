import { BreakpointObserver } from '@angular/cdk/layout';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { map } from 'rxjs';
import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  effect,
  inject,
  input,
  OnDestroy,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatMenuModule } from '@angular/material/menu';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { AuthService } from '../../core/auth/auth.service';
import { PageShellContextService } from '../../core/layout/page-shell-context';
import { AssistantWorkspaceTemplateComponent } from '../../shared/templates/assistant-workspace-template.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';
import { AiConversationComposerComponent } from './components/ai-conversation-composer.component';
import { AiConversationHistoryComponent } from './components/ai-conversation-history.component';
import { AiChatThreadComponent } from './components/ai-chat-thread.component';
import { AiSourceBarComponent } from './components/ai-source-bar.component';
import { AiWorkspacePanelComponent } from './components/ai-workspace-panel.component';
import { AiConversationStore } from './ai-conversation.store';
import { AiModelStore } from './ai-model.store';
import { AiContextStore } from './ai-context.store';
import type { AssistantResourceType } from './ai-conversation.store';

/**
 * Page Assistant IA (route et panneau flottant `panelMode`) : historique,
 * espace de travail (contexte) et fil de conversation. L’état vit dans les
 * stores de la feature ; les vues sont les sous-composants du dossier
 * `components/`.
 */
@Component({
  selector: 'app-ai-assistant-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [AiConversationStore, AiModelStore, AiContextStore],
  imports: [
    IconComponent,
    PageHeaderComponent,
    FormsModule,
    MatButtonModule,
    MatChipsModule,
    MatFormFieldModule,
    MatInputModule,
    MatMenuModule,
    MatProgressSpinnerModule,
    MatSelectModule,
    RouterLink,
    AiConversationComposerComponent,
    AiConversationHistoryComponent,
    AiChatThreadComponent,
    AiSourceBarComponent,
    AiWorkspacePanelComponent,
    AssistantWorkspaceTemplateComponent,
  ],
  templateUrl: './ai-assistant-page.component.html',
  styleUrl: './ai-assistant-page.component.scss',
  host: {
    '[class.assistant-panel-mode]': 'panelMode()',
    '[class.is-dragging-files]': 'context.draggingFiles()',
    '(dragenter)': 'onDragOver($event)',
    '(dragover)': 'onDragOver($event)',
    '(dragleave)': 'onDragLeave($event)',
    '(drop)': 'onDrop($event)',
  },
})
export class AiAssistantPageComponent implements OnDestroy {
  readonly panelMode = input(false);
  readonly conversations = inject(AiConversationStore);
  readonly models = inject(AiModelStore);
  readonly context = inject(AiContextStore);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  private readonly pageShellContext = inject(PageShellContextService);
  protected readonly isAdmin = inject(AuthService).isAdmin;

  /** Identifiant de la conversation ouverte (lu aussi par l’hôte du panneau flottant). */
  readonly conversationId = this.conversations.conversationId;

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
  protected readonly modelSettingsOpen = signal(false);

  constructor() {
    // Câblage entre stores : la page fournit les ponts navigation/composer.
    this.conversations.urlSync = (id) => this.setConversationUrl(id);
    this.conversations.pendingResource = () => this.context.selectedResource();
    this.conversations.pendingReadTools = () => this.context.selectedReadTools();
    this.conversations.pendingAttachments = () => this.context.attachments();
    this.conversations.pendingModelSelection = () => {
      const option = this.models.selectedModelOption();
      return option
        ? {
            key: this.models.modelOptionKey(option),
            provider: option.provider,
            model: option.model ?? '',
            reasoningEffort: this.models.selectedReasoningEffort(),
            workerId: option.workerId,
          }
        : null;
    };
    this.conversations.canSendProvider = () => this.models.canSend();
    this.conversations.resetComposer = () => {
      this.context.removeAttachment();
      this.context.selectedReadTools.set([]);
      this.context.selectedResource.set(null);
    };
    this.conversations.reloadAfterAction = (action) => {
      if (action === 'start-library' || action === 'cancel-execution')
        this.context.loadExecutions();
      if (action === 'delete-flow' || action === 'publish-flow') this.context.loadFlows();
      if (action === 'assign-library-flow') this.context.loadLibraries();
    };
    this.conversations.onOptionsReady = () => this.models.loadAssistantOptions();
    this.conversations.deletedOpenConversation = () => this.startNewConversation();

    effect(() => this.historyOpen.set(!this.compact()));
    afterNextRender(() => {
      // Le même composant vit aussi dans l’assistant flottant : seul l’écran de route
      // possède le contexte de recherche global.
      if (this.panelMode()) return;
      this.destroyRef.onDestroy(
        this.pageShellContext.register({
          searchPlaceholder: 'Rechercher dans les conversations IA…',
          query: this.conversations.searchQuery,
          onSearchQueryChange: (query) => this.conversations.updateSearchQuery(query),
          searchResults: (query) => this.conversations.conversationSearchResults(query),
          actions: [
            {
              id: 'new-assistant-conversation',
              label: 'Nouvelle conversation',
              icon: 'add',
              run: () => {
                this.conversations.updateSearchQuery('');
                this.startNewConversation();
              },
            },
          ],
        }),
      );
    });
    this.context.loadFlows();
    this.context.loadExecutions();
    this.context.loadNodes();
    this.context.loadLibraries();
    this.conversations.loadConversations();
    this.models.loadAssistantOptions();
    afterNextRender(() => {
      if (this.panelMode()) return;
      this.route.queryParamMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((params) => {
        const id = params.get('conversation');
        if (!id) {
          if (this.conversations.detail()) this.conversations.detail.set(null);
          return;
        }
        if (this.conversations.detail()?.conversation.id !== id) this.openConversation(id, false);
      });
    });
  }

  ngOnDestroy(): void {
    this.conversations.dispose();
    this.models.dispose();
  }

  protected startNewConversation(): void {
    if (this.conversations.sending()) return;
    this.context.closePreview();
    if (this.compact()) this.historyOpen.set(false);
    this.conversations.followTail.set(true);
    this.conversations.searchTarget.set(null);
    this.conversations.highlightedTurn.set(null);
    this.conversations.detail.set(null);
    this.context.selectedResource.set(null);
    this.conversations.errorMessage.set(null);
    this.conversations.pendingActionProposal.set(null);
    this.conversations.prompt.set('');
    this.context.removeAttachment();
    this.conversations.pendingPrompt.set(null);
    this.conversations.hasPendingRequest.set(false);
    this.conversations.resumingRequest.set(false);
    this.conversations.requestPrompt.set('');
    this.conversations.requestDraftPrompt.set('');
    this.conversations.requestId.set('');
    this.models.selectedReasoningEffort.set(null);
    this.models.selectDefaultModel();
    if (this.panelMode()) return;
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { conversation: null },
      queryParamsHandling: 'merge',
    });
  }

  protected openConversation(
    id: string,
    updateUrl = true,
    target: { turnIndex: number; role: 'user' | 'assistant' } | null = null,
  ): void {
    this.context.closePreview();
    this.context.selectedResource.set(null);
    this.context.removeAttachment();
    this.conversations.pendingActionProposal.set(null);
    if (this.compact()) this.historyOpen.set(false);
    this.conversations.followTail.set(true);
    this.conversations.searchTarget.set(null);
    this.conversations.highlightedTurn.set(null);
    this.conversations.openConversation(id, updateUrl, target, (detail) => {
      this.models.selectFromConversation(detail.conversation);
    });
  }

  protected chooseResource(type: AssistantResourceType): void {
    this.context.selectResourceType(type);
    this.context.openWorkspace();
  }

  protected onDragOver(event: DragEvent): void {
    if (!event.dataTransfer?.types.includes('Files')) return;
    event.preventDefault();
    if (this.conversations.sending() || this.conversations.hasPendingRequest()) {
      event.dataTransfer.dropEffect = 'none';
      return;
    }
    this.context.draggingFiles.set(true);
  }

  protected onDragLeave(event: DragEvent): void {
    const host = event.currentTarget as HTMLElement;
    if (!event.relatedTarget || !host.contains(event.relatedTarget as Node)) {
      this.context.draggingFiles.set(false);
    }
  }

  protected async onDrop(event: DragEvent): Promise<void> {
    if (!event.dataTransfer?.types.includes('Files')) return;
    event.preventDefault();
    this.context.draggingFiles.set(false);
    if (this.conversations.sending() || this.conversations.hasPendingRequest()) return;
    for (const file of Array.from(event.dataTransfer.files))
      await this.context.selectAttachment(file);
  }

  private setConversationUrl(id: string | null): void {
    if (this.panelMode()) return;
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { conversation: id },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }
}
