import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { SearchFieldComponent } from './search-field.component';
import {
  FLOW_COMPONENT_KIND_LABELS,
  FlowComponentDefinition,
  FlowComponentFilterOption,
} from '../models/flow-component';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-searchable-component-catalog',
  imports: [MatFormFieldModule, MatSelectModule, SearchFieldComponent],
  template: `
    <section class="component-catalog" aria-label="Catalogue des composants">
      <app-search-field
        class="search-field"
        variant="bar"
        label="Rechercher un composant"
        [value]="query()"
        (valueChange)="query.set($event)"
      />

      <div class="catalog-filters">
        <mat-form-field appearance="outline">
          <mat-label>Type de flow</mat-label>
          <mat-select [value]="selectedType()" (selectionChange)="selectedType.set($event.value)">
            <mat-option value="all">Tous les types</mat-option>
            @for (option of typeOptions(); track option.value) {
              <mat-option [value]="option.value">{{ option.label }}</mat-option>
            }
          </mat-select>
        </mat-form-field>

        <mat-form-field appearance="outline">
          <mat-label>Plugin</mat-label>
          <mat-select
            [value]="selectedPlugin()"
            (selectionChange)="selectedPlugin.set($event.value)"
          >
            <mat-option value="all">Tous les plugins</mat-option>
            @for (option of pluginOptions(); track option.value) {
              <mat-option [value]="option.value">{{ option.label }}</mat-option>
            }
          </mat-select>
        </mat-form-field>

        <mat-form-field appearance="outline">
          <mat-label>Outil média</mat-label>
          <mat-select [value]="selectedTool()" (selectionChange)="selectedTool.set($event.value)">
            <mat-option value="all">Tous les outils</mat-option>
            @for (option of toolOptions(); track option.value) {
              <mat-option [value]="option.value">{{ option.label }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
      </div>

      <div class="component-list" aria-live="polite">
        @for (
          component of filteredComponents();
          track component.pluginId +
            ':' +
            component.id +
            ':' +
            (component.reference?.pluginVersion ?? '') +
            ':' +
            (component.reference?.componentVersion ?? '')
        ) {
          <button class="component-option" type="button" (click)="selected.emit(component)">
            <span class="component-option-heading">
              <span class="component-name">{{ component.name }}</span>
              <span class="component-meta"
                >{{ kindLabel(component.kind) }} · {{ component.pluginName }}
                @if (component.executionToolName) {
                  · {{ component.executionToolName }}
                }
              </span>
            </span>
            <span class="component-description">{{ component.description }}</span>
            <span class="component-meta">{{ mediaTypeLabel(component.mediaType) }}</span>
          </button>
        } @empty {
          <p class="empty-state">Aucun composant ne correspond à ces filtres.</p>
        }
      </div>
    </section>
  `,
  styles: `
    :host {
      display: block;
    }
    .component-catalog {
      display: grid;
      gap: var(--space-3);
    }
    .search-field {
      width: 100%;
    }
    .catalog-filters {
      display: grid;
      grid-template-columns: repeat(3, minmax(0, 1fr));
      gap: var(--space-3);
    }
    .catalog-filters mat-form-field {
      min-width: 0;
    }
    .component-list {
      display: grid;
      gap: var(--space-2);
    }
    .component-option {
      width: 100%;
      padding: 14px var(--space-4);
      display: grid;
      gap: 7px;
      text-align: left;
      border: 1px solid var(--border-subtle);
      border-radius: var(--radius-md);
      background: var(--surface);
      color: var(--text-primary);
      font: inherit;
      cursor: pointer;
    }
    .component-option:hover,
    .component-option:focus-visible {
      border-color: var(--accent);
      background: var(--accent-soft);
      outline: none;
    }
    .component-option-heading {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-3);
    }
    .component-name {
      font-weight: 700;
    }
    .component-plugin,
    .component-meta {
      color: var(--text-muted);
      font-size: var(--font-body);
    }
    .component-description {
      color: var(--text-secondary);
      line-height: 1.45;
    }
    .empty-state {
      margin: 0;
      padding: var(--space-5) var(--space-3);
      color: var(--text-muted);
      text-align: center;
    }
    @media (max-width: 620px) {
      .catalog-filters {
        grid-template-columns: 1fr;
        gap: 0;
      }
    }
  `,
})
export class SearchableComponentCatalogComponent {
  readonly components = input.required<readonly FlowComponentDefinition[]>();
  readonly typeOptions = input.required<readonly FlowComponentFilterOption[]>();
  readonly pluginOptions = input.required<readonly FlowComponentFilterOption[]>();
  readonly selected = output<FlowComponentDefinition>();

  protected readonly query = signal('');
  protected readonly selectedType = signal('all');
  protected readonly selectedPlugin = signal('all');
  protected readonly selectedTool = signal('all');
  protected readonly toolOptions = computed(() => {
    const tools = new Map<string, string>();
    for (const component of this.components()) {
      if (component.executionToolId && component.executionToolName)
        tools.set(component.executionToolId, component.executionToolName);
    }
    return [...tools].map(([value, label]) => ({ value, label }));
  });
  protected readonly filteredComponents = computed(() => {
    const query = this.query().trim().toLocaleLowerCase();
    const type = this.selectedType();
    const plugin = this.selectedPlugin();
    const tool = this.selectedTool();

    return this.components().filter((component) => {
      const matchesQuery =
        query.length === 0 ||
        `${component.name} ${component.description} ${component.pluginName} ${component.executionToolName ?? ''}`
          .toLocaleLowerCase()
          .includes(query);
      return (
        matchesQuery &&
        (type === 'all' || component.mediaType === type) &&
        (plugin === 'all' || component.pluginId === plugin) &&
        (tool === 'all' || component.executionToolId === tool)
      );
    });
  });

  protected mediaTypeLabel(mediaType: FlowComponentDefinition['mediaType']): string {
    return this.typeOptions().find((option) => option.value === mediaType)?.label ?? mediaType;
  }

  protected kindLabel(kind: FlowComponentDefinition['kind']): string {
    return FLOW_COMPONENT_KIND_LABELS[kind];
  }
}
