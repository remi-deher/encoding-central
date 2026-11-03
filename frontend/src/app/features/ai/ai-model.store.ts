import { computed, inject, Injectable, signal } from '@angular/core';
import { AiToolsService } from '../../shared/services/ai-tools.service';
import { AiAssistantOptions } from '../../shared/models/ai-tools';
import { aiEffortLabel, aiModelKey } from './components/ai-model-selection';
import { AiConversationStore } from './ai-conversation.store';

/**
 * Modèles et fournisseurs de l’assistant : options chargées depuis le backend,
 * sélection du modèle/raisonnement et condition d’envoi.
 */
@Injectable()
export class AiModelStore {
  private readonly aiTools = inject(AiToolsService);
  readonly conversations = inject(AiConversationStore);

  readonly assistantOptions = signal<AiAssistantOptions | null>(null);
  readonly selectedModelKey = signal('');
  readonly selectedReasoningEffort = signal<string | null>(null);
  readonly loadingAssistantOptions = signal(true);
  private optionsPollTimer: ReturnType<typeof setTimeout> | null = null;

  readonly modelOptionKey = aiModelKey;
  readonly effortLabel = aiEffortLabel;

  readonly selectedModelOption = computed(
    () =>
      this.assistantOptions()?.models.find(
        (option) => this.modelOptionKey(option) === this.selectedModelKey(),
      ) ?? null,
  );

  readonly reasoningEfforts = computed(
    () => this.selectedModelOption()?.reasoningEfforts ?? [],
  );

  readonly canSend = computed(
    () =>
      !this.conversations.sending() &&
      !this.conversations.loadingDetail() &&
      Boolean(this.conversations.prompt().trim()) &&
      this.conversations.prompt().trim().length <= 4000 &&
      (this.conversations.hasPendingRequest() ||
        (!this.loadingAssistantOptions() && Boolean(this.selectedModelOption()))) &&
      (!this.conversations.hasPendingRequest() ||
        this.conversations.prompt() === this.conversations.requestDraftPrompt()),
  );

  loadAssistantOptions(): void {
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
        if (!catalogLoading && !this.conversations.detail()) this.selectDefaultModel();
        if (catalogLoading)
          this.optionsPollTimer = window.setTimeout(() => this.loadAssistantOptions(), 750);
      },
      error: () => this.loadingAssistantOptions.set(false),
    });
  }

  selectDefaultModel(): void {
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

  selectModel(key: string): void {
    this.selectedModelKey.set(key);
    this.selectedReasoningEffort.set(this.selectedModelOption()?.defaultReasoningEffort ?? null);
  }

  selectEffort(value: string | null): void {
    this.selectedReasoningEffort.set(value || null);
  }

  selectType(provider: string): void {
    if (this.conversations.sending() || this.conversations.hasPendingRequest()) return;
    const options =
      this.assistantOptions()?.models.filter((option) => option.provider === provider) ?? [];
    const selected = options.find((option) => option.isDefault) ?? options[0];
    if (selected) this.selectModel(this.modelOptionKey(selected));
  }

  /** Applique la sélection lue depuis une conversation ouverte. */
  selectFromConversation(conversation: {
    provider: string;
    model?: string | null;
    reasoningEffort?: string | null;
  }): void {
    this.selectedModelKey.set(this.modelOptionKey(conversation as never));
    this.selectedReasoningEffort.set(conversation.reasoningEffort ?? null);
  }

  dispose(): void {
    if (this.optionsPollTimer !== null) clearTimeout(this.optionsPollTimer);
  }
}
