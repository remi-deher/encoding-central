import {
  ChangeDetectionStrategy,
  Component,
  computed,
  OnChanges,
  SimpleChanges,
  input,
  inject,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { BreakpointObserver } from '@angular/cdk/layout';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatStepper, MatStepperModule } from '@angular/material/stepper';
import { SchemaFormFieldComponent } from './schema-form-field.component';

export interface RecordFormField {
  readonly id: string;
  readonly label: string;
  readonly type: 'text' | 'select' | 'number' | 'password' | 'textarea' | 'email' | 'url' | 'date';
  readonly autocomplete?: string;
  readonly placeholder?: string;
  readonly min?: number;
  readonly max?: number;
  readonly step?: number;
  readonly minLength?: number;
  readonly maxLength?: number;
  readonly pattern?: string;
  readonly hint?: string;
  readonly required?: boolean;
  readonly filterOptionsByFieldId?: string;
  readonly options?: readonly RecordFormOption[];
}

export interface RecordFormOption {
  readonly value: string;
  readonly label: string;
  readonly mediaTypes?: readonly string[];
}

export interface RecordFormStep {
  readonly id: string;
  readonly label: string;
  readonly description?: string;
  readonly fieldIds: readonly string[];
}

export interface RecordFormData {
  readonly title: string;
  readonly description: string;
  readonly submitLabel: string;
  readonly reviewTitle?: string;
  /** `false` soumet directement, sans étape de récapitulatif (connexion, changement de mot de passe). */
  readonly review?: boolean;
  readonly cancellable?: boolean;
  readonly fields: readonly RecordFormField[];
  /** Les formulaires courts conservent une étape unique ; les parcours complexes déclarent leurs étapes ici. */
  readonly steps?: readonly RecordFormStep[];
  readonly initialValues?: Readonly<Record<string, string>>;
}

type RecordFormControls = Record<string, FormControl<string>>;

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-record-form',
  imports: [MatButtonModule, MatStepperModule, ReactiveFormsModule, SchemaFormFieldComponent],
  template: `
    <section class="record-form" [attr.aria-label]="data().title">
      @if (useStepper()) {
        <mat-stepper
          #stepper
          linear
          [orientation]="narrowViewport().matches && !embedded() ? 'vertical' : 'horizontal'"
          aria-label="Étapes du formulaire"
          [class.stepper-compact]="embedded()"
          (selectedIndexChange)="stepIndex.set($event)"
        >
          @for (step of steps(); track step.id; let index = $index; let last = $last) {
            <mat-step [label]="step.label" [stepControl]="formFor(step.id)">
              <form [formGroup]="formFor(step.id)" (ngSubmit)="advance(stepper, step, last)">
                @if (step.description && !embedded()) {
                  <p class="step-description">{{ step.description }}</p>
                }
                <div class="field-grid">
                  @for (field of fieldsFor(step); track field.id) {
                    <app-schema-form-field
                      [label]="field.label"
                      [type]="field.type"
                      [control]="controlFor(field.id)!"
                      [options]="optionsFor(field)"
                      [hint]="fieldHint(field)"
                      [error]="fieldError(field)"
                      [placeholder]="field.placeholder ?? ''"
                      [autocomplete]="field.autocomplete ?? ''"
                      [min]="field.min"
                      [max]="field.max"
                      [step]="field.step"
                      [minLength]="field.minLength"
                      [maxLength]="field.maxLength"
                      (selectionChanged)="onSelectChange(field)"
                    />
                  }
                </div>
                @if (!embedded()) {
                  <footer class="form-actions">
                    @if (index > 0) {
                      <button matButton="text" type="button" matStepperPrevious [disabled]="busy()">
                        Retour
                      </button>
                    } @else if (data().cancellable !== false) {
                      <button
                        matButton="text"
                        type="button"
                        [disabled]="busy()"
                        (click)="cancelled.emit()"
                      >
                        Annuler
                      </button>
                    }
                    <button matButton="filled" type="submit" [disabled]="busy()">
                      {{
                        last
                          ? data().review === false
                            ? data().submitLabel
                            : 'Vérifier'
                          : 'Continuer'
                      }}
                    </button>
                  </footer>
                }
              </form>
            </mat-step>
          }
          @if (data().review !== false) {
            <mat-step label="Vérification">
              <div class="review-step">
                <h3>{{ data().reviewTitle ?? 'Vérifier la définition' }}</h3>
                @if (!embedded()) {
                  <p class="step-description">
                    Relisez les paramètres avant de les enregistrer. Vous pouvez revenir à chaque
                    étape pour les modifier.
                  </p>
                }
                <dl class="summary">
                  @for (field of data().fields; track field.id) {
                    @if (isFieldVisible(field)) {
                      <div>
                        <dt>{{ field.label }}</dt>
                        <dd>{{ displayValue(field.id) }}</dd>
                      </div>
                    }
                  }
                </dl>
                @if (!embedded()) {
                  <footer class="form-actions">
                    <button matButton="text" type="button" matStepperPrevious [disabled]="busy()">
                      Modifier
                    </button>
                    <button matButton="filled" type="button" [disabled]="busy()" (click)="save()">
                      {{ data().submitLabel }}
                    </button>
                  </footer>
                }
              </div>
            </mat-step>
          }
        </mat-stepper>
      } @else if (!reviewing) {
        <form
          [formGroup]="formFor(steps()[0].id)"
          (ngSubmit)="data().review === false ? save() : reviewLegacy()"
        >
          <div class="field-grid">
            @for (field of fieldsFor(steps()[0]); track field.id) {
              <app-schema-form-field
                [label]="field.label"
                [type]="field.type"
                [control]="controlFor(field.id)!"
                [options]="optionsFor(field)"
                [hint]="fieldHint(field)"
                [error]="fieldError(field)"
                [placeholder]="field.placeholder ?? ''"
                [autocomplete]="field.autocomplete ?? ''"
                [min]="field.min"
                [max]="field.max"
                [step]="field.step"
                [minLength]="field.minLength"
                [maxLength]="field.maxLength"
                (selectionChanged)="onSelectChange(field)"
              />
            }
          </div>
          @if (!embedded()) {
            <footer class="form-actions">
              @if (data().cancellable !== false) {
                <button
                  matButton="text"
                  type="button"
                  [disabled]="busy()"
                  (click)="cancelled.emit()"
                >
                  Annuler
                </button>
              }
              <button matButton="filled" type="submit" [disabled]="busy()">
                {{ data().review === false ? data().submitLabel : 'Vérifier' }}
              </button>
            </footer>
          }
        </form>
      } @else {
        <div class="review-step">
          <h3>{{ data().reviewTitle ?? 'Vérifier la définition' }}</h3>
          <dl class="summary">
            @for (field of data().fields; track field.id) {
              @if (isFieldVisible(field)) {
                <div>
                  <dt>{{ field.label }}</dt>
                  <dd>{{ displayValue(field.id) }}</dd>
                </div>
              }
            }
          </dl>
          @if (!embedded()) {
            <footer class="form-actions">
              <button
                matButton="text"
                type="button"
                [disabled]="busy()"
                (click)="reviewing = false"
              >
                Modifier
              </button>
              <button matButton="filled" type="button" [disabled]="busy()" (click)="save()">
                {{ data().submitLabel }}
              </button>
            </footer>
          }
        </div>
      }
    </section>
  `,
  styles: `
    :host ::ng-deep .stepper-compact .mat-horizontal-stepper-header-container {
      display: none;
    }
    .record-form {
      width: 100%;
      min-width: 0;
    }
    form,
    .review-step {
      display: grid;
      gap: var(--space-4);
      padding-top: var(--space-3);
    }
    .field-grid {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: var(--space-3) var(--space-4);
    }
    mat-form-field {
      width: 100%;
      min-width: 0;
    }
    .step-description {
      margin: 0;
      color: var(--text-secondary);
      line-height: 1.55;
    }
    h3 {
      margin: 0;
      color: var(--text-primary);
      font-size: var(--font-title-sm);
    }
    .summary {
      display: grid;
      gap: 0;
      margin: 0;
      border-top: 1px solid var(--border-subtle);
    }
    .summary div {
      display: grid;
      grid-template-columns: minmax(110px, 0.8fr) minmax(0, 1.2fr);
      gap: var(--space-3);
      padding: var(--space-3) 0;
      border-bottom: 1px solid var(--border-subtle);
    }
    dt {
      color: var(--text-muted);
    }
    dd {
      min-width: 0;
      margin: 0;
      color: var(--text-primary);
      font-weight: 600;
      overflow-wrap: anywhere;
    }
    .form-actions {
      position: sticky;
      bottom: calc(-1 * var(--space-5));
      z-index: 1;
      display: flex;
      justify-content: flex-end;
      gap: var(--space-2);
      margin: 0 calc(-1 * var(--space-5)) calc(-1 * var(--space-5));
      padding: var(--space-3) var(--space-5);
      border-top: 1px solid var(--border-subtle);
      background: var(--surface);
    }
    @media (max-width: 600px) {
      .field-grid {
        grid-template-columns: minmax(0, 1fr);
      }
      .summary div {
        grid-template-columns: minmax(0, 1fr);
        gap: 4px;
      }
      .form-actions {
        bottom: calc(-1 * var(--space-4));
        margin-inline: calc(-1 * var(--space-4));
        margin-bottom: calc(-1 * var(--space-4));
        padding-inline: var(--space-4);
      }
    }
  `,
})
export class RecordFormComponent implements OnChanges {
  protected readonly narrowViewport = toSignal(
    inject(BreakpointObserver).observe('(max-width: 699px)'),
    {
      initialValue: { matches: false, breakpoints: {} },
    },
  );
  readonly data = input.required<RecordFormData>();
  readonly busy = input(false);
  readonly useStepper = input(true);
  /**
   * Dans un tiroir : le formulaire n'affiche ni son stepper ni ses boutons. Le tiroir porte le
   * pied (`primaryLabel`, `secondaryLabel`, `primary()`, `secondary()`) et l'avancement (`stepIndex`).
   */
  readonly embedded = input(false);
  readonly stepIndex = signal(0);
  private readonly stepper = viewChild(MatStepper);
  readonly stepCount = computed(() => this.steps().length + (this.data().review === false ? 0 : 1));
  readonly primaryLabel = computed(() => {
    const index = this.stepIndex();
    const count = this.steps().length;
    if (index >= count) return this.data().submitLabel;
    if (index < count - 1) return 'Continuer';
    return this.data().review === false ? this.data().submitLabel : 'Vérifier';
  });
  readonly secondaryLabel = computed(() => {
    const index = this.stepIndex();
    if (index === 0) return this.data().cancellable === false ? null : 'Annuler';
    return index >= this.steps().length ? 'Modifier' : 'Retour';
  });
  readonly submitted = output<Record<string, string>>();
  readonly cancelled = output<void>();
  primary(): void {
    const index = this.stepIndex();
    const steps = this.steps();
    const stepper = this.stepper();
    if (index >= steps.length) this.save();
    else if (stepper) this.advance(stepper, steps[index], index === steps.length - 1);
  }

  secondary(): void {
    if (this.stepIndex() === 0) this.cancelled.emit();
    else this.stepper()?.previous();
  }

  protected stepForms: Record<string, FormGroup<RecordFormControls>> = {};
  protected reviewing = false;

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['data']) {
      this.reviewing = false;
      this.stepIndex.set(0);
      this.stepForms = {};
      for (const step of this.steps()) {
        const controls: RecordFormControls = {};
        for (const field of this.fieldsFor(step)) {
          const validators = [];
          if (field.required) validators.push(Validators.required);
          if (field.minLength) validators.push(Validators.minLength(field.minLength));
          if (field.maxLength) validators.push(Validators.maxLength(field.maxLength));
          if (field.pattern) validators.push(Validators.pattern(field.pattern));
          if (field.type === 'email') validators.push(Validators.email);
          if (field.type === 'url') validators.push(Validators.pattern(/^https?:\/\/.+\..+/i));
          if (field.type === 'number') {
            if (field.min !== undefined) validators.push(Validators.min(field.min));
            if (field.max !== undefined) validators.push(Validators.max(field.max));
            if (field.step === 1) validators.push(Validators.pattern(/^-?[0-9]+$/));
          }
          controls[field.id] = new FormControl(this.data().initialValues?.[field.id] ?? '', {
            nonNullable: true,
            validators,
          });
        }
        this.stepForms[step.id] = new FormGroup(controls);
      }
    }
    for (const form of Object.values(this.stepForms)) {
      if (this.busy()) form.disable({ emitEvent: false });
      else form.enable({ emitEvent: false });
    }
  }

  protected steps(): readonly RecordFormStep[] {
    const configured = this.data().steps;
    if (!configured?.length)
      return [
        {
          id: 'details',
          label: 'Informations',
          description: this.data().description,
          fieldIds: this.data().fields.map((field) => field.id),
        },
      ];

    const knownFields = new Set(this.data().fields.map((field) => field.id));
    const assignedFields = new Set<string>();
    const steps = configured.map((step) => ({
      ...step,
      fieldIds: step.fieldIds.filter((fieldId) => {
        if (!knownFields.has(fieldId) || assignedFields.has(fieldId)) return false;
        assignedFields.add(fieldId);
        return true;
      }),
    }));
    const unassignedFields = this.data()
      .fields.map((field) => field.id)
      .filter((fieldId) => !assignedFields.has(fieldId));
    if (unassignedFields.length)
      steps.push({
        id: 'additional',
        label: 'Informations complémentaires',
        fieldIds: unassignedFields,
      });
    return steps;
  }

  protected formFor(stepId: string): FormGroup<RecordFormControls> {
    return this.stepForms[stepId] ?? new FormGroup<RecordFormControls>({});
  }

  protected fieldsFor(step: RecordFormStep): readonly RecordFormField[] {
    return step.fieldIds
      .map((fieldId) => this.data().fields.find((field) => field.id === fieldId))
      .filter((field): field is RecordFormField => field !== undefined);
  }

  hasUnsavedChanges(): boolean {
    return Object.values(this.stepForms).some((form) => form.dirty);
  }

  protected advance(stepper: MatStepper, step: RecordFormStep, last: boolean): void {
    const form = this.formFor(step.id);
    form.markAllAsTouched();
    if (this.busy() || !form.valid || !this.stepFiltersValid(step)) return;
    if (!last) {
      stepper.next();
      return;
    }
    if (this.data().review === false) this.save();
    else stepper.next();
  }

  protected reviewLegacy(): void {
    const step = this.steps()[0];
    const form = this.formFor(step.id);
    form.markAllAsTouched();
    if (form.valid && this.stepFiltersValid(step)) this.reviewing = true;
  }

  protected save(): void {
    if (
      this.busy() ||
      Object.values(this.stepForms).some((form) => !form.valid) ||
      this.steps().some((step) => !this.stepFiltersValid(step))
    )
      return;
    const values: Record<string, string> = {};
    for (const form of Object.values(this.stepForms))
      Object.assign(values, form.getRawValue() as Record<string, string>);
    const visibleValues: Record<string, string> = {};
    for (const [fieldId, value] of Object.entries(values)) {
      const field = this.data().fields.find((item) => item.id === fieldId);
      if (field && this.isFieldVisible(field)) visibleValues[fieldId] = value;
    }
    this.submitted.emit(visibleValues);
  }

  protected fieldError(field: RecordFormField): string {
    const control = this.controlFor(field.id);
    if (!control || !control.touched || control.valid) return '';
    if (control.hasError('required')) return 'Ce champ est obligatoire.';
    if (control.hasError('email')) return 'Saisissez une adresse e-mail valide.';
    if (control.hasError('minlength')) return `Saisissez au moins ${field.minLength} caractères.`;
    if (control.hasError('maxlength')) return `Saisissez au maximum ${field.maxLength} caractères.`;
    if (control.hasError('min')) return `La valeur minimale est ${field.min}.`;
    if (control.hasError('max')) return `La valeur maximale est ${field.max}.`;
    if (control.hasError('pattern')) return 'Le format saisi est invalide.';
    return 'Vérifiez cette valeur.';
  }

  protected fieldHint(field: RecordFormField): string {
    return (
      field.hint ??
      (field.filterOptionsByFieldId ? 'Les choix proposés dépendent du type de média.' : '')
    );
  }

  protected optionsFor(field: RecordFormField): readonly RecordFormOption[] {
    const filterFieldId = field.filterOptionsByFieldId;
    if (!filterFieldId) return field.options ?? [];
    const mediaType = this.controlFor(filterFieldId)?.value;
    return (field.options ?? []).filter(
      (option) => !option.mediaTypes?.length || option.mediaTypes.includes(mediaType ?? ''),
    );
  }

  protected onSelectChange(field: RecordFormField): void {
    for (const dependent of this.data().fields.filter(
      (item) => item.filterOptionsByFieldId === field.id,
    )) {
      const control = this.controlFor(dependent.id);
      const value = control?.value;
      const option = dependent.options?.find((item) => item.value === value);
      if (
        !option?.mediaTypes?.length ||
        option.mediaTypes.includes(this.controlFor(field.id)?.value ?? '')
      )
        continue;
      control?.setValue('');
    }
  }

  protected isFieldVisible(field: RecordFormField): boolean {
    if (!field.filterOptionsByFieldId) return true;
    return this.optionsFor(field).some(
      (option) => option.value === this.controlFor(field.id)?.value,
    );
  }

  protected displayValue(fieldId: string): string {
    const field = this.data().fields.find((item) => item.id === fieldId);
    const value = String(this.controlFor(fieldId)?.value ?? '');
    if (field?.type === 'password') return '••••••••';
    return field?.options?.find((option) => option.value === value)?.label ?? value;
  }

  private stepFiltersValid(step: RecordFormStep): boolean {
    return this.fieldsFor(step).every((field) => {
      if (!field.filterOptionsByFieldId) return true;
      const option = field.options?.find((item) => item.value === this.controlFor(field.id)?.value);
      if (!option) return false;
      if (!option.mediaTypes?.length) return true;
      return option.mediaTypes.includes(this.controlFor(field.filterOptionsByFieldId)?.value ?? '');
    });
  }

  protected controlFor(fieldId: string): FormControl<string> | undefined {
    for (const form of Object.values(this.stepForms)) {
      const control = form.controls[fieldId];
      if (control) return control;
    }
    return undefined;
  }
}
