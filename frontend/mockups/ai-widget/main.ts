import { ChangeDetectionStrategy, Component, ElementRef, signal, viewChild } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { MatMenuModule } from '@angular/material/menu';

@Component({
  selector: 'app-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatSelectModule, MatMenuModule],
  templateUrl: './mockup.html',
  styleUrl: './mockup.scss',
  host: {
    '(document:click)': 'closeSettingsOnOutsideClick($event)',
    '(document:keydown.escape)': 'closeSettings()',
  },
})
class WidgetMockup {
  readonly text = signal('');
  readonly provider = signal('openai');
  readonly model = signal('gpt-6.1');
  readonly effortIndex = signal(1);
  readonly settingsOpen = signal(false);
  readonly resource = signal<string | null>(null);
  readonly fileName = signal<string | null>(null);
  readonly messages = signal<{ role: string; text: string }[]>([
    { role: 'Vous', text: 'Quels flows ont été modifiés récemment ?' },
    {
      role: 'Assistant',
      text: 'Le flow « HEVC + AAC/AC3 + ASS » a été mis à jour hier. Je peux aussi comparer ses dernières versions.',
    },
  ]);
  readonly providers = [
    { id: 'openai', label: 'OpenAI' },
    { id: 'anthropic', label: 'Anthropic' },
  ];
  // Fixture visuelle : en production, options et efforts viennent du backend.
  readonly models = [
    {
      provider: 'openai',
      model: 'gpt-6.1',
      label: 'GPT 6.1',
      reasoningEfforts: ['low', 'medium', 'high', 'max'],
    },
    {
      provider: 'openai',
      model: 'gpt-5-mini',
      label: 'GPT 5 mini',
      reasoningEfforts: ['low', 'medium', 'high'],
    },
    {
      provider: 'anthropic',
      model: 'claude-sonnet',
      label: 'Claude Sonnet',
      reasoningEfforts: ['low', 'medium', 'high'],
    },
    {
      provider: 'anthropic',
      model: 'claude-opus',
      label: 'Claude Opus',
      reasoningEfforts: ['medium', 'high', 'max'],
    },
  ];
  readonly input = viewChild<ElementRef<HTMLTextAreaElement>>('messageInput');
  readonly thread = viewChild<ElementRef<HTMLElement>>('thread');
  readonly modelControl = viewChild<ElementRef<HTMLElement>>('modelControl');

  closeSettingsOnOutsideClick(event: MouseEvent) {
    if (!this.settingsOpen()) return;
    const target = event.target;
    if (!(target instanceof Element) || target.closest('.cdk-overlay-container')) return;
    if (!this.modelControl()?.nativeElement.contains(target)) this.closeSettings();
  }

  filteredModels() {
    return this.models.filter((option) => option.provider === this.provider());
  }

  selectedModelOption() {
    return this.models.find((option) => option.model === this.model()) ?? this.filteredModels()[0];
  }

  efforts() {
    return this.selectedModelOption().reasoningEfforts;
  }

  selectedEffort() {
    return this.efforts()[this.effortIndex()] ?? this.efforts()[0];
  }

  selectProvider(value: string) {
    this.provider.set(value);
    const firstModel = this.models.find((option) => option.provider === value);
    if (!firstModel) return;
    this.model.set(firstModel.model);
    this.effortIndex.set(Math.min(1, firstModel.reasoningEfforts.length - 1));
  }

  selectModel(value: string) {
    this.model.set(value);
    this.effortIndex.set(Math.min(1, this.efforts().length - 1));
  }

  setEffortIndex(value: string) {
    this.effortIndex.set(Number(value));
  }

  resetEffort() {
    this.effortIndex.set(Math.min(1, this.efforts().length - 1));
  }

  closeSettings() {
    this.settingsOpen.set(false);
  }

  onFileSelected(event: Event) {
    const input = event.target as HTMLInputElement;
    this.fileName.set(input.files?.[0]?.name ?? null);
    input.value = '';
  }

  effortLabel(value: string) {
    return (
      ({ low: 'Faible', medium: 'Moyen', high: 'Élevé', max: 'Maximum' } as Record<string, string>)[
        value
      ] ?? value
    );
  }

  send() {
    const text = this.text().trim();
    if (!text) return;
    const attachmentNote = [this.resource(), this.fileName()].filter(Boolean).join(' · ');
    this.messages.update((messages) => [
      ...messages,
      { role: 'Vous', text },
      {
        role: 'Assistant',
        text: `Réponse de démonstration avec ${this.selectedModelOption().label} · effort ${this.effortLabel(this.selectedEffort())}.${attachmentNote ? ` Pièce jointe : ${attachmentNote}.` : ''}`,
      },
    ]);
    this.text.set('');
    this.fileName.set(null);
    this.resource.set(null);
    setTimeout(() => {
      const element = this.thread()?.nativeElement;
      element?.scrollTo({ top: element.scrollHeight });
      this.input()?.nativeElement.focus();
    });
  }

  keydown(event: KeyboardEvent) {
    if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
      event.preventDefault();
      this.send();
    }
  }
}
bootstrapApplication(WidgetMockup).catch(console.error);
