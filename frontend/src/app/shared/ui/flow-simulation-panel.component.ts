import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MatExpansionModule } from '@angular/material/expansion';
import { ComponentConfigurationFormComponent } from './component-configuration-form.component';
import { DetailField, DetailFieldListComponent } from './detail-field-list.component';
import { FileImportComponent } from './file-import.component';
import {
  FlowComponentConfiguration,
  FlowComponentConfigurationSchema,
  FlowGraphNode,
} from '../models/flow-component';
import {
  SIMULATION_PROFILE_SCHEMA,
  SimulationResult,
  SimulationStep,
} from '../models/flow-simulation';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-flow-simulation-panel',
  imports: [
    MatExpansionModule,
    ComponentConfigurationFormComponent,
    DetailFieldListComponent,
    FileImportComponent,
  ],
  template: `
    <p class="simulation-intro">
      Le test suit le flow en cours, y compris les modifications non enregistrées. Les conditions
      natives sont évaluées. Les traitements, scripts, intégrations et relais utilisent vos
      résultats fictifs.
    </p>
    @if (error()) {
      <p class="simulation-error" role="alert">{{ error() }}</p>
    }
    <mat-accordion multi>
      <mat-expansion-panel [expanded]="!result()">
        <mat-expansion-panel-header
          ><mat-panel-title>Fichier et métadonnées</mat-panel-title></mat-expansion-panel-header
        >
        <app-file-import
          label="Lire un fichier avec MediaInfo"
          [busy]="busy()"
          description="Le fichier est envoyé au serveur uniquement pour lire ses métadonnées, puis la copie est supprimée. Maximum : 512 Mio, deux minutes. Les données importées restent modifiables."
          (selected)="fileSelected.emit($event)"
        />
        <app-component-configuration-form
          [schema]="profileSchema"
          [values]="profile()"
          [busy]="busy()"
          submitLabel="Lancer le test"
          (saved)="runRequested.emit($event)"
          (cancelled)="closed.emit()"
        />
      </mat-expansion-panel>
      @if (result(); as test) {
        <mat-expansion-panel expanded>
          <mat-expansion-panel-header
            ><mat-panel-title
              >Parcours simulé · {{ statusLabel() }}</mat-panel-title
            ></mat-expansion-panel-header
          >
          <p role="status">{{ test.detail }}</p>
          <mat-accordion multi>
            @for (item of trace(); track item.nodeId) {
              <mat-expansion-panel>
                <mat-expansion-panel-header>
                  <mat-panel-title>{{ item.title }} · {{ item.output }}</mat-panel-title>
                </mat-expansion-panel-header>
                <app-detail-field-list [fields]="item.fields" />
              </mat-expansion-panel>
            }
          </mat-accordion>
          @if (test.issues?.length) {
            <app-detail-field-list [fields]="issueFields()" />
          }
        </mat-expansion-panel>
      }
    </mat-accordion>
    @if (waitingNode(); as node) {
      <h3>Résultat fictif : {{ node.name ?? node.component.name }}</h3>
      <p>{{ node.component.description }}</p>
      <app-component-configuration-form
        [schema]="assumptionSchema()"
        [values]="assumptionDefaults()"
        [busy]="busy()"
        submitLabel="Poursuivre avec ce résultat"
        (saved)="continueRequested.emit($event)"
        (cancelled)="closed.emit()"
      />
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--space-4);
      min-width: 0;
    }
    .simulation-intro {
      margin: 0;
      padding: 12px 16px;
      border: 1px solid var(--border-subtle);
      border-left: 3px solid var(--accent);
      border-radius: var(--radius-md);
      background: var(--surface-container);
      line-height: 1.5;
      color: var(--text-secondary);
    }
    .simulation-error {
      margin: 0;
      padding: 10px 14px;
      border: 1px solid var(--danger);
      border-radius: var(--radius-sm);
      background: var(--danger-soft);
      color: var(--danger);
    }
    p {
      margin: 0 0 var(--space-4);
      line-height: 1.5;
      color: var(--text-secondary);
    }
    h3 {
      margin: 0;
      font-size: var(--font-title);
    }
    mat-panel-title {
      white-space: normal;
    }
  `,
})
export class FlowSimulationPanelComponent {
  readonly nodes = input.required<readonly FlowGraphNode[]>();
  readonly profile = input.required<FlowComponentConfiguration>();
  readonly result = input<SimulationResult | null>(null);
  readonly busy = input(false);
  readonly error = input('');
  readonly runRequested = output<FlowComponentConfiguration>();
  readonly continueRequested = output<FlowComponentConfiguration>();
  readonly fileSelected = output<File>();
  readonly closed = output<void>();
  protected readonly profileSchema = SIMULATION_PROFILE_SCHEMA;
  protected readonly waitingNode = computed(() =>
    this.nodes().find((node) => node.id === this.result()?.waitingNodeId),
  );
  protected readonly statusLabel = computed(
    () =>
      ({
        invalid: 'Flow invalide',
        waiting: 'Résultat à préciser',
        failed: 'Échec',
        completed: 'Terminé',
      })[this.result()?.status ?? 'waiting'],
  );
  protected readonly trace = computed(
    () =>
      this.result()?.steps.map((step, index) => ({
        nodeId: step.nodeId,
        title: `${index + 1}. ${step.name}`,
        output:
          this.nodes()
            .find((node) => node.id === step.nodeId)
            ?.component.outputs.find((output) => output.id === step.outputId)?.label ??
          step.outputId ??
          'Fin',
        fields: this.stepFields(step),
      })) ?? [],
  );
  private stepFields(step: SimulationStep): readonly DetailField[] {
    const schema = this.nodes().find((node) => node.id === step.nodeId)?.component
      .configurationSchema;
    const field = schema?.fields.find((field) => field.type === 'condition-tree');
    return [
      { label: step.assumed ? 'Hypothèse du scénario' : 'Décision du moteur', value: step.detail },
      ...(step.conditions ?? []).map((observation, index) => {
        const definition =
          field?.type === 'condition-tree'
            ? field.conditionFields.find((field) => field.id === observation.rule.field)
            : undefined;
        const label = definition?.label ?? observation.rule.field;
        const operator =
          definition?.operators.find((op) => op.value === observation.rule.operator)?.label ??
          observation.rule.operator;
        return {
          section: 'Conditions évaluées (avec arrêt dès que le groupe est décidé)',
          label: `${index + 1}. ${label}${observation.rule.variableName ? ' · ' + observation.rule.variableName : ''} · ${observation.rule.scope === 'source' ? 'Origine' : 'Courant'}`,
          value: `${JSON.stringify(observation.actual) ?? 'Absent'} ${operator} ${JSON.stringify(observation.rule.value) ?? ''} vers ${observation.error ?? (observation.result ? 'Oui' : 'Non')}`,
        };
      }),
      { section: 'Contexte après cette étape', label: 'Worker simulé', value: step.workerNodeId },
      { section: 'Contexte après cette étape', label: 'Extension', value: step.context.extension },
      {
        section: 'Contexte après cette étape',
        label: 'Plan en attente',
        value: step.context.plan.length,
      },
      {
        section: 'Contexte après cette étape',
        label: 'Variables',
        value: JSON.stringify(step.context.variables, null, 2),
      },
      {
        section: 'Contexte après cette étape',
        label: 'Métadonnées courantes',
        value: JSON.stringify(step.context.currentMetadata, null, 2),
      },
      {
        section: 'Contexte après cette étape',
        label: 'Métadonnées d’origine',
        value: JSON.stringify(step.context.beforeMetadata, null, 2),
      },
    ];
  }
  protected readonly issueFields = computed<readonly DetailField[]>(
    () =>
      this.result()?.issues?.map((issue) => ({ label: issue.path, value: issue.message })) ?? [],
  );
  protected readonly assumptionSchema = computed<FlowComponentConfigurationSchema>(() => {
    const node = this.waitingNode();
    return {
      version: 1,
      fields: [
        {
          id: 'outputId',
          type: 'select',
          label: 'Sortie à simuler',
          required: true,
          options:
            node?.component.outputs.map((output) => ({ value: output.id, label: output.label })) ??
            [],
        },
        {
          id: 'modified',
          type: 'boolean',
          label: 'Cette étape a produit un fichier transformé',
          description:
            'Active la condition « Le fichier a été transformé » ; ne modifie pas automatiquement les métadonnées.',
        },
        { id: 'extension', type: 'text', label: 'Nouvelle extension (facultatif)' },
        {
          id: 'metadata',
          type: 'text',
          label: 'Métadonnées après cette étape (JSON)',
          multiline: true,
          description:
            'Laissez vide pour conserver le profil courant. Objet avec format et streams, visible dans le parcours.',
        },
        {
          id: 'variables',
          type: 'text',
          label: 'Variables produites (objet JSON)',
          multiline: true,
          description: 'Fusionnées avec les variables existantes.',
        },
        ...(node?.component.id === 'nodes-handoff'
          ? [
              {
                id: 'workerNodeId',
                type: 'text' as const,
                label: 'Identifiant du worker qui prend le relais',
                required: true,
              },
            ]
          : []),
      ],
    };
  });
  protected readonly assumptionDefaults = computed<FlowComponentConfiguration>(() => {
    const node = this.waitingNode();
    return {
      outputId: '',
      modified: false,
      metadata: JSON.stringify(this.result()?.steps.at(-1)?.context.currentMetadata ?? {}, null, 2),
      variables: '{}',
      extension: '',
      workerNodeId: node?.configuration['targetNodeId'] ?? '',
    };
  });
}
