import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatChipsModule } from '@angular/material/chips';
import { ThemeToggleComponent } from '../../src/app/core/theme/theme-toggle.component';
import { StatusBadgeComponent } from '../../src/app/shared/ui/status-badge.component';
import { CategoryTabsComponent } from '../../src/app/shared/ui/category-tabs.component';
import { NotificationService } from '../../src/app/shared/services/notification.service';

@Component({
  selector: 'app-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatChipsModule,
    MatSlideToggleModule,
    ThemeToggleComponent,
    StatusBadgeComponent,
    CategoryTabsComponent,
  ],
  templateUrl: './mockup.html',
  styleUrl: './mockup.scss',
})
class AiMockup {
  readonly notice = inject(NotificationService);
  readonly tab = signal('assistant');
  readonly started = signal(false);
  readonly toolsMenuOpen = signal(false);
  readonly moreToolsOpen = signal(false);
  readonly providerMenuOpen = signal(false);
  readonly modelMenuOpen = signal(false);
  readonly reasoning = signal<string | null>('high');
  readonly resourcePickerOpen = signal(false);
  readonly selectedResource = signal('');
  readonly attachedResources = signal<string[]>(['flow-hevc']);
  readonly resourceGroups = [
    {
      label: 'Flows',
      resources: [
        { id: 'flow-hevc', label: 'Flow · HEVC · v3' },
        { id: 'flow-audio', label: 'Flow · Normalisation audio · v1' },
      ],
    },
    {
      label: 'Nœuds',
      resources: [
        { id: 'node-maison', label: 'Nœud · Maison' },
        { id: 'node-worker', label: 'Nœud · Worker GPU' },
      ],
    },
    { label: 'Bibliothèques', resources: [{ id: 'library-films', label: 'Bibliothèque · Films' }] },
    { label: 'Exécutions', resources: [{ id: 'execution-1042', label: 'Exécution · #1042' }] },
  ];
  resourceLabel(id: string) {
    return (
      this.resourceGroups.flatMap((group) => group.resources).find((resource) => resource.id === id)
        ?.label ?? id
    );
  }
  attachResource() {
    const id = this.selectedResource();
    if (!id || this.attachedResources().includes(id)) return;
    this.attachedResources.update((resources) => [...resources, id]);
    this.selectedResource.set('');
  }
  removeResource(id: string) {
    this.attachedResources.update((resources) => resources.filter((resource) => resource !== id));
  }
  readonly prompt = signal('');
  readonly messages = signal<string[]>([]);
  readonly model = signal('gpt-6.1');
  readonly provider = signal('codex-cli');
  readonly modelGroups = [
    {
      provider: 'codex-cli',
      label: 'ChatGPT',
      detail: 'Codex CLI',
      models: [
        { id: 'gpt-6.1', label: 'GPT 6.1', reasoningEfforts: ['low', 'medium', 'high', 'xhigh'], defaultReasoningEffort: 'high', reasoningKnown: true },
      ],
    },
    {
      provider: 'claude-cli',
      label: 'Claude',
      detail: 'Claude Code',
      models: [
        { id: 'claude-opus', label: 'Claude Opus', reasoningEfforts: ['low', 'medium', 'high'], defaultReasoningEffort: 'high', reasoningKnown: true },
        { id: 'claude-sonnet', label: 'Claude Sonnet', reasoningEfforts: ['low', 'medium', 'high'], defaultReasoningEffort: 'medium', reasoningKnown: true },
        { id: 'claude-haiku', label: 'Claude Haiku', reasoningEfforts: [], defaultReasoningEffort: null, reasoningKnown: true },
      ],
    },
    {
      provider: 'openai',
      label: 'OpenAI API',
      detail: 'API compatible',
      models: [
        { id: 'gpt-5', label: 'GPT-5', reasoningEfforts: ['low', 'medium', 'high'], defaultReasoningEffort: 'medium', reasoningKnown: true },
        { id: 'gpt-4.1', label: 'GPT-4.1', reasoningEfforts: [], defaultReasoningEffort: null, reasoningKnown: true },
      ],
    },
  ];
  readonly providerGroup = computed(() => this.modelGroups.find((group) => group.provider === this.provider()) ?? null);
  readonly providerModels = computed(() => this.providerGroup()?.models.filter((item) => !this.disabledModels().includes(item.id)) ?? []);
  readonly selectedModel = computed(() => this.providerModels().find((item) => item.id === this.model()) ?? null);
  readonly reasoningEfforts = computed(() => this.selectedModel()?.reasoningEfforts ?? []);
  readonly reasoningIndex = computed(() => Math.max(0, this.reasoningEfforts().indexOf(this.reasoning() ?? '')));
  readonly tabs = [
    { value: 'assistant', label: 'Assistant' },
    { value: 'providers', label: 'Fournisseurs' },
    { value: 'activity', label: 'Activité' },
  ];
  readonly providers = ['ChatGPT · Codex CLI', 'Claude', 'OpenAI API'];
  readonly disabledModels = signal<readonly string[]>([]);
  isEnabledModel(model: { id: string }): boolean {
    return !this.disabledModels().includes(model.id);
  }
  toggleModel(id: string, enabled: boolean) {
    this.disabledModels.update((ids) =>
      enabled ? ids.filter((item) => item !== id) : [...ids, id],
    );
    if (!enabled && this.model() === id) {
      const fallbackGroup = this.modelGroups.find((group) => group.models.some((item) => !this.disabledModels().includes(item.id)));
      const fallback = fallbackGroup?.models.find((item) => !this.disabledModels().includes(item.id));
      this.provider.set(fallbackGroup?.provider ?? '');
      this.model.set(fallback?.id ?? '');
      this.reasoning.set(fallback?.defaultReasoningEffort ?? null);
    }
  }
  send() {
    const message = this.prompt().trim();
    if (!message) return;
    this.messages.update((items) => [...items, message]);
    this.prompt.set('');
    this.started.set(true);
  }
  ask(prompt: string) {
    this.prompt.set(prompt);
    this.send();
  }
  openConversation() {
    this.messages.set(['Comment optimiser mon flow HEVC sans perdre le HDR ?']);
    this.started.set(true);
  }
  chooseProvider(id: string) {
    this.provider.set(id);
    const options = this.modelGroups.find((group) => group.provider === id)?.models.filter((item) => !this.disabledModels().includes(item.id)) ?? [];
    const model = options.find((item) => item.id === this.model()) ?? options[0];
    this.model.set(model?.id ?? '');
    this.reasoning.set(model?.defaultReasoningEffort ?? model?.reasoningEfforts.at(-1) ?? null);
    this.providerMenuOpen.set(false);
  }
  chooseModel(id: string) {
    this.model.set(id);
    const option = this.providerModels().find((item) => item.id === id);
    this.reasoning.set(option?.defaultReasoningEffort ?? option?.reasoningEfforts.at(-1) ?? null);
  }
  setReasoningIndex(index: number) {
    this.reasoning.set(this.reasoningEfforts()[index] ?? null);
  }
  effortLabel(effort: string | null) {
    return ({ low: 'Faible', minimal: 'Minimal', medium: 'Moyen', high: 'Élevé', xhigh: 'Très élevé', max: 'Maximum' } as Record<string, string>)[effort ?? ''] ?? 'Automatique';
  }
  providerLabel(provider: string) {
    return this.modelGroups.find((group) => group.provider === provider)?.label ?? provider;
  }
  chooseTool(tool: string) {
    this.toolsMenuOpen.set(false);
    this.moreToolsOpen.set(false);
    if (tool === 'resource') {
      this.resourcePickerOpen.set(true);
      return;
    }
    const prompts: Record<string, string> = {
      cluster: 'Analyse l’état actuel de mon cluster et signale les points à vérifier.',
      flow: 'Aide-moi à créer un flow adapté à ma bibliothèque.',
      execution: 'Résume la dernière exécution et explique les erreurs éventuelles.',
      file: 'Je souhaite importer un fichier pour l’analyser.',
    };
    if (tool === 'file') {
      this.notice.show('Import de fichier simulé dans cette maquette.');
      return;
    }
    this.prompt.set(prompts[tool] ?? '');
  }
  reset() {
    this.started.set(false);
    this.messages.set([]);
    this.prompt.set('');
  }
}
bootstrapApplication(AiMockup).catch(console.error);
