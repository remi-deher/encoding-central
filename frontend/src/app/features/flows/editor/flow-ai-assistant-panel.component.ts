import { FlowPreviewComponent } from '../../../shared/ui/flow-preview/flow-preview';
import { FlowComponentDefinition } from '../../../shared/models/flow-component';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  signal,
  input,
  output,
  effect,
  untracked,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { AiToolsService } from '../../../shared/services/ai-tools.service';
import { FlowDocument } from '../../../shared/models/flow-validation';
import { FlowSimulation } from '../../../shared/services/flow-simulation';
import { NotificationService } from '../../../shared/services/notification.service';
import { createUuid } from '../../../shared/utils/uuid';

export interface FlowAiAssistantPanelData {
  readonly mediaType: string;
  readonly currentFlow?: FlowDocument | null;
  readonly label?: string;
  readonly initialPrompt?: string;
  readonly components?: readonly FlowComponentDefinition[];
}

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-flow-ai-assistant-panel',
  providers: [FlowSimulation],
  imports: [
    CommonModule,
    FlowPreviewComponent,
    FormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressSpinnerModule,
  ],
  template: `
    <section class="app-dialog flow-ai-dialog">
      <header class="app-dialog-header">
        <span class="assistant-mark" aria-hidden="true">
          <svg viewBox="0 0 24 24" fill="none">
            <path d="m12 2 1.55 6.45L20 10l-6.45 1.55L12 18l-1.55-6.45L4 10l6.45-1.55L12 2Z" />
            <path d="m19 16 .7 2.3L22 19l-2.3.7L19 22l-.7-2.3L16 19l2.3-.7L19 16Z" />
          </svg>
        </span>
        <div class="app-dialog-heading">
          <p class="app-dialog-eyebrow">Assistant de flow</p>
          <h2>Assistant de flow</h2>
          <p class="dialog-description">
            @if (mode() === 'generate') {
              Décrivez le résultat attendu et l’assistant préparera un flow adapté à votre média.
            } @else if (mode() === 'refactor') {
              Décrivez les changements souhaités pour faire évoluer le flow actuel.
            } @else {
              Demandez une synthèse des étapes, des conditions et des connexions de ce flow.
            }
          </p>
        </div>
        <button
          class="dialog-close"
          type="button"
          aria-label="Fermer l’assistant"
          (click)="close()"
        >
          <span aria-hidden="true">×</span>
        </button>
      </header>

      <div class="app-dialog-body flow-ai-body">
        <p role="status">{{ data().label || 'Flow complet' }}</p>
        @if (data().currentFlow) {
          <div class="mode-selector" role="group" aria-label="Action de l’assistant">
            <button
              class="mode-card"
              [class.selected]="mode() === 'generate'"
              [attr.aria-pressed]="mode() === 'generate'"
              type="button"
              (click)="setMode('generate')"
            >
              <span class="mode-icon" aria-hidden="true">
                <svg viewBox="0 0 24 24" fill="none">
                  <rect x="4" y="4" width="16" height="16" rx="3" />
                  <path d="M12 8v8M8 12h8" />
                </svg>
              </span>
              <span class="mode-copy">
                <strong>Nouveau flow</strong>
                <span>Créer un traitement à partir d’une consigne</span>
              </span>
              <span class="mode-indicator" aria-hidden="true"></span>
            </button>
            <button
              class="mode-card"
              [class.selected]="mode() === 'refactor'"
              [attr.aria-pressed]="mode() === 'refactor'"
              type="button"
              (click)="setMode('refactor')"
            >
              <span class="mode-icon" aria-hidden="true">
                <svg viewBox="0 0 24 24" fill="none">
                  <path d="M4 19.5V20h.5l11.2-11.2-1.5-1.5L3 18.5v1Z" />
                  <path
                    d="m17.2 4.8 2 2a1.4 1.4 0 0 1 0 2l-1.5 1.5-4-4L15.2 4.8a1.4 1.4 0 0 1 2 0Z"
                  />
                  <path d="M4 4h7M4 8h4M4 12h2" />
                </svg>
              </span>
              <span class="mode-copy">
                <strong>Modifier ce flow</strong>
                <span>Ajuster les étapes du flow existant</span>
              </span>
              <span class="mode-indicator" aria-hidden="true"></span>
            </button>
            <button
              class="mode-card"
              [class.selected]="mode() === 'explain'"
              [attr.aria-pressed]="mode() === 'explain'"
              type="button"
              (click)="setMode('explain')"
            >
              <span class="mode-icon" aria-hidden="true">
                <svg viewBox="0 0 24 24" fill="none">
                  <circle cx="12" cy="12" r="9" />
                  <path d="M9.8 9a2.3 2.3 0 1 1 3.9 1.7c-1.2 1-1.7 1.4-1.7 2.8M12 17h.01" />
                </svg>
              </span>
              <span class="mode-copy">
                <strong>Expliquer ce flow</strong>
                <span>Comprendre les étapes et leurs liens</span>
              </span>
              <span class="mode-indicator" aria-hidden="true"></span>
            </button>
          </div>
        }

        @if (mode() !== 'explain') {
          <div class="suggestions-container">
            <div class="suggestions-heading">
              <span class="suggestions-title">Pour commencer</span>
              <span class="suggestions-hint">Choisissez un exemple ou écrivez votre consigne</span>
            </div>
            <div class="suggestions-list" role="group" aria-label="Exemples de consignes">
              @for (suggestion of suggestions; track suggestion) {
                <button type="button" class="suggestion-chip" (click)="applySuggestion(suggestion)">
                  {{ suggestion }}
                </button>
              }
            </div>
          </div>

          <mat-form-field appearance="outline" class="prompt-field">
            <mat-label>Que doit faire votre flow ?</mat-label>
            <textarea
              matInput
              rows="5"
              [ngModel]="prompt()"
              (ngModelChange)="prompt.set($event)"
              placeholder="Ex. Convertir les vidéos en HEVC, normaliser l’audio en AAC stéréo et conserver les sous-titres français."
            ></textarea>
            <mat-hint>Précisez les codecs, langues ou conditions importantes pour vous.</mat-hint>
          </mat-form-field>
        } @else {
          <div class="explain-card">
            <span class="explain-icon" aria-hidden="true">i</span>
            <p>
              L’assistant analysera les blocs, les conditions et les connexions pour vous présenter
              le parcours du flow dans un langage clair.
            </p>
          </div>
        }

        @if (explanation()) {
          <div class="explanation-box">
            <h4>Explication du flow :</h4>
            <p>{{ explanation() }}</p>
          </div>
        }

        @if (proposal(); as proposed) {
          <section class="proposal-box" aria-labelledby="proposal-title">
            <h3 id="proposal-title">Proposition à examiner</h3>
            <p>
              @if (data().currentFlow) {
                {{ proposalChanges().addedNodes.length }} bloc(s) ajouté(s),
                {{ proposalChanges().changedNodes.length }} modifié(s),
                {{ proposalChanges().removedNodes.length }} supprimé(s),
                {{ proposalChanges().addedConnections.length }} connexion(s) ajoutée(s) et
                {{ proposalChanges().removedConnections.length }} supprimée(s).
                @if (proposalChanges().metadataChanged) {
                  Le nom ou le média du flow change.
                }
                @if (
                  proposalChanges().addedNodes.length ||
                  proposalChanges().changedNodes.length ||
                  proposalChanges().removedNodes.length
                ) {
                  <ul aria-label="Blocs modifiés">
                    @for (node of proposalChanges().addedNodes; track node) {
                      <li>Ajout : {{ node }}</li>
                    }
                    @for (node of proposalChanges().changedNodes; track node) {
                      <li>Modification : {{ node }}</li>
                    }
                    @for (node of proposalChanges().removedNodes; track node) {
                      <li>Suppression : {{ node }}</li>
                    }
                  </ul>
                }
                @if (
                  proposalChanges().addedConnections.length ||
                  proposalChanges().removedConnections.length
                ) {
                  <ul aria-label="Connexions modifiées">
                    @for (connection of proposalChanges().addedConnections; track connection) {
                      <li>Connexion ajoutée : {{ connection }}</li>
                    }
                    @for (connection of proposalChanges().removedConnections; track connection) {
                      <li>Connexion supprimée : {{ connection }}</li>
                    }
                  </ul>
                }
              } @else {
                Le flow contient {{ proposed.nodes.length }} bloc(s) et
                {{ proposed.connections.length }} connexion(s).
              }
            </p>
            <details>
              <summary>Examiner la définition complète</summary>
              <pre>{{ formatFlow(proposed) }}</pre>
            </details>
            <section class="proposal-simulation" aria-labelledby="proposal-simulation-title">
              <h4 id="proposal-simulation-title">Vérification du comportement</h4>
              <p>
                Simulation déterministe avec un profil fictif H.264 1080p et AAC stéréo. Les
                résultats supposés ne prouvent ni l’exécution réelle ni la disponibilité d’un outil.
              </p>
              <button
                matButton="tonal"
                type="button"
                [disabled]="simulation.busy()"
                (click)="simulateProposal(proposed)"
              >
                {{ simulation.busy() ? 'Simulation…' : 'Simuler cette proposition' }}
              </button>
              @if (simulation.error()) {
                <p role="alert">{{ simulation.error() }}</p>
              }
              @if (simulation.result(); as result) {
                <div class="simulation-result" role="status">
                  <strong>{{ simulationStatus(result.status) }}</strong>
                  <p>{{ result.detail }}</p>
                  @if (result.steps.length) {
                    <ol>
                      @for (step of result.steps; track $index) {
                        <li>
                          {{ step.name }} — {{ step.detail }}
                          @if (step.assumed) {
                            <span>(résultat supposé)</span>
                          }
                        </li>
                      }
                    </ol>
                  }
                </div>
              }
            </section>
            <app-flow-preview [flow]="proposed" [components]="data().components ?? []" />
            <div class="proposal-decisions">
              <button matButton="filled" [disabled]="applyBusy()" (click)="applyProposal()">
                Appliquer au flow
              </button>
              <button matButton="text" (click)="discardProposal()">Rejeter la proposition</button>
            </div>
          </section>
        }

        @if (errorMessage()) {
          <div class="error-box">
            <span class="error-mark" aria-hidden="true">!</span>
            <span>{{ errorMessage() }}</span>
          </div>
        }

        @if (errorMessage() && sessionId()) {
          <p>
            Vous pouvez réessayer la demande, ou ouvrir une nouvelle session depuis le dernier
            contexte sauvegardé. Une tentative sans résultat confirmé pourra consommer du quota à
            nouveau.
          </p>
          <button matButton="tonal" type="button" [disabled]="loading()" (click)="recoverSession()">
            Reprendre dans une nouvelle session
          </button>
        }
        @if (loading()) {
          <div class="loading-state" role="status" aria-live="polite">
            <mat-spinner diameter="24"></mat-spinner>
            <span>L’assistant analyse le catalogue et prépare le flow…</span>
          </div>
        }
      </div>

      <div class="app-dialog-footer flow-ai-footer">
        <span class="footer-note">{{ data().mediaType }}</span>
        <button
          class="cancel-action"
          matButton="text"
          type="button"
          [disabled]="loading()"
          (click)="close()"
        >
          Annuler
        </button>
        <button
          class="submit-action"
          matButton="filled"
          type="button"
          [disabled]="loading() || proposal() || (mode() !== 'explain' && !prompt().trim())"
          (click)="submit()"
        >
          <svg viewBox="0 0 24 24" fill="none" aria-hidden="true">
            @if (mode() === 'explain') {
              <circle cx="12" cy="12" r="9" />
              <path d="M3 12s3.2-5 9-5 9 5 9 5-3.2 5-9 5-9-5-9-5Z" />
              <circle cx="12" cy="12" r="2" />
            } @else {
              <path d="m12 2 1.55 6.45L20 10l-6.45 1.55L12 18l-1.55-6.45L4 10l6.45-1.55L12 2Z" />
              <path d="m19 16 .7 2.3L22 19l-2.3.7L19 22l-.7-2.3L16 19l2.3-.7L19 16Z" />
            }
          </svg>
          {{
            mode() === 'explain'
              ? 'Analyser le flow'
              : mode() === 'refactor'
                ? 'Préparer les changements'
                : 'Générer le flow'
          }}
        </button>
      </div>
    </section>
  `,
  styles: [
    `
      :host {
        display: flex;
        flex-direction: column;
        min-height: 0;
        max-width: 100%;
      }
      .flow-ai-dialog {
        display: flex;
        flex-direction: column;
        min-height: 0;
        width: 100%;
      }
      .flow-ai-body {
        min-height: 0;
        overflow: auto;
        flex: 1;
      }
      .proposal-decisions {
        display: flex;
        gap: 8px;
        flex-wrap: wrap;
        margin-top: 16px;
      }
      .flow-ai-dialog {
        min-width: 0;
        max-width: none;
      }
      .flow-ai-dialog .app-dialog-header {
        align-items: flex-start;
        padding: 24px 28px 20px;
      }
      .assistant-mark {
        width: 44px;
        height: 44px;
        flex: 0 0 44px;
        display: grid;
        place-items: center;
        border: 1px solid color-mix(in srgb, var(--accent) 25%, var(--border-subtle));
        border-radius: 14px;
        background: color-mix(in srgb, var(--accent-soft) 72%, var(--surface));
        color: var(--accent-strong);
      }
      .assistant-mark svg {
        width: 24px;
        height: 24px;
        stroke: currentColor;
        stroke-linecap: round;
        stroke-linejoin: round;
        stroke-width: 1.5;
      }
      .dialog-description {
        max-width: 620px;
        margin: 6px 0 0;
        color: var(--text-muted);
        font-size: 0.92rem;
        line-height: 1.5;
      }
      .dialog-close {
        width: 38px;
        height: 38px;
        flex: 0 0 38px;
        display: grid;
        place-items: center;
        margin: -4px -8px 0 0;
        border: 0;
        border-radius: 10px;
        background: transparent;
        color: var(--text-muted);
        cursor: pointer;
      }
      .dialog-close:hover {
        background: var(--surface-container);
        color: var(--text-primary);
      }
      .dialog-close span {
        font-size: 1.8rem;
        line-height: 1;
      }
      .flow-ai-body {
        display: flex;
        flex-direction: column;
        gap: 22px;
        padding-top: 4px !important;
      }
      .mode-selector {
        display: grid;
        grid-template-columns: repeat(3, minmax(0, 1fr));
        gap: 10px;
      }
      .mode-card {
        min-width: 0;
        min-height: 108px;
        display: grid;
        grid-template-columns: 38px minmax(0, 1fr) 14px;
        align-items: start;
        gap: 11px;
        padding: 15px 13px;
        border: 1px solid var(--border-subtle);
        border-radius: 14px;
        background: var(--surface);
        color: var(--text-secondary);
        font: inherit;
        text-align: left;
        cursor: pointer;
        transition:
          border-color 150ms ease,
          background 150ms ease,
          box-shadow 150ms ease;
      }
      .mode-card:hover {
        border-color: var(--border-strong);
        background: var(--surface-container-low);
      }
      .mode-card.selected {
        border-color: var(--accent);
        background: color-mix(in srgb, var(--accent-soft) 38%, var(--surface));
        box-shadow: inset 0 0 0 1px color-mix(in srgb, var(--accent) 20%, transparent);
      }
      .mode-card:focus-visible,
      .suggestion-chip:focus-visible,
      .dialog-close:focus-visible {
        outline: 3px solid color-mix(in srgb, var(--accent) 48%, transparent);
        outline-offset: 2px;
      }
      .mode-icon {
        width: 38px;
        height: 38px;
        display: grid;
        place-items: center;
        border-radius: 11px;
        background: var(--surface-container);
        color: var(--accent-strong);
      }
      .selected .mode-icon {
        background: var(--accent-soft);
      }
      .mode-icon svg {
        width: 20px;
        height: 20px;
        stroke: currentColor;
        stroke-linecap: round;
        stroke-linejoin: round;
        stroke-width: 1.7;
      }
      .mode-copy {
        min-width: 0;
        display: flex;
        flex-direction: column;
        gap: 5px;
      }
      .mode-copy strong {
        color: var(--text-primary);
        font-size: 0.9rem;
        line-height: 1.3;
      }
      .mode-copy > span {
        color: var(--text-muted);
        font-size: 0.77rem;
        line-height: 1.4;
      }
      .mode-indicator {
        width: 13px;
        height: 13px;
        margin-top: 2px;
        border: 1.5px solid var(--border-strong);
        border-radius: 50%;
      }
      .selected .mode-indicator {
        border: 4px solid var(--accent);
        background: var(--surface);
      }
      .suggestions-container {
        display: flex;
        flex-direction: column;
        gap: 10px;
      }
      .suggestions-heading {
        display: flex;
        align-items: baseline;
        flex-wrap: wrap;
        gap: 4px 10px;
      }
      .suggestions-title {
        color: var(--text-primary);
        font-size: 0.9rem;
        font-weight: 700;
      }
      .suggestions-hint {
        color: var(--text-muted);
        font-size: 0.8rem;
      }
      .suggestions-list {
        display: flex;
        flex-wrap: wrap;
        gap: 8px;
      }
      .suggestion-chip {
        max-width: 100%;
        padding: 8px 12px;
        border: 1px solid var(--border-subtle);
        border-radius: 999px;
        background: var(--surface-container-low);
        color: var(--text-secondary);
        font: inherit;
        font-size: 0.8rem;
        line-height: 1.3;
        cursor: pointer;
        text-align: left;
        transition:
          border-color 150ms ease,
          background 150ms ease,
          color 150ms ease;
      }
      .suggestion-chip:hover {
        border-color: var(--accent);
        background: var(--accent-soft);
        color: var(--accent-strong);
      }
      .prompt-field {
        width: 100%;
      }
      .prompt-field textarea {
        min-height: 132px;
        line-height: 1.55;
        resize: vertical;
      }
      .explain-card {
        display: flex;
        align-items: flex-start;
        gap: 12px;
        padding: 18px;
        border: 1px solid var(--border-subtle);
        border-radius: 14px;
        background: var(--surface-container-low);
      }
      .explain-card p {
        margin: 0;
        color: var(--text-secondary);
        line-height: 1.55;
      }
      .explain-icon {
        width: 24px;
        height: 24px;
        flex: 0 0 24px;
        display: grid;
        place-items: center;
        border-radius: 50%;
        background: var(--accent-soft);
        color: var(--accent-strong);
        font-weight: 800;
      }
      .loading-state {
        display: flex;
        align-items: center;
        gap: 12px;
        padding: 14px 16px;
        border: 1px solid var(--border-subtle);
        background: var(--surface-container-low);
        border-radius: 12px;
        color: var(--text-secondary);
        font-size: 0.9rem;
      }
      .error-box {
        display: flex;
        align-items: flex-start;
        gap: 10px;
        padding: 13px 15px;
        border: 1px solid color-mix(in srgb, var(--danger) 35%, var(--border-subtle));
        border-radius: 12px;
        background: var(--danger-soft);
        color: var(--danger-strong);
        font-size: 0.9rem;
        line-height: 1.45;
      }
      .error-mark {
        width: 19px;
        height: 19px;
        flex: 0 0 19px;
        display: grid;
        place-items: center;
        border: 1.5px solid currentColor;
        border-radius: 50%;
        font-size: 0.78rem;
        font-weight: 800;
        line-height: 1;
      }
      .explanation-box {
        padding: 18px;
        border: 1px solid var(--border-subtle);
        border-radius: 14px;
        background: var(--surface-container-low);
        line-height: 1.6;
      }
      .explanation-box h4 {
        margin: 0 0 8px;
        color: var(--text-primary);
      }
      .explanation-box p {
        margin: 0;
      }
      .proposal-box {
        padding: 18px;
        border: 1px solid var(--border-subtle);
        border-radius: 14px;
        background: var(--surface-container-low);
      }
      .proposal-box h3 {
        margin: 0 0 8px;
        color: var(--text-primary);
      }
      .proposal-box pre {
        max-height: 260px;
        overflow: auto;
        white-space: pre-wrap;
        overflow-wrap: anywhere;
      }
      .proposal-simulation {
        margin-top: 16px;
        padding: 16px;
        border-radius: var(--radius-md, 12px);
        background: var(--surface-container);
      }
      .proposal-simulation h4,
      .proposal-simulation p {
        margin-top: 0;
      }
      .simulation-result {
        margin-top: 12px;
      }
      .simulation-result ol {
        padding-inline-start: 22px;
      }
      .flow-ai-footer {
        gap: 10px;
      }
      .footer-note {
        margin-right: auto;
        color: var(--text-muted);
        font-size: 0.78rem;
        text-transform: capitalize;
      }
      .cancel-action {
        color: var(--text-secondary);
      }
      .submit-action svg {
        width: 18px;
        height: 18px;
        margin-right: 7px;
        vertical-align: -4px;
        stroke: currentColor;
        stroke-linecap: round;
        stroke-linejoin: round;
        stroke-width: 1.7;
      }
      @media (max-width: 760px) {
        .mode-selector {
          grid-template-columns: 1fr;
        }
        .mode-card {
          min-height: 76px;
        }
      }
      @media (max-width: 600px) {
        .flow-ai-dialog .app-dialog-header {
          gap: 11px;
          padding: 18px 18px 16px;
        }
        .assistant-mark {
          width: 38px;
          height: 38px;
          flex-basis: 38px;
          border-radius: 12px;
        }
        .dialog-close {
          margin-right: -8px;
        }
        .dialog-description {
          font-size: 0.85rem;
        }
        .flow-ai-footer {
          flex-wrap: wrap;
          gap: 4px;
        }
        .footer-note {
          display: none;
        }
        .flow-ai-footer button {
          min-width: 0;
          padding-inline: 12px;
        }
      }
      @media (prefers-reduced-motion: reduce) {
        .mode-card,
        .suggestion-chip {
          transition: none;
        }
      }
    `,
  ],
})
export class FlowAiAssistantPanelComponent {
  readonly context = input.required<FlowAiAssistantPanelData>();
  readonly data = this.context;
  readonly applyBusy = input(false);
  readonly flowApplied = output<FlowDocument>();
  readonly dismissed = output<void>();
  constructor() {
    effect(() => {
      const data = this.data();
      untracked(() => {
        this.setMode(data.currentFlow ? 'refactor' : 'generate');
        this.prompt.set(data.initialPrompt ?? '');
      });
    });
  }
  private readonly aiService = inject(AiToolsService);
  private readonly notifications = inject(NotificationService);
  protected readonly simulation = inject(FlowSimulation);

  readonly mode = signal<'generate' | 'refactor' | 'explain'>('generate');
  readonly prompt = signal('');
  readonly loading = signal(false);
  readonly explanation = signal<string | null>(null);
  readonly proposal = signal<FlowDocument | null>(null);
  readonly proposalRequestId = signal<string | null>(null);
  readonly proposalChanges = computed(() => {
    const current = this.data().currentFlow;
    const proposed = this.proposal();
    if (!current || !proposed)
      return {
        addedNodes: [] as string[],
        changedNodes: [] as string[],
        removedNodes: [] as string[],
        addedConnections: [] as string[],
        removedConnections: [] as string[],
        metadataChanged: false,
      };
    const nodeLabel = (node: FlowDocument['nodes'][number]) =>
      `${node.name || node.component.componentId} · ${node.id}`;
    const currentNodes = new Map(current.nodes.map((node) => [node.id, node]));
    const proposedNodes = new Map(proposed.nodes.map((node) => [node.id, node]));
    const addedNodes = [...proposedNodes]
      .filter(([id]) => !currentNodes.has(id))
      .map(([, node]) => nodeLabel(node));
    const removedNodes = [...currentNodes]
      .filter(([id]) => !proposedNodes.has(id))
      .map(([, node]) => nodeLabel(node));
    const changedNodes = [...proposedNodes]
      .filter(([id, node]) => {
        const previous = currentNodes.get(id);
        return previous !== undefined && JSON.stringify(previous) !== JSON.stringify(node);
      })
      .map(([, node]) => nodeLabel(node));
    const connectionLabel = (connection: FlowDocument['connections'][number]) =>
      `${connection.sourceNodeId} · ${connection.sourceOutputId} vers ${connection.targetNodeId}`;
    const key = (connection: FlowDocument['connections'][number]) =>
      `${connection.sourceNodeId}|${connection.sourceOutputId}|${connection.targetNodeId}`;
    const currentConnections = new Set(current.connections.map(key));
    const proposedConnections = new Set(proposed.connections.map(key));
    return {
      addedNodes,
      changedNodes,
      removedNodes,
      addedConnections: proposed.connections
        .filter((item) => !currentConnections.has(key(item)))
        .map(connectionLabel),
      removedConnections: current.connections
        .filter((item) => !proposedConnections.has(key(item)))
        .map(connectionLabel),
      metadataChanged:
        current.name !== proposed.name ||
        current.mediaType !== proposed.mediaType ||
        JSON.stringify(current.componentPacks ?? []) !==
          JSON.stringify(proposed.componentPacks ?? []),
    };
  });
  readonly errorMessage = signal<string | null>(null);

  readonly sessionId = signal<string | null>(null);
  private requestId: string = createUuid();
  private lastPrompt = '';

  readonly suggestions = [
    'Anime japonais (JP/FR + sous-titres ASS)',
    '4K HDR vers 1080p SDR HEVC',
    'Normalisation audio AAC stéréo',
    'Garde de lecture Plex avant remplacement',
  ];

  setMode(newMode: 'generate' | 'refactor' | 'explain'): void {
    this.mode.set(newMode);
    this.sessionId.set(null);
    this.requestId = createUuid();
    this.errorMessage.set(null);
    this.explanation.set(null);
    this.proposal.set(null);
    this.proposalRequestId.set(null);
  }

  acknowledgeProposal(): void {
    this.recordProposalDecision('applied-to-editor');
  }

  applyProposal(): void {
    const flow = this.proposal();
    if (!flow) return;
    this.flowApplied.emit(flow);
  }

  simulateProposal(flow: FlowDocument): void {
    this.simulation.prepare(flow);
    void this.simulation.run(this.simulation.profile());
  }

  simulationStatus(status: string): string {
    return (
      (
        {
          completed: 'Simulation terminée',
          waiting: 'Simulation en attente d’une hypothèse',
          failed: 'Échec simulé',
          invalid: 'Flow invalide pour la simulation',
        } as Record<string, string>
      )[status] ?? 'État de simulation inconnu'
    );
  }

  discardProposal(): void {
    this.recordProposalDecision('rejected');
    this.proposal.set(null);
    this.errorMessage.set(null);
    this.requestId = createUuid();
    this.lastPrompt = '';
  }

  private recordProposalDecision(decision: 'applied-to-editor' | 'rejected'): void {
    const requestId = this.proposalRequestId();
    if (!requestId) return;
    this.aiService.recordFlowProposalDecision(requestId, decision).subscribe({
      error: () =>
        this.notifications.show(
          'La décision est appliquée, mais son suivi IA n’a pas pu être mis à jour.',
        ),
    });
    this.proposalRequestId.set(null);
  }

  formatFlow(flow: FlowDocument): string {
    return JSON.stringify(flow, null, 2);
  }

  recoverSession(): void {
    const id = this.sessionId();
    if (!id || this.loading()) return;
    this.loading.set(true);
    this.aiService.forkSession(id).subscribe({
      next: (session) => {
        this.sessionId.set(session.id);
        this.requestId = createUuid();
        this.loading.set(false);
        this.errorMessage.set(null);
      },
      error: () => {
        this.loading.set(false);
        this.errorMessage.set(
          'Le checkpoint n’est pas encore disponible ou une tentative peut encore être active. Réessayez après expiration de son bail.',
        );
      },
    });
  }

  applySuggestion(text: string): void {
    this.prompt.set(text);
  }

  submit(): void {
    if (this.loading()) return;
    if (this.lastPrompt !== this.prompt()) {
      this.requestId = createUuid();
      this.lastPrompt = this.prompt();
    }
    this.loading.set(true);
    this.errorMessage.set(null);
    this.explanation.set(null);
    this.proposal.set(null);
    this.proposalRequestId.set(null);

    const currentMode = this.mode();
    if (currentMode === 'generate') {
      this.aiService
        .generateFlow({
          sessionId: this.sessionId(),
          requestId: this.requestId,
          mediaType: this.data().mediaType,
          userPrompt: this.prompt(),
        })
        .subscribe({
          next: (res) => {
            this.sessionId.set(res.sessionId ?? this.sessionId());
            if (res.requestId) this.requestId = res.requestId;
            if (res.success) this.requestId = createUuid();
            this.loading.set(false);
            if (res.success && res.flow) {
              this.proposalRequestId.set(res.requestId ?? null);
              this.proposal.set(res.flow);
            } else {
              this.errorMessage.set(res.explanation || 'Impossible de générer un flow valide.');
            }
          },
          error: (err) => {
            this.loading.set(false);
            this.errorMessage.set(err.message ?? 'Erreur lors de la génération.');
          },
        });
    } else if (currentMode === 'refactor' && this.data().currentFlow) {
      this.aiService
        .refactorFlow({
          sessionId: this.sessionId(),
          requestId: this.requestId,
          currentFlow: this.data().currentFlow!,
          userPrompt: this.prompt(),
        })
        .subscribe({
          next: (res) => {
            this.sessionId.set(res.sessionId ?? this.sessionId());
            if (res.requestId) this.requestId = res.requestId;
            if (res.success) this.requestId = createUuid();
            this.loading.set(false);
            if (res.success && res.flow) {
              this.proposalRequestId.set(res.requestId ?? null);
              this.proposal.set(res.flow);
            } else {
              this.errorMessage.set(
                res.explanation || 'Impossible d’appliquer les modifications au flow.',
              );
            }
          },
          error: (err) => {
            this.loading.set(false);
            this.errorMessage.set(err.message ?? 'Erreur lors de la modification.');
          },
        });
    } else if (currentMode === 'explain' && this.data().currentFlow) {
      this.aiService
        .explainFlow({
          sessionId: this.sessionId(),
          requestId: this.requestId,
          flow: this.data().currentFlow!,
        })
        .subscribe({
          next: (res) => {
            this.sessionId.set(res.sessionId ?? this.sessionId());
            if (res.requestId) this.requestId = res.requestId;
            if (res.success) this.requestId = createUuid();
            this.loading.set(false);
            if (res.success) this.explanation.set(res.explanation ?? 'Explication non disponible.');
            else this.errorMessage.set(res.explanation ?? 'Impossible de joindre le fournisseur.');
          },
          error: (err) => {
            this.loading.set(false);
            this.errorMessage.set(err.message ?? 'Erreur lors de l’analyse.');
          },
        });
    }
  }

  close(): void {
    this.dismissed.emit();
  }
}
