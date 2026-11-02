import { ConfigurationReferenceFieldComponent } from './configuration-reference-field/configuration-reference-field.component';
import { ConfigurationChoice } from '../models/configuration-choice';
import { ConfigurationRecordListComponent } from './configuration-record-list/configuration-record-list.component';
import { FlowConfigurationRecord } from '../models/flow-component';
import { NgTemplateOutlet } from '@angular/common';
import { flowFieldValidator } from './flow-configuration-controls';
import {
  flowConfigurationValue,
  isFlowConfigurationFieldVisible,
} from '../models/flow-configuration';
import {
  ChangeDetectionStrategy,
  Component,
  OnChanges,
  SimpleChanges,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { firstValueFrom, Subscription } from 'rxjs';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { ConditionTreeEditorComponent } from './condition-tree-editor.component';
import { ConditionGroup } from '../models/condition-tree';
import { FlowScripts } from '../services/flow-scripts';
import { FlowScriptSummary } from '../models/flow-scripts';
import { EncodingProfiles } from '../services/encoding-profiles';
import {
  EncodingProfileSnapshot,
  EncodingProfileSummary,
  VideoEncoderDescriptor,
} from '../models/encoding-profiles';
import { CodeEditorComponent } from './code-editor.component';
import { SchemaFormFieldComponent } from './schema-form-field.component';
import {
  FlowComponentConfiguration,
  FlowComponentConfigurationField,
  FlowComponentConfigurationSchema,
  FlowComponentConfigurationValue,
} from '../models/flow-component';

type ConfigurationFormGroup = FormGroup<
  Record<string, FormControl<FlowComponentConfigurationValue>>
>;

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-component-configuration-form',
  imports: [
    NgTemplateOutlet,
    ConfigurationRecordListComponent,
    ConfigurationReferenceFieldComponent,
    MatButtonModule,
    MatCheckboxModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    ReactiveFormsModule,
    ConditionTreeEditorComponent,
    CodeEditorComponent,
    SchemaFormFieldComponent,
  ],
  template: `
    <form class="configuration-form" [formGroup]="form" (ngSubmit)="save()">
      @if (instanceName() !== undefined) {
        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label>Nom du bloc</mat-label>
          <input matInput type="text" [formControl]="nameControl" maxlength="128" />
          <mat-hint>Facultatif : un nom adapté au rôle de ce bloc dans le flow.</mat-hint>
        </mat-form-field>
      }
      @if (fields.length) {
        <ng-container
          [ngTemplateOutlet]="fieldTemplate"
          [ngTemplateOutletContext]="{ visibleFields: primaryFields() }"
        />
        @if (advancedFields().length) {
          <details class="advanced-settings" [open]="advancedInvalid()">
            <summary>Réglages avancés · {{ advancedFields().length }}</summary>
            <ng-container
              [ngTemplateOutlet]="fieldTemplate"
              [ngTemplateOutletContext]="{ visibleFields: advancedFields() }"
            />
          </details>
        }
        <ng-template #fieldTemplate let-visibleFields="visibleFields">
          <div class="configuration-fields">
            @for (field of visibleFields; track field.id) {
              @if (isFieldVisible(field)) {
                @if (field.section && visibleFields[$index - 1]?.section !== field.section) {
                  <h3>{{ field.section }}</h3>
                }
                @switch (field.type) {
                  @case ('record-list') {
                    <app-configuration-record-list
                      [field]="field"
                      [value]="recordListValue(field.id)"
                      [disabled]="busy()"
                      (valueChange)="setRecordListValue(field.id, $event)"
                    />
                    @if (configurationFieldError(field.id)) {
                      <p role="alert">{{ configurationFieldError(field.id) }}</p>
                    }
                  }
                  @case ('condition-tree') {
                    <section
                      class="condition-field"
                      [attr.aria-label]="field.label"
                      [attr.inert]="busy() ? '' : null"
                    >
                      <h3>{{ field.label }}</h3>
                      @if (field.description) {
                        <p>{{ field.description }}</p>
                      }
                      <app-condition-tree-editor
                        [value]="conditionValue(field.id)"
                        [fields]="field.conditionFields"
                        [presets]="field.conditionPresets ?? []"
                        (valueChange)="setConditionValue(field.id, $event)"
                      />
                    </section>
                  }
                  @case ('boolean') {
                    <div class="boolean-field">
                      <mat-checkbox [formControlName]="field.id">{{ field.label }}</mat-checkbox>
                      @if (field.description) {
                        <p class="field-description">{{ field.description }}</p>
                      }
                    </div>
                  }
                  @case ('select') {
                    <app-schema-form-field
                      [label]="field.label"
                      type="select"
                      [control]="form.controls[field.id]"
                      [options]="field.options"
                      [hint]="field.description ?? ''"
                      [error]="configurationFieldError(field.id)"
                      (selectionChanged)="onSelectFieldChanged(field.id)"
                    />
                  }
                  @case ('script-reference') {
                    <mat-form-field appearance="outline" subscriptSizing="dynamic">
                      <mat-label>{{ field.label }}</mat-label>
                      <mat-select
                        [formControlName]="field.id"
                        [disabled]="busy() || scriptSelectionBusy()"
                        (selectionChange)="selectScriptVersion(field.id, $event.value)"
                      >
                        <mat-option value="">Code intégré au flow</mat-option>
                        @for (script of scripts(); track script.id) {
                          @for (version of script.versions; track version.version) {
                            <mat-option [value]="script.id + '@' + version.version">
                              {{ version.name }} · v{{ version.version }} ·
                              {{ version.language === 'python' ? 'Python' : 'JavaScript' }}
                            </mat-option>
                          }
                        }
                        @if (scripts().length === 0) {
                          <mat-option disabled
                            >Aucune version publiée. Créez-en une dans Librairies /
                            Scripts.</mat-option
                          >
                        }
                      </mat-select>
                      @if (scriptLoadMessage()) {
                        <mat-hint>{{ scriptLoadMessage() }}</mat-hint>
                      } @else if (field.description) {
                        <mat-hint>{{ field.description }}</mat-hint>
                      }
                    </mat-form-field>
                  }
                  @case ('encoding-profile') {
                    <mat-form-field appearance="outline" subscriptSizing="dynamic">
                      <mat-label>{{ field.label }}</mat-label>
                      <mat-select
                        [value]="profileReference(field.id)"
                        [disabled]="busy() || encodingProfileBusy()"
                        (selectionChange)="selectEncodingProfile(field.id, $event.value)"
                      >
                        @for (profile of encodingProfiles(); track profile.id) {
                          @for (version of profile.versions; track version.version) {
                            <mat-option [value]="profile.id + '@' + version.version">
                              {{ version.name }} · v{{ version.version }}
                              @if (profile.isBuiltIn) {
                                · Intégré
                              }
                            </mat-option>
                          }
                        }
                      </mat-select>
                      @if (encodingProfileMessage()) {
                        <mat-hint>{{ encodingProfileMessage() }}</mat-hint>
                      } @else if (field.description) {
                        <mat-hint>{{ field.description }}</mat-hint>
                      }
                    </mat-form-field>
                    @if (field.id === 'encodingProfile' && profilePreviewEncoders().length) {
                      <section
                        class="profile-target-preview"
                        aria-label="Réglages du profil pour cet encodeur"
                      >
                        <h3>{{ profilePreviewTitle() }}</h3>
                        @if (textValue('encoderMode') !== 'specific') {
                          <p>
                            Le worker utilisera le premier encodeur vérifié disponible selon la
                            priorité du profil.
                          </p>
                        }
                        @for (encoder of profilePreviewEncoders(); track encoder.id) {
                          @if (profileTarget(encoder.id); as target) {
                            <div class="profile-target-row">
                              <strong>{{ encoder.name }}</strong>
                              <span
                                >{{ encoder.qualityMode }} ({{ encoder.qualityOption }}) :
                                {{ target.quality }}</span
                              >
                              @if (encoder.presetOption && target.preset) {
                                <span
                                  >{{ encoder.presetOption }} :
                                  {{ profilePreset(encoder, target.preset) }}</span
                                >
                              }
                              <span>Priorité {{ target.priority + 1 }}</span>
                            </div>
                          }
                        }
                      </section>
                    }
                  }
                  @case ('video-encoder') {
                    <mat-form-field appearance="outline" subscriptSizing="dynamic">
                      <mat-label>{{ field.label }}</mat-label>
                      <mat-select [formControlName]="field.id">
                        @for (encoder of availableProfileEncoders(); track encoder.id) {
                          <mat-option [value]="encoder.id">{{ encoder.name }}</mat-option>
                        }
                      </mat-select>
                      <mat-hint
                        >Seuls les encodeurs réglés dans le profil pour ce codec sont
                        proposés.</mat-hint
                      >
                    </mat-form-field>
                  }
                  @case ('number') {
                    <app-schema-form-field
                      [label]="field.label"
                      type="number"
                      [numericValue]="true"
                      [control]="form.controls[field.id]"
                      [hint]="field.description ?? ''"
                      [error]="configurationFieldError(field.id)"
                      [min]="field.min"
                      [max]="field.max"
                      [step]="field.step"
                    />
                  }
                  @case ('text') {
                    @if (field.editor) {
                      <app-configuration-reference-field
                        [field]="field"
                        [value]="textValue(field.id)"
                        [disabled]="busy()"
                        [contextOptions]="referenceOptions()[field.editor] ?? []"
                        [error]="configurationFieldError(field.id)"
                        (valueChange)="setTextValue(field.id, $event)"
                      />
                    } @else if (field.id === 'code') {
                      <section class="code-field" [attr.aria-label]="field.label">
                        <h3>{{ field.label }}</h3>
                        @if (field.description) {
                          <p class="field-description">{{ field.description }}</p>
                        }
                        <app-code-editor
                          [value]="textValue(field.id)"
                          [language]="scriptLanguage()"
                          [disabled]="busy()"
                          [label]="field.label"
                          (valueChange)="setTextValue(field.id, $event)"
                        />
                        @if (form.controls[field.id].invalid && form.controls[field.id].touched) {
                          <p class="field-error">
                            {{ form.controls[field.id].errors?.['configuration'] }}
                          </p>
                        }
                      </section>
                    } @else {
                      <app-schema-form-field
                        [label]="field.label"
                        [type]="field.multiline ? 'textarea' : 'text'"
                        [control]="form.controls[field.id]"
                        [hint]="field.description ?? ''"
                        [error]="configurationFieldError(field.id)"
                        [placeholder]="field.placeholder ?? ''"
                        [rows]="6"
                      />
                    }
                  }
                  @case ('secret') {
                    <app-schema-form-field
                      [label]="field.label"
                      type="password"
                      autocomplete="new-password"
                      [control]="form.controls[field.id]"
                      [hint]="
                        configuredSecrets().includes(field.id)
                          ? 'Laissez vide pour conserver le secret enregistré.'
                          : (field.description ?? '')
                      "
                      [error]="configurationFieldError(field.id)"
                      [placeholder]="field.placeholder ?? ''"
                    />
                    @if (configuredSecrets().includes(field.id)) {
                      <mat-checkbox
                        [checked]="secretsToClear.has(field.id)"
                        [disabled]="field.required && !form.controls[field.id].value"
                        (change)="setSecretCleared(field.id, $event.checked)"
                      >
                        Supprimer le secret enregistré
                      </mat-checkbox>
                    }
                  }
                }
              }
            }
          </div>
        </ng-template>
      } @else {
        <p class="empty-configuration">Ce composant ne demande aucun réglage.</p>
      }

      @if (progressiveDisclosure()) {
        <section aria-label="Résumé des paramètres">
          <h3>Configuration effective</h3>
          <p>
            Les valeurs du formulaire incluent les valeurs par défaut du composant. Les champs
            masqués sont conservés.
          </p>
          <ul>
            @for (line of configurationSummary(); track line) {
              <li>{{ line }}</li>
            }
          </ul>
        </section>
      }
      @if (showActions()) {
        <div class="form-actions">
          <ng-content select="[form-secondary-actions]" />
          <button matButton="text" type="button" (click)="cancelled.emit()">Annuler</button>
          @if (showConnectionTest()) {
            <button
              matButton="outlined"
              type="button"
              [disabled]="busy() || form.invalid || connectionTestBusy()"
              (click)="testConnection()"
            >
              {{ connectionTestBusy() ? 'Test en cours…' : 'Tester la connexion' }}
            </button>
          }
          <button
            matButton="filled"
            type="submit"
            [disabled]="busy() || scriptSelectionBusy() || form.invalid || nameControl.invalid"
          >
            {{ submitLabel() }}
          </button>
        </div>
      }
      @if (connectionTestMessage()) {
        <p
          class="connection-test-result"
          [class.connection-test-failed]="!connectionTestSuccess()"
          aria-live="polite"
        >
          {{ connectionTestMessage() }}
        </p>
      }
    </form>
  `,
  styles: `
    :host {
      display: block;
    }
    .configuration-form {
      display: grid;
      gap: var(--space-4);
    }
    .advanced-settings {
      margin-block: var(--space-4);
      padding: var(--space-3);
      border-radius: var(--radius-lg);
      background: var(--surface-container-low);
    }
    .advanced-settings summary {
      cursor: pointer;
      padding: 10px 0;
      font-weight: 600;
    }
    .configuration-fields {
      display: grid;
      gap: var(--space-2);
    }
    mat-form-field {
      width: 100%;
    }
    .boolean-field {
      padding: var(--space-2) 0;
    }
    .field-description {
      margin: var(--space-1) 0 0 var(--space-6);
      color: var(--text-muted);
      font-size: var(--font-body);
    }
    .profile-target-preview {
      display: grid;
      gap: var(--space-2);
      padding: var(--space-3);
      border: 1px solid var(--border-subtle);
      border-radius: var(--radius-md);
      background: var(--surface-muted);
    }
    .profile-target-preview h3 {
      margin: 0;
      color: var(--text-primary);
      font-size: var(--font-body);
    }
    .profile-target-preview p {
      margin: 0;
      color: var(--text-secondary);
      font-size: var(--font-body);
      line-height: 1.45;
    }
    .profile-target-row {
      display: grid;
      gap: 3px;
      padding: var(--space-2) 10px;
      border-radius: var(--radius-sm);
      background: var(--surface);
      font-size: var(--font-body);
    }
    .profile-target-row strong {
      color: var(--text-primary);
    }
    .profile-target-row span {
      color: var(--text-secondary);
    }
    .empty-configuration {
      margin: 0;
      color: var(--text-secondary);
    }
    .form-actions {
      display: flex;
      justify-content: flex-end;
      align-items: center;
      flex-wrap: wrap;
      gap: var(--space-2);
      padding-top: var(--space-4);
    }
    .connection-test-result {
      margin: var(--space-2) 0 0;
      color: var(--color-success, #237a45);
    }
    .connection-test-failed {
      color: var(--color-error, #b3261e);
    }
    @media (max-width: 600px) {
      .form-actions {
        position: sticky;
        bottom: 0;
        margin-inline: -16px;
        padding: var(--space-3) var(--space-4);
        background: var(--surface);
        border-top: 1px solid var(--border-subtle);
      }
      .form-actions button {
        flex: 1 0 auto;
        white-space: nowrap;
      }
    }
  `,
})
export class ComponentConfigurationFormComponent implements OnChanges {
  private readonly scriptLibrary = inject(FlowScripts);
  private readonly profileLibrary = inject(EncodingProfiles);
  readonly referenceOptions = input<Readonly<Record<string, readonly ConfigurationChoice[]>>>({});
  readonly schema = input.required<FlowComponentConfigurationSchema | undefined>();
  readonly values = input<FlowComponentConfiguration>({});
  readonly configuredSecrets = input<readonly string[]>([]);
  readonly instanceName = input<string | undefined>();
  readonly instanceNameChange = output<string>();
  readonly submitLabel = input('Enregistrer');
  readonly showActions = input(true);
  readonly busy = input(false);
  readonly showConnectionTest = input(false);
  readonly connectionTestBusy = input(false);
  readonly connectionTestMessage = input('');
  readonly connectionTestSuccess = input(false);
  readonly progressiveDisclosure = input(false);
  protected primaryFields(): readonly FlowComponentConfigurationField[] {
    return this.progressiveDisclosure()
      ? this.fields.filter((field) => this.isPrimary(field) && this.isFieldVisible(field))
      : this.fields;
  }
  protected advancedFields(): readonly FlowComponentConfigurationField[] {
    return this.progressiveDisclosure()
      ? this.fields.filter((field) => !this.isPrimary(field) && this.isFieldVisible(field))
      : [];
  }
  private isPrimary(field: FlowComponentConfigurationField): boolean {
    return field.importance !== 'advanced';
  }
  protected configurationSummary(): readonly string[] {
    return this.fields
      .filter(
        (field) => this.isFieldVisible(field) && field.type !== 'secret' && field.id !== 'code',
      )
      .flatMap((field) => {
        const value = this.form.controls[field.id]?.value;
        if (value === null || value === undefined || value === '' || typeof value === 'object')
          return [];
        const label =
          field.type === 'select'
            ? (field.options.find((option) => option.value === value)?.label ?? String(value))
            : typeof value === 'boolean'
              ? value
                ? 'Oui'
                : 'Non'
              : String(value);
        return [field.label + ' : ' + label];
      });
  }
  protected advancedInvalid(): boolean {
    return this.advancedFields().some(
      (field) => this.isFieldVisible(field) && this.form.controls[field.id]?.invalid,
    );
  }
  readonly saved = output<FlowComponentConfiguration>();
  readonly configurationChange = output<FlowComponentConfiguration>();
  readonly cancelled = output<void>();
  readonly savedWithSecrets = output<{
    readonly values: FlowComponentConfiguration;
    readonly clearSecrets: readonly string[];
  }>();
  readonly connectionTestRequested = output<FlowComponentConfiguration>();

  protected fields: readonly FlowComponentConfigurationField[] = [];
  protected readonly scripts = signal<readonly FlowScriptSummary[]>([]);
  protected readonly encodingProfiles = signal<readonly EncodingProfileSummary[]>([]);
  protected readonly videoEncoders = signal<readonly VideoEncoderDescriptor[]>([]);
  protected readonly scriptLoadMessage = signal('');
  protected readonly scriptSelectionBusy = signal(false);
  protected readonly encodingProfileMessage = signal('');
  protected readonly encodingProfileBusy = signal(false);
  protected secretsToClear = new Set<string>();
  protected form: ConfigurationFormGroup = new FormGroup({});
  private formChanges?: Subscription;
  protected nameControl = new FormControl('', {
    nonNullable: true,
    validators: [Validators.maxLength(128)],
  });

  ngOnChanges(changes: SimpleChanges): void {
    if (
      changes['schema'] ||
      changes['values'] ||
      changes['instanceName'] ||
      changes['configuredSecrets']
    ) {
      this.secretsToClear = new Set();
      this.nameControl.setValue(this.instanceName() ?? '');
      this.fields = (this.schema()?.fields ?? []).filter(
        (field) =>
          !field.legacyOnly || Object.prototype.hasOwnProperty.call(this.values(), field.id),
      );
      const controls: Record<string, FormControl<FlowComponentConfigurationValue>> = {};
      for (const field of this.fields) {
        controls[field.id] = new FormControl(flowConfigurationValue(field, this.values()), {
          validators: flowFieldValidator(field),
        });
      }
      this.form = new FormGroup(controls);
      this.formChanges?.unsubscribe();
      this.formChanges = this.form.valueChanges.subscribe(() =>
        this.configurationChange.emit({ ...this.values(), ...this.form.getRawValue() }),
      );
      if (this.fields.some((field) => field.type === 'script-reference')) this.loadScripts();
      if (this.fields.some((field) => field.type === 'encoding-profile'))
        this.loadEncodingProfiles();
    }
    if (this.busy()) {
      this.form.disable({ emitEvent: false });
      this.nameControl.disable({ emitEvent: false });
    } else {
      this.form.enable({ emitEvent: false });
      this.nameControl.enable({ emitEvent: false });
    }
  }

  protected save(): void {
    if (this.busy() || this.form.invalid || this.nameControl.invalid) return;
    if (this.instanceName() !== undefined)
      this.instanceNameChange.emit(this.nameControl.value.trim());
    const values = { ...this.values(), ...this.form.getRawValue() };
    this.saved.emit(values);
    this.savedWithSecrets.emit({ values, clearSecrets: [...this.secretsToClear] });
  }

  protected testConnection(): void {
    if (this.busy() || this.form.invalid || this.connectionTestBusy()) return;
    this.connectionTestRequested.emit(this.form.getRawValue());
  }

  protected recordListValue(id: string): readonly FlowConfigurationRecord[] {
    const value = this.form.controls[id]?.value;
    return Array.isArray(value) ? value : [];
  }
  protected setRecordListValue(id: string, value: readonly FlowConfigurationRecord[]): void {
    this.form.controls[id].setValue(value);
    this.form.controls[id].markAsDirty();
  }
  protected conditionValue(fieldId: string): ConditionGroup | undefined {
    const value = this.form.controls[fieldId]?.value;
    return value && typeof value === 'object' && 'kind' in value ? value : undefined;
  }
  protected setConditionValue(fieldId: string, value: ConditionGroup): void {
    if (this.busy()) return;
    const control = this.form.controls[fieldId];
    control.setValue(value);
    control.markAsDirty();
    control.markAsTouched();
  }

  protected setSecretCleared(fieldId: string, cleared: boolean): void {
    if (this.busy()) return;
    const updated = new Set(this.secretsToClear);
    if (cleared) updated.add(fieldId);
    else updated.delete(fieldId);
    this.secretsToClear = updated;
  }

  protected scriptLanguage(): 'javascript' | 'python' {
    return this.form.controls['language']?.value === 'python' ? 'python' : 'javascript';
  }

  protected isFieldVisible(field: FlowComponentConfigurationField): boolean {
    return isFlowConfigurationFieldVisible(field, this.form.getRawValue());
  }

  protected profileReference(fieldId: string): string {
    const value = this.form.controls[fieldId]?.value;
    return isEncodingProfileSnapshot(value) ? `${value.id}@${value.version}` : '';
  }

  protected availableProfileEncoders(): readonly VideoEncoderDescriptor[] {
    const profile = this.form.controls['encodingProfile']?.value;
    const codec = this.form.controls['videoCodec']?.value;
    if (!isEncodingProfileSnapshot(profile) || typeof codec !== 'string') return [];
    const configured = new Set(profile.targets.map((target) => target.encoderId));
    return this.videoEncoders().filter(
      (encoder) => encoder.codec === codec && configured.has(encoder.id),
    );
  }

  protected profilePreviewEncoders(): readonly VideoEncoderDescriptor[] {
    const encoders = this.availableProfileEncoders();
    if (this.textValue('encoderMode') === 'specific') {
      const selected = this.form.controls['encoderId']?.value;
      return encoders.filter((encoder) => encoder.id === selected);
    }
    return [...encoders].sort(
      (left, right) =>
        (this.profileTarget(left.id)?.priority ?? Number.MAX_SAFE_INTEGER) -
        (this.profileTarget(right.id)?.priority ?? Number.MAX_SAFE_INTEGER),
    );
  }

  protected profilePreviewTitle(): string {
    return this.textValue('encoderMode') === 'specific'
      ? 'Réglages de l’encodeur sélectionné'
      : 'Cibles du profil pour ce codec';
  }

  protected profileTarget(encoderId: string) {
    const profile = this.form.controls['encodingProfile']?.value;
    return isEncodingProfileSnapshot(profile)
      ? profile.targets.find((target) => target.encoderId === encoderId)
      : undefined;
  }

  protected profilePreset(encoder: VideoEncoderDescriptor, presetId: string): string {
    return encoder.presets.find((preset) => preset.id === presetId)?.value ?? presetId;
  }

  protected async selectEncodingProfile(fieldId: string, reference: string): Promise<void> {
    const separator = reference.lastIndexOf('@');
    if (separator <= 0) return;
    this.encodingProfileBusy.set(true);
    this.encodingProfileMessage.set('Chargement de la version sélectionnée…');
    try {
      const version = await this.profileLibrary.version(
        reference.slice(0, separator),
        Number(reference.slice(separator + 1)),
      );
      const snapshot: EncodingProfileSnapshot = {
        id: version.profileId,
        version: version.version,
        name: version.name,
        description: version.description,
        targets: version.targets,
      };
      this.form.controls[fieldId]?.setValue(snapshot);
      this.encodingProfileMessage.set(
        `Profil ${version.name} v${version.version} intégré à la définition du flow.`,
      );
      this.selectFirstCompatibleEncoder();
    } catch {
      this.encodingProfileMessage.set('Impossible de charger cette version de profil.');
    } finally {
      this.encodingProfileBusy.set(false);
    }
  }

  protected onSelectFieldChanged(fieldId: string): void {
    if (fieldId === 'videoCodec' || fieldId === 'encoderMode') this.selectFirstCompatibleEncoder();
  }

  protected textValue(fieldId: string): string {
    const value = this.form.controls[fieldId]?.value;
    return typeof value === 'string' ? value : '';
  }

  protected setTextValue(fieldId: string, value: string): void {
    if (this.busy()) return;
    const control = this.form.controls[fieldId];
    control?.setValue(value);
    control?.markAsDirty();
    control?.markAsTouched();
  }

  protected configurationFieldError(fieldId: string): string {
    const control = this.form.controls[fieldId];
    return control.invalid && control.touched
      ? String(control.errors?.['configuration'] ?? 'Vérifiez cette valeur.')
      : '';
  }

  protected async selectScriptVersion(fieldId: string, reference: string): Promise<void> {
    if (!reference) {
      this.scriptLoadMessage.set('');
      return;
    }
    const split = reference.lastIndexOf('@');
    if (split <= 0) return;
    this.scriptSelectionBusy.set(true);
    this.scriptLoadMessage.set('Chargement de la version sélectionnée…');
    try {
      const version = await this.scriptLibrary.version(
        reference.slice(0, split),
        Number(reference.slice(split + 1)),
      );
      this.form.controls[fieldId]?.setValue(reference);
      this.form.controls['language']?.setValue(version.language);
      this.form.controls['code']?.setValue(version.code);
      this.form.controls['code']?.markAsDirty();
      this.scriptLoadMessage.set(
        `Code de ${version.name} v${version.version} copié dans cette version du flow.`,
      );
    } catch {
      this.form.controls[fieldId]?.setValue('');
      this.scriptLoadMessage.set('Impossible de charger cette version de script.');
    } finally {
      this.scriptSelectionBusy.set(false);
    }
  }

  private loadScripts(): void {
    this.scriptLoadMessage.set('Chargement des scripts publiés…');
    firstValueFrom(this.scriptLibrary.list()).then(
      (scripts) => {
        this.scripts.set(scripts.filter((script) => script.versions.length > 0));
        this.scriptLoadMessage.set('');
      },
      () => this.scriptLoadMessage.set('Les scripts publiés ne sont pas disponibles.'),
    );
  }

  private loadEncodingProfiles(): void {
    this.encodingProfileMessage.set('Chargement des profils publiés…');
    Promise.all([
      firstValueFrom(this.profileLibrary.list()),
      firstValueFrom(this.profileLibrary.encoders()),
    ]).then(
      ([profiles, encoders]) => {
        this.encodingProfiles.set(profiles.filter((profile) => profile.versions.length > 0));
        this.videoEncoders.set(encoders);
        this.encodingProfileMessage.set('');
        this.selectFirstCompatibleEncoder();
      },
      () => this.encodingProfileMessage.set('Les profils d’encodage ne sont pas disponibles.'),
    );
  }

  private selectFirstCompatibleEncoder(): void {
    const mode = this.form.controls['encoderMode']?.value;
    if (mode !== 'specific') return;
    const options = this.availableProfileEncoders();
    const selected = this.form.controls['encoderId']?.value;
    if (options.some((encoder) => encoder.id === selected)) return;
    this.form.controls['encoderId']?.setValue(options[0]?.id ?? '');
  }
}

function isEncodingProfileSnapshot(value: unknown): value is EncodingProfileSnapshot {
  return (
    typeof value === 'object' &&
    value !== null &&
    'targets' in value &&
    Array.isArray(value.targets)
  );
}
