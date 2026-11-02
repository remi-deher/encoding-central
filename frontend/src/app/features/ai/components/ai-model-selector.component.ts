import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { MatSelectModule } from '@angular/material/select';
import { MatSliderModule } from '@angular/material/slider';
import { MatTooltipModule } from '@angular/material/tooltip';
import { AiAssistantModelOption } from '../../../shared/models/ai-tools';
import { aiEffortLabel, aiModelKey, aiTypeLabel } from './ai-model-selection';

@Component({
  selector: 'app-ai-model-selector',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '(document:click)': 'closeOnOutsideClick($event)',
    '(document:keydown.escape)': 'closeSettings()',
  },
  imports: [MatSelectModule, MatSliderModule, MatTooltipModule],
  template: `
    <div class="model-control">
      <button
        class="model-button"
        type="button"
        aria-controls="assistant-model-settings"
        [attr.aria-expanded]="settingsOpen()"
        [disabled]="disabled()"
        [attr.aria-label]="
          'Modèle : ' + (selected()?.label ?? unavailableModel() ?? 'indisponible')
        "
        (click)="toggleSettings()"
      >
        <svg
          viewBox="0 0 24 24"
          width="17"
          height="17"
          aria-hidden="true"
          fill="none"
          stroke="currentColor"
          stroke-width="1.8"
          stroke-linecap="round"
        >
          <path d="M4 7h9m4 0h3M4 17h3m4 0h9" />
          <circle cx="15" cy="7" r="2" />
          <circle cx="9" cy="17" r="2" />
        </svg>
        <span class="model-current">{{
          selected()?.label ?? unavailableModel() ?? 'Indisponible'
        }}</span>
        <span class="model-effort">{{ selectedEffortLabel() }}</span>
        <svg
          class="model-chevron"
          [class.is-open]="settingsOpen()"
          viewBox="0 0 24 24"
          width="14"
          height="14"
          aria-hidden="true"
          fill="none"
          stroke="currentColor"
          stroke-width="2"
          stroke-linecap="round"
          stroke-linejoin="round"
        >
          <path d="m6 9 6 6 6-6" />
        </svg>
      </button>
      <section
        id="assistant-model-settings"
        class="settings-popover"
        [class.is-open]="settingsOpen()"
        role="region"
        aria-label="Réglages du fournisseur, du modèle et de l’effort"
        [attr.aria-hidden]="!settingsOpen()"
        [attr.inert]="!settingsOpen() ? '' : null"
      >
        <div class="popover-heading">
          <div>
            <strong>Niveau de réflexion</strong>
            <small>{{ selected()?.label ?? unavailableModel() ?? 'Modèle indisponible' }}</small>
          </div>
          <span class="effort-value">{{ selectedEffortLabel() }}</span>
        </div>
        <div class="selection-row">
          <mat-select
            class="provider-select"
            aria-label="Fournisseur IA"
            matTooltip="Fournisseur IA actif"
            [value]="provider()"
            [disabled]="disabled()"
            (selectionChange)="typeChange.emit($event.value)"
          >
            @if (provider() && !types().includes(provider())) {
              <mat-option [value]="provider()" disabled>{{ typeLabel(provider()) }}</mat-option>
            }
            @for (type of types(); track type) {
              <mat-option [value]="type">{{ typeLabel(type) }}</mat-option>
            }
          </mat-select>
          <mat-select
            class="model-select"
            aria-label="Modèle"
            matTooltip="Modèles disponibles pour le fournisseur sélectionné"
            placeholder="Modèle"
            [value]="selectedKey()"
            [disabled]="disabled() || !filteredModels().length"
            (selectionChange)="modelChange.emit($event.value)"
          >
            <mat-select-trigger>{{
              selected()?.label ?? unavailableModel() ?? 'Modèle indisponible'
            }}</mat-select-trigger>
            @if (!selected() && selectedKey()) {
              <mat-option [value]="selectedKey()" disabled>{{
                unavailableModel() ?? 'Modèle indisponible'
              }}</mat-option>
            }
            @for (option of filteredModels(); track key(option)) {
              <mat-option [value]="key(option)"
                >{{ option.label }}{{ option.nodeName ? ' · ' + option.nodeName : '' }}</mat-option
              >
            }
          </mat-select>
        </div>
        @if (selected()?.reasoningEfforts.length) {
          <section class="effort-control" aria-label="Niveau de réflexion">
            <div class="effort-heading">
              <div>
                <strong>Effort de raisonnement</strong
                ><small>Réglages disponibles pour ce modèle</small>
              </div>
            </div>
            <mat-slider
              class="effort-slider"
              min="0"
              [max]="reasoningEfforts().length"
              step="1"
              discrete
              showTickMarks
              [displayWith]="effortSliderLabel"
              [disabled]="disabled()"
            >
              <input
                matSliderThumb
                aria-label="Effort de raisonnement"
                [value]="sliderValue()"
                (valueChange)="selectEffortAt($event)"
              />
            </mat-slider>
            <div class="effort-labels" aria-hidden="true">
              <span>{{ effortLabel(reasoningEfforts()[0]) }}</span>
              <span>{{ effortLabel(reasoningEfforts()[reasoningEfforts().length - 1]) }}</span>
            </div>
          </section>
        } @else if (selected()) {
          <section class="effort-control effort-unavailable" aria-label="Niveau de réflexion">
            <strong>Niveau de réflexion</strong><small>{{ reasoningAvailabilityMessage() }}</small>
          </section>
        }
      </section>
    </div>
  `,
  styles: `
    :host {
      display: grid;
      position: relative;
      min-width: 0;
    }
    .model-control {
      position: relative;
      display: inline-flex;
      min-width: 0;
    }
    .model-button {
      display: inline-flex;
      align-items: center;
      gap: var(--space-2);
      min-width: 0;
      max-width: 100%;
      padding: var(--space-2) var(--space-3);
      border: 0;
      border-radius: var(--radius-pill);
      background: var(--surface-container-highest);
      color: var(--text-primary);
      font: inherit;
      cursor: pointer;
      transition:
        background 180ms var(--motion-spring-fast),
        border-radius 180ms var(--motion-spring-fast);
    }
    .model-button:hover {
      background: var(--surface-container-high);
    }
    .model-button:disabled {
      cursor: default;
      opacity: 0.6;
    }
    .model-current {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      color: var(--text-secondary);
    }
    .model-effort {
      flex: 0 0 auto;
      color: var(--text-muted);
      font-size: var(--font-label);
      white-space: nowrap;
    }
    @container (max-width: 420px) {
      .model-button { width: 100%; }
      .model-current { flex: 1 1 auto; text-align: left; }
    }
    .model-chevron {
      flex: 0 0 auto;
      transition: transform 180ms var(--motion-spring-fast);
    }
    .model-chevron.is-open {
      transform: rotate(180deg);
    }
    .settings-popover {
      position: absolute;
      z-index: 10;
      left: 0;
      bottom: calc(100% + var(--space-2));
      width: min(400px, calc(100cqw - 24px), calc(100vw - 40px));
      display: grid;
      gap: var(--space-3);
      padding: var(--space-4);
      border-radius: var(--radius-xl);
      background: var(--surface-container-high);
      box-shadow: var(--shadow-floating, 0 12px 36px rgb(0 0 0 / 28%));
      opacity: 0;
      visibility: hidden;
      pointer-events: none;
      transform: translateY(8px) scale(0.97);
      transform-origin: 18% 100%;
      transition:
        opacity 160ms var(--motion-spring-standard),
        transform 220ms var(--motion-spring-standard),
        visibility 220ms;
    }
    .settings-popover.is-open {
      opacity: 1;
      visibility: visible;
      pointer-events: auto;
      transform: translateY(0) scale(1);
    }
    .popover-heading {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-3);
    }
    .popover-heading strong,
    .popover-heading small {
      display: block;
    }
    .popover-heading strong {
      font-size: var(--font-label);
    }
    .popover-heading small {
      color: var(--text-muted);
      font-size: var(--font-label);
      margin-top: 2px;
    }
    .selection-row {
      display: grid;
      grid-template-columns: minmax(6.5rem, 0.8fr) minmax(8rem, 1.4fr);
      gap: var(--space-2);
      min-width: 0;
    }
    mat-select {
      width: 100%;
      min-width: 0;
      padding: var(--space-2) var(--space-3);
      border-radius: var(--radius-pill);
      background: var(--surface-container-low);
      font-size: var(--font-label);
    }
    mat-select:focus-visible {
      outline: 2px solid var(--accent-strong);
      outline-offset: 2px;
    }
    .effort-control {
      display: grid;
      gap: var(--space-1);
      min-width: 0;
      padding: var(--space-2) var(--space-3);
      border-radius: var(--radius-md);
      background: var(--surface-container-low);
    }
    .effort-heading {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-2);
      min-width: 0;
    }
    .effort-heading strong,
    .effort-heading small,
    .effort-unavailable strong,
    .effort-unavailable small {
      display: block;
    }
    .effort-heading strong,
    .effort-unavailable strong {
      font-size: var(--font-label);
      font-weight: 600;
    }
    .effort-heading small,
    .effort-unavailable small {
      margin-top: 2px;
      color: var(--text-muted);
      font-size: var(--font-label);
    }
    .effort-value {
      flex: 0 0 auto;
      padding: var(--space-1) var(--space-2);
      border-radius: var(--radius-pill);
      background: var(--surface-container-highest);
      font-size: var(--font-label);
    }
    .effort-slider {
      display: block;
      width: 100%;
      margin: 0;
    }
    .effort-labels {
      display: flex;
      justify-content: space-between;
      gap: var(--space-2);
      color: var(--text-muted);
      font-size: var(--font-label);
    }
    .effort-labels span:nth-child(2) {
      text-align: center;
    }
    .effort-labels span:last-child {
      text-align: right;
    }
    .effort-unavailable {
      min-height: 56px;
      align-content: center;
    }
    @media (prefers-reduced-motion: reduce) {
      .settings-popover,
      .model-button,
      .model-chevron {
        transition: none;
      }
    }
  `,
})
export class AiModelSelectorComponent {
  protected readonly settingsOpen = signal(false);
  readonly settingsOpenChange = output<boolean>();
  protected toggleSettings(): void {
    this.settingsOpen.update((open) => !open);
    this.settingsOpenChange.emit(this.settingsOpen());
  }
  protected closeSettings(): void {
    if (!this.settingsOpen()) return;
    this.settingsOpen.set(false);
    this.settingsOpenChange.emit(false);
  }
  protected closeOnOutsideClick(event: MouseEvent): void {
    if (
      this.settingsOpen() &&
      event.target instanceof Element &&
      !event.target.closest('app-ai-model-selector, .cdk-overlay-container')
    )
      this.closeSettings();
  }
  readonly options = input<readonly AiAssistantModelOption[]>([]);
  readonly selectedKey = input('');
  readonly fallbackProvider = input('');
  readonly unavailableModel = input<string | null>(null);
  readonly effort = input<string | null>(null);
  readonly disabled = input(false);
  readonly typeChange = output<string>();
  readonly modelChange = output<string>();
  readonly effortChange = output<string | null>();
  protected readonly key = aiModelKey;
  protected readonly typeLabel = aiTypeLabel;
  protected readonly effortLabel = aiEffortLabel;
  protected readonly selected = computed(
    () => this.options().find((option) => aiModelKey(option) === this.selectedKey()) ?? null,
  );
  protected readonly provider = computed(
    () =>
      this.selected()?.provider ?? (this.selectedKey().split('\n')[0] || this.fallbackProvider()),
  );
  protected readonly types = computed(() => [
    ...new Set(this.options().map((option) => option.provider)),
  ]);
  protected readonly filteredModels = computed(() =>
    this.options().filter((option) => option.provider === this.provider()),
  );
  protected readonly reasoningEfforts = computed(() => this.selected()?.reasoningEfforts ?? []);
  protected readonly sliderValue = computed(() => {
    const effort = this.effort();
    if (!effort) return 0;
    const index = this.reasoningEfforts().indexOf(effort);
    return index < 0 ? 0 : index + 1;
  });
  protected readonly selectedEffortLabel = computed(() =>
    this.effort() ? this.effortLabel(this.effort()!) : 'Automatique',
  );
  protected readonly reasoningAvailabilityMessage = computed(() =>
    this.selected()?.reasoningKnown
      ? 'Ce modèle ne propose pas de réglage d’effort.'
      : 'Les niveaux d’effort ne sont pas communiqués par le fournisseur.',
  );
  protected readonly effortSliderLabel = (value: number): string =>
    value === 0 ? 'Automatique' : this.effortLabel(this.reasoningEfforts()[value - 1] ?? '');

  protected selectEffortAt(value: number): void {
    this.effortChange.emit(value === 0 ? null : (this.reasoningEfforts()[value - 1] ?? null));
  }
}
