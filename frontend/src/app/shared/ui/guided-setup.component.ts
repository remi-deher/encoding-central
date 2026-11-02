import { BreakpointObserver } from '@angular/cdk/layout';
import { toSignal } from '@angular/core/rxjs-interop';
import { map } from 'rxjs';
import {
  ChangeDetectionStrategy,
  afterRenderEffect,
  Component,
  inject,
  input,
  output,
  viewChild,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatStepper, MatStepperIntl, MatStepperModule } from '@angular/material/stepper';
import { DetailCardGridComponent, DetailCard } from './detail-card-grid.component';
import { RecordFormComponent, RecordFormData } from './record-form.component';
import {
  DataTableComponent,
  DataTableColumn,
  DataTableRow,
  DataTableRowAction,
  DataTableActionEvent,
} from './data-table.component';

export interface GuidedHeroCode {
  readonly code: string;
  readonly label?: string;
  readonly helper?: string;
  readonly badge?: string;
  readonly tone?: 'accent' | 'warning' | 'neutral';
}

export interface GuidedStep {
  readonly id: string;
  readonly label: string;
  readonly description: string;
  readonly form?: RecordFormData;
  readonly cards?: readonly DetailCard[];
  readonly heroCode?: GuidedHeroCode;
  readonly note?: string;
  readonly acknowledgement?: {
    readonly label: string;
  };
  readonly choices?: {
    readonly columns: readonly DataTableColumn[];
    readonly rows: readonly DataTableRow[];
    readonly actions?: readonly DataTableRowAction[];
  };
  readonly actions?: readonly {
    id: string;
    label: string;
    primary?: boolean;
    disabled?: boolean;
    requiresAcknowledgement?: boolean;
  }[];
}

/** Gabarit de saisie guidée : étapes déclaratives, formulaires communs et stepper Material/CDK. */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-guided-setup',
  providers: [
    {
      provide: MatStepperIntl,
      useFactory: () =>
        Object.assign(new MatStepperIntl(), {
          completedLabel: 'Terminée',
          editableLabel: 'Modifiable',
          optionalLabel: 'Facultatif',
        }),
    },
  ],
  imports: [
    MatStepperModule,
    MatButtonModule,
    MatCheckboxModule,
    MatProgressBarModule,
    RecordFormComponent,
    DetailCardGridComponent,
    DataTableComponent,
  ],
  template: `
    @if (busy()) {
      <mat-progress-bar mode="indeterminate" aria-label="Vérification en cours" />
    }
    @if (error()) {
      <div role="alert" class="error">
        <svg class="callout-icon" viewBox="0 0 24 24" width="20" height="20" aria-hidden="true">
          <circle cx="12" cy="12" r="10" fill="none" stroke="currentColor" stroke-width="2" />
          <path d="M12 8v4m0 4h.01" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" />
        </svg>
        <span>{{ error() }}</span>
      </div>
    }
    <mat-stepper
      [orientation]="handset() ? 'vertical' : orientation()"
      linear
      aria-label="Étapes du parcours"
    >
      <ng-template matStepperIcon="done">
        <svg viewBox="0 0 24 24" width="18" height="18" aria-hidden="true">
          <path d="m5 12 4 4 10-10" fill="none" stroke="currentColor" stroke-width="2" />
        </svg>
      </ng-template>
      @for (step of steps(); track step.id; let i = $index) {
        <mat-step [label]="step.label" [completed]="i < index()" [editable]="false">
          @if (i === index()) {
            <p class="step-description">{{ step.description }}</p>
            @if (step.heroCode; as hero) {
              <div
                class="hero-code-container"
                [class]="'hero-code-container hero-code-container_' + (hero.tone ?? 'accent')"
              >
                <div class="hero-code-header">
                  <span class="hero-code-label">{{ hero.label ?? 'Code de vérification' }}</span>
                  @if (hero.badge) {
                    <span class="hero-code-badge">{{ hero.badge }}</span>
                  }
                </div>
                <div class="hero-code-value-wrapper">
                  <span class="hero-code-value" aria-label="Code de vérification">{{ hero.code }}</span>
                </div>
                @if (hero.helper) {
                  <p class="hero-code-helper">{{ hero.helper }}</p>
                }
              </div>
            }
            @if (step.choices; as choices) {
              <app-data-table
                [layout]="handset() ? 'cards' : 'table'"
                [columns]="choices.columns"
                [rows]="choices.rows"
                [rowOpenable]="!busy() && !disabled()"
                rowOpenLabel="Choisir"
                [rowActions]="busy() || disabled() ? [] : (choices.actions ?? [])"
                (rowOpen)="chosen.emit($event)"
                (rowAction)="choiceAction.emit($event)"
                emptyMessage="Aucun candidat détecté."
              />
            }
            @if (step.cards) {
              <app-detail-card-grid [cards]="step.cards!" [collapsible]="false" />
            }
            @if (step.note) {
              <div role="note" class="note">
                <svg class="callout-icon" viewBox="0 0 24 24" width="20" height="20" aria-hidden="true">
                  <circle cx="12" cy="12" r="10" fill="none" stroke="currentColor" stroke-width="2" />
                  <path d="M12 16v-4m0-4h.01" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" />
                </svg>
                <span>{{ step.note }}</span>
              </div>
            }
            @if (step.acknowledgement; as acknowledgement) {
              <mat-checkbox
                class="acknowledgement"
                [checked]="acknowledged()"
                [disabled]="busy() || disabled()"
                (change)="acknowledgementChange.emit($event.checked)"
              >
                {{ acknowledgement.label }}
              </mat-checkbox>
            }
            @if (step.form) {
              <app-record-form
                [data]="step.form!"
                [busy]="busy() || disabled()"
                [useStepper]="false"
                (submitted)="submitted.emit({ stepId: step.id, values: $event })"
                (cancelled)="cancelled.emit()"
              />
            }
            <div class="actions">
              @for (action of step.actions ?? []; track action.id) {
                <button
                  [matButton]="action.primary ? 'filled' : 'outlined'"
                  [disabled]="
                    busy() ||
                    disabled() ||
                    action.disabled ||
                    (action.requiresAcknowledgement && !acknowledged())
                  "
                  (click)="acted.emit(action.id)"
                >
                  {{ action.label }}
                </button>
              }
            </div>
          }
        </mat-step>
      }
    </mat-stepper>
  `,
  styles: `
    .step-description {
      margin: 0 0 var(--space-4);
      color: var(--text-secondary);
      font-size: var(--font-body, 14px);
      line-height: 1.55;
    }
    .actions {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-2);
      margin-top: var(--space-4);
    }
    .note {
      display: flex;
      align-items: flex-start;
      gap: var(--space-3);
      margin-block: var(--space-4);
      padding: var(--space-3) var(--space-4);
      border-radius: var(--radius-lg);
      background: var(--surface-container);
      border-inline-start: 4px solid var(--accent);
      color: var(--text-primary);
      font-size: var(--font-body, 14px);
      line-height: 1.5;
    }
    .error {
      display: flex;
      align-items: flex-start;
      gap: var(--space-3);
      margin-block: var(--space-3);
      padding: var(--space-3) var(--space-4);
      border-radius: var(--radius-lg);
      border-inline-start: 4px solid var(--danger);
      background: var(--danger-soft);
      color: var(--text-primary);
      font-size: var(--font-body, 14px);
      line-height: 1.5;
    }
    .callout-icon {
      flex: 0 0 20px;
      margin-top: 2px;
      color: currentColor;
    }
    .note .callout-icon {
      color: var(--accent);
    }
    .error .callout-icon {
      color: var(--danger);
    }
    .acknowledgement {
      display: block;
      margin-block: var(--space-4);
      font-size: var(--font-body, 14px);
      font-weight: 500;
    }
    .hero-code-container {
      margin-block: var(--space-2) var(--space-4);
      padding: var(--space-4);
      border-radius: var(--radius-xl);
      background: var(--surface-container-high);
      border: 1px solid var(--border-subtle);
      display: flex;
      flex-direction: column;
      align-items: center;
      text-align: center;
      gap: var(--space-2);
    }
    .hero-code-container_accent {
      background: color-mix(in srgb, var(--accent) 8%, var(--surface-container-high));
      border-color: color-mix(in srgb, var(--accent) 35%, var(--border-subtle));
    }
    .hero-code-container_warning {
      background: color-mix(in srgb, var(--warning) 10%, var(--surface-container-high));
      border-color: color-mix(in srgb, var(--warning) 40%, var(--border-subtle));
    }
    .hero-code-header {
      width: 100%;
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-2);
    }
    .hero-code-label {
      font-size: var(--font-label);
      font-weight: 700;
      letter-spacing: 0.06em;
      text-transform: uppercase;
      color: var(--text-muted);
    }
    .hero-code-badge {
      padding: 3px 8px;
      border-radius: var(--radius-pill);
      font-size: var(--font-label);
      font-weight: 700;
      background: var(--accent-soft);
      color: var(--accent-strong);
    }
    .hero-code-container_warning .hero-code-badge {
      background: var(--warning-soft);
      color: var(--warning);
    }
    .hero-code-value-wrapper {
      padding: var(--space-2) var(--space-4);
    }
    .hero-code-value {
      font-family: ui-monospace, 'JetBrains Mono', SFMono-Regular, Menlo, Monaco, Consolas, monospace;
      font-size: var(--font-display-sm);
      font-weight: 800;
      letter-spacing: 0.22em;
      color: var(--text-primary);
      text-indent: 0.22em;
      user-select: all;
    }
    .hero-code-helper {
      margin: 0;
      font-size: var(--font-body);
      color: var(--text-secondary);
      line-height: 1.45;
    }
    :host ::ng-deep .mat-step-header .mat-step-label {
      font-size: var(--font-body, 14px);
      font-weight: 600;
    }
  `,
})
export class GuidedSetupComponent {
  protected readonly handset = toSignal(
    inject(BreakpointObserver)
      .observe('(max-width: 700px)')
      .pipe(map((state) => state.matches)),
    { initialValue: false },
  );
  private readonly stepper = viewChild(MatStepper);
  readonly steps = input.required<readonly GuidedStep[]>();
  readonly orientation = input<'horizontal' | 'vertical'>('vertical');
  readonly index = input(0);
  readonly busy = input(false);
  readonly disabled = input(false);
  readonly error = input('');
  readonly acknowledged = input(false);
  readonly submitted = output<{ stepId: string; values: Record<string, string> }>();
  readonly acted = output<string>();
  readonly cancelled = output<void>();
  readonly chosen = output<DataTableRow>();
  readonly choiceAction = output<DataTableActionEvent>();
  readonly acknowledgementChange = output<boolean>();
  constructor() {
    // Appliquer l'index après les indicateurs completed des étapes ; le stepper linéaire
    // vérifie ces indicateurs avant d'autoriser le passage à l'étape suivante.
    afterRenderEffect(() => {
      const stepper = this.stepper();
      if (stepper && stepper.selectedIndex !== this.index()) stepper.selectedIndex = this.index();
    });
  }
}
