import { afterRenderEffect, Component, input, output, viewChild } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatStepper, MatStepperIntl, MatStepperModule } from '@angular/material/stepper';
import {
  DetailCardGridComponent,
  DetailCard,
} from '../../src/app/shared/ui/detail-card-grid.component';
import { RecordFormComponent, RecordFormData } from '../../src/app/shared/ui/record-form.component';

export interface GuidedStep {
  readonly id: string;
  readonly label: string;
  readonly description: string;
  readonly form?: RecordFormData;
  readonly cards?: readonly DetailCard[];
  readonly note?: string;
  readonly actions?: readonly { id: string; label: string; primary?: boolean }[];
}

/** Prototype d'un gabarit réutilisable : étapes déclaratives, formulaires communs et stepper Material/CDK. */
@Component({
  selector: 'mock-guided-setup',
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
    MatProgressBarModule,
    RecordFormComponent,
    DetailCardGridComponent,
  ],
  template: `
    @if (busy()) {
      <mat-progress-bar mode="indeterminate" aria-label="Vérification en cours" />
    }
    @if (error()) {
      <p role="alert" class="error">{{ error() }}</p>
    }
    <mat-stepper orientation="vertical" linear aria-label="Étapes du parcours">
      <ng-template matStepperIcon="done">
        <svg viewBox="0 0 24 24" width="18" height="18" aria-hidden="true">
          <path d="m5 12 4 4 10-10" fill="none" stroke="currentColor" stroke-width="2" />
        </svg>
      </ng-template>
      @for (step of steps(); track step.id; let i = $index) {
        <mat-step [label]="step.label" [completed]="i < index()" [editable]="false">
          @if (i === index()) {
            <p>{{ step.description }}</p>
            @if (step.cards) {
              <app-detail-card-grid [cards]="step.cards!" [collapsible]="false" />
            }
            @if (step.note) {
              <p role="note" class="note">{{ step.note }}</p>
            }
            @if (step.form) {
              <app-record-form
                [data]="step.form!"
                [busy]="busy()"
                (submitted)="submitted.emit({ stepId: step.id, values: $event })"
                (cancelled)="cancelled.emit()"
              />
            }
            <div class="actions">
              @for (action of step.actions ?? []; track action.id) {
                <button
                  [matButton]="action.primary ? 'filled' : 'outlined'"
                  [disabled]="busy()"
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
    p {
      color: var(--text-secondary);
      line-height: 1.6;
    }
    .actions {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-2);
      margin-top: var(--space-4);
    }
    .note {
      padding: var(--space-3);
      border-radius: var(--radius-md);
      background: var(--surface-muted);
    }
    .error {
      padding: var(--space-3);
      border-radius: var(--radius-md);
      border-inline-start: 3px solid var(--danger);
      background: var(--danger-soft);
      color: var(--text-primary);
    }
  `,
})
export class GuidedSetup {
  private readonly stepper = viewChild(MatStepper);
  readonly steps = input.required<readonly GuidedStep[]>();
  readonly index = input(0);
  readonly busy = input(false);
  readonly error = input('');
  readonly submitted = output<{ stepId: string; values: Record<string, string> }>();
  readonly acted = output<string>();
  readonly cancelled = output<void>();
  constructor() {
    // Appliquer l'index après les indicateurs completed des étapes ; le stepper linéaire
    // vérifie ces indicateurs avant d'autoriser le passage à l'étape suivante.
    afterRenderEffect(() => {
      const stepper = this.stepper();
      if (stepper && stepper.selectedIndex !== this.index()) stepper.selectedIndex = this.index();
    });
  }
}
