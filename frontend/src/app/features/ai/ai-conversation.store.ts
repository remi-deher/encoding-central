import { HttpErrorResponse } from '@angular/common/http';
import { computed, inject, Injectable, signal } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { Observable, Subscription } from 'rxjs';
import { AiToolsService } from '../../shared/services/ai-tools.service';
import { ConfirmationDialogComponent } from '../../shared/ui/confirmation-dialog.component';
import { PageShellSearchResult } from '../../core/layout/page-shell-context';
import {
  AiAssistantActionProposal,
  AiAssistantActivity,
  AiAssistantConversation,
  AiAssistantConversationDetail,
  AiAssistantConversationSearchResult,
  AiAssistantSource,
  AiAssistantToolCall,
} from '../../shared/models/ai-tools';
import { createUuid } from '../../shared/utils/uuid';

export type AssistantResourceType = 'flow' | 'execution' | 'node' | 'library';

/**
 * Conversations de l’assistant : liste, recherche, fil de messages, envoi,
 * reprise/annulation de demande, suppression et propositions d’action.
 * Le modèle choisi et le contexte (ressources, pièces jointes) vivent dans
 * {@link AiModelStore} et {@link AiContextStore}.
 */
@Injectable()
export class AiConversationStore {
  private readonly aiTools = inject(AiToolsService);
  private readonly dialog = inject(MatDialog);

  readonly conversations = signal<readonly AiAssistantConversation[]>([]);
  readonly searchQuery = signal('');
  readonly searchingConversations = signal(false);
  readonly assistantSearchResults = signal<readonly AiAssistantConversationSearchResult[]>([]);
  readonly assistantSearchResultsQuery = signal('');
  readonly detail = signal<AiAssistantConversationDetail | null>(null);
  readonly loadingDetail = signal(false);
  readonly deletingConversationId = signal<string | null>(null);
  readonly loadingConversations = signal(true);
  readonly sending = signal(false);
  readonly cancelling = signal(false);
  readonly prompt = signal('');
  readonly errorMessage = signal<string | null>(null);
  readonly pendingPrompt = signal<string | null>(null);
  readonly hasPendingRequest = signal(false);
  readonly resumingRequest = signal(false);
  readonly requestProgress = signal<string | null>(null);
  readonly requestProgressDetail = signal<string | null>(null);
  readonly pendingActionProposal = signal<AiAssistantActionProposal | null>(null);
  readonly confirmingAiAction = signal(false);
  readonly actionBusy = signal(false);
  readonly actionMessage = signal<string | null>(null);
  readonly followTail = signal(true);
  readonly searchTarget = signal<{ turnIndex: number; role: 'user' | 'assistant' } | null>(null);
  readonly highlightedTurn = signal<{ turnIndex: number; role: 'user' | 'assistant' } | null>(null);
  readonly requestPrompt = signal('');
  readonly requestDraftPrompt = signal('');
  readonly requestId = signal('');

  /** Synchronisation de l’URL (?conversation=) : assigné par la page (null en mode panneau). */
  urlSync: ((id: string | null) => void) | null = null;
  /** Ressources jointes au prochain message (store de contexte), pour l’envoi. */
  pendingResource: (() => {
    type: AssistantResourceType;
    id: string;
    version?: number;
  } | null) | null = null;
  /** Outils de lecture sélectionnés et pièce jointe courante (store de contexte). */
  pendingReadTools: (() => readonly string[]) | null = null;
  pendingAttachments: (() => readonly { name: string; content: string }[]) | null = null;
  /** Modèle/effort choisis (store de modèles). */
  pendingModelSelection: (() => {
    key: string;
    provider: string;
    model: string;
    reasoningEffort: string | null;
    workerId?: string | null;
  } | null) | null = null;
  /** Efface la pièce jointe et remet le prompt à zéro après un envoi réussi (store de contexte). */
  resetComposer: (() => void) | null = null;
  /** Recharge les listes dépendantes après une action (flows/exécutions/bibliothèques). */
  reloadAfterAction: ((action: string) => void) | null = null;
  /** Appelé quand la conversation ouverte vient d’être supprimée (page : nouvelle conversation). */
  deletedOpenConversation: (() => void) | null = null;
  /** Sélectionne le modèle par défaut quand les options sont prêtes. */
  onOptionsReady: (() => void) | null = null;

  private requestSelection: import('../../shared/models/ai-tools').AiTurnModelSelection | null = null;
  private pendingPollTimer: ReturnType<typeof setTimeout> | null = null;
  private progressPollTimer: ReturnType<typeof setTimeout> | null = null;
  private progressStream: Subscription | null = null;
  private searchTimer: ReturnType<typeof setTimeout> | null = null;
  private searchRequest: Subscription | null = null;

  readonly filteredConversations = computed(() => {
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

  readonly conversationId = computed(() => this.detail()?.conversation.id ?? null);

  normalizeSearchText(value: string): string {
    return value
      .normalize('NFD')
      .replace(/[\u0300-\u036f]/g, '')
      .toLocaleLowerCase('fr-FR')
      .trim();
  }

  conversationSearchResults(query: string): readonly PageShellSearchResult[] {
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

  updateSearchQuery(query: string): void {
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

  conversationSources(
    conversation: AiAssistantConversation,
  ): readonly AiAssistantSource[] {
    return [conversation.source, ...(conversation.additionalSources ?? [])];
  }

  visibleConversationSources(
    conversation: AiAssistantConversation,
  ): readonly AiAssistantSource[] {
    return this.conversationSources(conversation).filter((source) => source.kind !== 'application');
  }

  openConversation(
    id: string,
    updateUrl = true,
    target: { turnIndex: number; role: 'user' | 'assistant' } | null = null,
    beforeOpen: ((detail: AiAssistantConversationDetail) => void) | null = null,
  ): void {
    if (this.loadingDetail()) return;
    if (this.pendingPollTimer !== null) clearTimeout(this.pendingPollTimer);
    this.loadingDetail.set(true);
    this.errorMessage.set(null);
    this.aiTools.getAssistantConversation(id).subscribe({
      next: (detail) => {
        this.detail.set(detail);
        this.searchTarget.set(target);
        this.highlightedTurn.set(target);
        beforeOpen?.(detail);
        this.requestSelection = null;
        if (detail.pendingTurn) {
          this.requestSelection = detail.conversation;
          this.prompt.set(detail.pendingTurn.userPrompt);
          this.requestPrompt.set(detail.pendingTurn.userPrompt);
          this.requestDraftPrompt.set(detail.pendingTurn.userPrompt);
          this.requestId.set(detail.pendingTurn.requestId);
          this.hasPendingRequest.set(true);
          this.resumingRequest.set(!detail.pendingTurn.isRunning);
          if (detail.pendingTurn.isRunning) {
            this.pendingPrompt.set(detail.pendingTurn.userPrompt);
            this.sending.set(true);
            const pendingRequestId = detail.pendingTurn.requestId;
            // Le flux annonce la fin de la demande : on relit la conversation alors, avec un filet de 15 s si rien ne vient.
            this.pollPendingConversation(id, pendingRequestId, 15_000, 15_000);
            this.pollRequestProgress(pendingRequestId, {
              onEnded: () => this.pollPendingConversation(id, pendingRequestId, 300, 1_000),
              onFallback: () => this.pollPendingConversation(id, pendingRequestId, 2_000, 2_000),
            });
          } else {
            this.pendingPrompt.set(detail.pendingTurn.userPrompt);
            this.sending.set(false);
            this.errorMessage.set(
              'Une demande interrompue peut être reprise avec le même identifiant.',
            );
          }
        } else {
          this.requestPrompt.set('');
          this.requestDraftPrompt.set('');
          this.requestId.set('');
          this.pendingPrompt.set(null);
          this.hasPendingRequest.set(false);
          this.resumingRequest.set(false);
          this.sending.set(false);
        }
        this.loadingDetail.set(false);
        if (updateUrl) this.urlSync?.(id);
      },
      error: (error: HttpErrorResponse) => {
        this.loadingDetail.set(false);
        this.errorMessage.set(this.errorText(error, 'Cette conversation est indisponible.'));
      },
    });
  }

  confirmDeleteConversation(conversation: AiAssistantConversation): void {
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
        if (this.detail()?.conversation.id === conversation.id) this.deletedOpenConversation?.();
        this.loadConversations();
      },
      error: (error: HttpErrorResponse) => {
        this.deletingConversationId.set(null);
        this.errorMessage.set(this.errorText(error, 'Impossible de supprimer cette conversation.'));
      },
    });
  }

  send(): void {
    const message = this.prompt().trim();
    if (!message || !this.canSendCheck()) return;
    const outgoingPrompt =
      this.requestPrompt() && this.hasPendingRequest() ? this.requestPrompt() : message;
    if (this.requestDraftPrompt() !== message || !this.requestId()) {
      this.requestDraftPrompt.set(message);
      this.requestPrompt.set(outgoingPrompt);
      this.requestId.set(createUuid());
    }

    const activeConversation = this.detail()?.conversation ?? null;
    const resource = this.pendingResource?.() ?? null;
    const flowId = activeConversation
      ? (this.conversationSources(activeConversation).find((source) => source.kind === 'flow')
          ?.resourceId ??
        (resource?.type === 'flow' ? resource.id : null))
      : resource?.type === 'flow'
        ? resource.id
        : null;
    const selectedModel = this.pendingModelSelection?.();
    this.requestSelection ??= selectedModel
      ? {
          provider: selectedModel.provider,
          model: selectedModel.model,
          reasoningEffort: selectedModel.reasoningEffort,
          workerId: selectedModel.workerId,
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
    this.pollRequestProgress(this.requestId());
    this.aiTools
      .sendAssistantMessage({
        requestId: this.requestId(),
        userPrompt: outgoingPrompt,
        conversationId: activeConversation?.id,
        resourceType: activeConversation ? undefined : resource?.type,
        resourceId: activeConversation ? undefined : resource?.id,
        resourceVersion: activeConversation ? undefined : resource?.version,
        toolCalls: (this.pendingReadTools?.() ?? []).map(
          (name) =>
            ({
              name,
              ...(name === 'get_flow' ? { flowId } : {}),
            }) as AiAssistantToolCall,
        ),
        provider: this.requestSelection?.provider,
        model: this.requestSelection?.model,
        reasoningEffort: this.requestSelection?.reasoningEffort,
        workerId: this.requestSelection?.workerId,
        attachments: this.pendingAttachments?.() ?? [],
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
                      executorNodeId: response.executorWorkerId,
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
          this.urlSync?.(response.conversation.id);
          this.pendingActionProposal.set(
            response.success ? (response.actionProposal ?? null) : null,
          );
          if (response.success) {
            this.prompt.set('');
            this.resetComposer?.();
            this.requestPrompt.set('');
            this.requestDraftPrompt.set('');
            this.requestId.set('');
            this.requestSelection = null;
            this.hasPendingRequest.set(false);
            this.resumingRequest.set(false);
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
            this.requestId.set('');
            this.requestPrompt.set('');
            this.requestDraftPrompt.set('');
            this.requestSelection = null;
            this.onOptionsReady?.();
          } else {
            this.pendingPrompt.set(message);
            this.hasPendingRequest.set(true);
            this.resumingRequest.set(true);
          }
        },
      });
  }

  private canSendCheck(): boolean {
    // Le computed canSend du store de modèles est consulté via ce callback.
    return this.canSendProvider ? this.canSendProvider() : false;
  }

  /** Fourni par la page (computed canSend du store de modèles). */
  canSendProvider: (() => boolean) | null = null;

  cancelRequest(): void {
    if (!this.requestId() || this.cancelling()) return;
    this.cancelling.set(true);
    this.aiTools.cancelAssistantRequest(this.requestId(), this.conversationId()).subscribe({
      next: () => this.cancelling.set(false),
      error: (error: HttpErrorResponse) => {
        this.cancelling.set(false);
        this.errorMessage.set(
          this.errorText(error, 'La demande est terminée ou ne peut plus être interrompue.'),
        );
      },
    });
  }

  dismissAiAction(): void {
    this.pendingActionProposal.set(null);
  }

  confirmAiAction(isAdmin: boolean): void {
    const proposal = this.pendingActionProposal();
    if (!proposal || this.confirmingAiAction() || !isAdmin) return;
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
        this.reloadAfterAction?.(proposal.action);
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

  /**
   * Suit la progression par SSE ; l'interrogation d'autrefois ne sert que si le flux ne peut pas s'ouvrir.
   * `onEnded` est appelé quand le serveur annonce la fin de la demande, `onFallback` quand on retombe sur l'interrogation.
   */
  private pollRequestProgress(
    requestId: string,
    handlers: { onEnded?: () => void; onFallback?: () => void } = {},
  ): void {
    this.stopProgressPolling();
    this.progressStream = this.aiTools.streamAssistantActivity(requestId).subscribe({
      next: (activity) => this.showRequestProgress(activity),
      complete: () => handlers.onEnded?.(),
      error: () => {
        handlers.onFallback?.();
        this.pollRequestProgressByTimer(requestId);
      },
    });
  }

  private showRequestProgress(activity: AiAssistantActivity): void {
    this.requestProgress.set(activity.progress ?? activity.status);
    this.requestProgressDetail.set(
      activity.progressDetail ?? this.progressLabel(activity.progress ?? activity.status),
    );
  }

  private pollRequestProgressByTimer(requestId: string): void {
    const poll = () =>
      this.aiTools.getAssistantActivity(requestId).subscribe({
        next: (activity) => {
          if (activity) this.showRequestProgress(activity);
          if (this.sending() && this.requestId() === requestId)
            this.progressPollTimer = window.setTimeout(poll, activity ? 900 : 500);
        },
        error: () => {
          if (this.sending() && this.requestId() === requestId)
            this.progressPollTimer = window.setTimeout(poll, 1800);
        },
      });
    this.progressPollTimer = window.setTimeout(poll, 350);
  }

  private stopProgressPolling(): void {
    this.progressStream?.unsubscribe();
    this.progressStream = null;
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

  loadConversations(): void {
    this.loadingConversations.set(true);
    this.aiTools.listAssistantConversations().subscribe({
      next: (conversations) => {
        this.conversations.set(conversations);
        this.loadingConversations.set(false);
      },
      error: () => this.loadingConversations.set(false),
    });
  }

  private pollPendingConversation(
    conversationId: string,
    requestId: string,
    firstDelayMs: number,
    repeatDelayMs: number,
  ): void {
    if (this.pendingPollTimer !== null) clearTimeout(this.pendingPollTimer);
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
            this.requestPrompt.set('');
            this.requestDraftPrompt.set('');
            this.requestId.set('');
            this.requestSelection = null;
            this.loadConversations();
            return;
          }
          if (detail.pendingTurn?.requestId === requestId && detail.pendingTurn.isRunning) {
            this.pollPendingConversation(conversationId, requestId, repeatDelayMs, repeatDelayMs);
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
        error: () =>
          this.pollPendingConversation(conversationId, requestId, repeatDelayMs, repeatDelayMs),
      });
    }, firstDelayMs);
  }

  errorText(error: HttpErrorResponse, fallback: string): string {
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
      return 'Le serveur n’a pu terminer la demande. Elle peut être reprise avec le même message.';
    return fallback;
  }

  /** Libère les minuteurs (appelé par la page à sa destruction). */
  dispose(): void {
    if (this.pendingPollTimer !== null) clearTimeout(this.pendingPollTimer);
    this.stopProgressPolling();
    if (this.searchTimer !== null) clearTimeout(this.searchTimer);
    this.searchRequest?.unsubscribe();
  }
}
