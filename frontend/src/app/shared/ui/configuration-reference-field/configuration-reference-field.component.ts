import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  output,
  signal,
  OnInit,
} from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { MatAutocompleteModule } from '@angular/material/autocomplete';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatChipsModule } from '@angular/material/chips';
import { MatButtonModule } from '@angular/material/button';
import { FlowComponentConfigurationField } from '../../models/flow-component';
import {
  ConfigurationChoice,
  configurationTokens,
  LANGUAGE_CHOICES,
} from '../../models/configuration-choice';
import { ClusterApi } from '../../services/cluster-api';
import { FlowResources } from '../../services/flow-resources';
import { NodeGroupResponse } from '../../models/cluster';

@Component({
  selector: 'app-configuration-reference-field',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    MatAutocompleteModule,
    MatFormFieldModule,
    MatInputModule,
    MatChipsModule,
    MatButtonModule,
  ],
  template: `
    <mat-form-field appearance="outline" subscriptSizing="dynamic">
      <mat-label>{{ field().label }}</mat-label>
      <input
        matInput
        [value]="multiple() ? draft() : label(value())"
        (focus)="query.set('')"
        [disabled]="disabled()"
        [matAutocomplete]="choices"
        (input)="edit($event)"
        (keydown.enter)="accept($event)"
      />
      <mat-autocomplete
        #choices="matAutocomplete"
        [displayWith]="displayReference"
        (optionSelected)="choose($event.option.value)"
      >
        @for (option of filtered(); track option.value) {
          <mat-option [value]="option.value">{{ option.label }}</mat-option>
        }
      </mat-autocomplete>
      <mat-hint>{{ field().description }} {{ loadMessage() }}</mat-hint>
    </mat-form-field>
    @if (error()) {
      <p role="alert">{{ error() }}</p>
    }
    @if (multiple()) {
      <button
        matButton="text"
        type="button"
        [disabled]="disabled() || !draft().trim()"
        (click)="choose(draft())"
      >
        Ajouter
      </button>
      <mat-chip-set [attr.aria-label]="field().label">
        @for (token of tokens(); track token) {
          <mat-chip [disabled]="disabled()" (removed)="remove(token)">
            {{ label(token)
            }}<button matChipRemove type="button" [attr.aria-label]="'Retirer ' + label(token)">
              ×
            </button>
          </mat-chip>
        }
      </mat-chip-set>
    }
    @if (field().editor === 'indices') {
      <p>Indices dans le repère indiqué par le bloc. Ils varient selon le média traité.</p>
    }
    @if (field().editor === 'artifact') {
      <p>
        Chemin relatif à la bibliothèque ou référence artifact:étape/index vers un fichier produit
        par une étape précédente.
      </p>
    }
    @if (field().editor === 'font-set') {
      <p>Nom du jeu installé sur les workers. Sa présence sera vérifiée à l’exécution.</p>
    }
    @if (field().editor === 'resource') {
      <p>
        Ressource du catalogue (Bibliothèques, Ressources). Le flow garde la version choisie ; les
        nœuds récupèrent les fichiers automatiquement.
      </p>
    }
    @if (field().editor === 'node') {
      <p>
        L’état affiché est observé. L’éligibilité dépend aussi des capacités et de l’accès à la
        bibliothèque.
      </p>
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--space-2);
      min-width: 0;
    }
    mat-form-field {
      width: 100%;
    }
    p {
      color: var(--text-secondary);
      font-size: var(--font-body);
      margin: 0;
    }
  `,
})
export class ConfigurationReferenceFieldComponent implements OnInit {
  private readonly cluster = inject(ClusterApi);
  private readonly http = inject(HttpClient);
  private readonly resourcesApi = inject(FlowResources);
  readonly field = input.required<FlowComponentConfigurationField>();
  readonly value = input('');
  readonly disabled = input(false);
  readonly error = input('');
  readonly contextOptions = input<readonly ConfigurationChoice[]>([]);
  readonly valueChange = output<string>();
  protected readonly draft = signal('');
  protected readonly query = signal('');
  protected readonly loadMessage = signal('');
  private readonly observed = signal<readonly ConfigurationChoice[]>([]);
  protected readonly multiple = computed(() =>
    ['languages', 'indices', 'labels'].includes(this.field().editor ?? ''),
  );
  protected readonly tokens = computed(() => configurationTokens(this.value()));
  private readonly options = computed(() => [
    ...this.contextOptions(),
    ...this.observed(),
    ...(['language', 'languages'].includes(this.field().editor ?? '') ? LANGUAGE_CHOICES : []),
  ]);
  protected readonly filtered = computed(() =>
    this.options().filter((option) =>
      (option.label + ' ' + option.value)
        .toLocaleLowerCase()
        .includes(this.query().toLocaleLowerCase()),
    ),
  );

  async ngOnInit(): Promise<void> {
    const editor = this.field().editor;
    if (
      editor !== 'node' &&
      editor !== 'node-group' &&
      editor !== 'font-set' &&
      editor !== 'resource'
    )
      return;
    try {
      if (editor === 'resource') {
        const summaries = await this.resourcesApi.list();
        const details = await Promise.all(
          summaries.map((summary) => this.resourcesApi.get(summary.id)),
        );
        const options = details.flatMap((resource) =>
          resource.versions.map((version) => ({
            value: `${resource.summary.id}:${version.version}`,
            label:
              `${resource.summary.name} · v${version.version}` +
              (version.version === resource.summary.latestVersion ? ' (dernière)' : ''),
          })),
        );
        this.observed.set(options);
        if (!options.length)
          this.loadMessage.set(
            'Aucune ressource dans le catalogue. Ajoutez des polices dans Bibliothèques, Ressources.',
          );
        return;
      }
      if (editor === 'font-set') {
        const nodes = (await firstValueFrom(this.cluster.listNodes())).filter((node) =>
          node.roles.includes('worker'),
        );
        const names = [...new Set(nodes.flatMap((node) => node.inventory?.fontSets ?? []))];
        this.observed.set(
          names.map((name) => ({
            value: name,
            label:
              name +
              ' · ' +
              nodes
                .filter((node) => node.inventory?.fontSets?.includes(name))
                .map((node) => (node.displayName || node.name) + ' (' + node.status + ')')
                .join(', '),
          })),
        );
        if (!names.length)
          this.loadMessage.set(
            'Aucun jeu de polices observé. Les workers anciens peuvent ne pas publier cet inventaire.',
          );
        return;
      }
      const options =
        editor === 'node'
          ? (await firstValueFrom(this.cluster.listNodes()))
              .filter((node) => node.roles.includes('worker'))
              .map((node) => ({
                value: node.id,
                label: (node.displayName || node.name) + ' · ' + node.status,
              }))
          : (
              await firstValueFrom(
                this.http.get<readonly NodeGroupResponse[]>('/api/v1/node-groups'),
              )
            ).map((group) => ({ value: group.id, label: group.name }));
      this.observed.set(options);
      if (!options.length)
        this.loadMessage.set(
          'Aucune ressource observée. La référence enregistrée reste conservée.',
        );
    } catch {
      this.loadMessage.set('Liste indisponible. Vous pouvez conserver ou saisir une référence.');
    }
  }
  protected readonly displayReference = (value: string): string =>
    this.multiple() ? value : this.label(value ?? '');
  protected label(value: string): string {
    return this.options().find((option) => option.value === value)?.label ?? value;
  }
  protected edit(event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.query.set(value);
    if (this.multiple()) this.draft.set(value);
    else this.valueChange.emit(value);
  }
  protected accept(event: Event): void {
    if (!this.multiple()) return;
    event.preventDefault();
    this.choose(this.draft());
  }
  protected choose(value: string): void {
    this.valueChange.emit(
      this.multiple() ? configurationTokens([...this.tokens(), value].join(',')).join(',') : value,
    );
    this.draft.set('');
    this.query.set('');
  }
  protected remove(value: string): void {
    this.valueChange.emit(
      this.tokens()
        .filter((item) => item !== value)
        .join(','),
    );
  }
}
