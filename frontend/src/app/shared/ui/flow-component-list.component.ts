import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { MatExpansionModule } from '@angular/material/expansion';
import { FlowComponentDefinition, FLOW_COMPONENT_KIND_LABELS } from '../models/flow-component';
import { flowComponentColors } from '../models/flow-presentation';

const MEDIA_LABELS: Record<FlowComponentDefinition['mediaType'], string> = {
  image: 'Image',
  video: 'Vidéo',
  audio: 'Audio',
  subtitles: 'Sous-titres',
  general: 'Général',
};

const MEDIA_ORDER: Record<FlowComponentDefinition['mediaType'], number> = {
  image: 0,
  video: 1,
  audio: 2,
  subtitles: 3,
  general: 4,
};

interface FlowComponentGroup {
  readonly mediaType: FlowComponentDefinition['mediaType'];
  readonly label: string;
  readonly components: readonly FlowComponentDefinition[];
}

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-flow-component-list',
  imports: [MatExpansionModule],
  template: `
    @if (groups().length) {
      <mat-accordion class="component-groups" multi aria-label="Blocs par type de média">
        @for (group of groups(); track group.mediaType; let first = $first) {
          <mat-expansion-panel [expanded]="first">
            <mat-expansion-panel-header>
              <mat-panel-title>{{ group.label }}</mat-panel-title>
              <mat-panel-description>
                {{ group.components.length }} {{ group.components.length === 1 ? 'bloc' : 'blocs' }}
              </mat-panel-description>
            </mat-expansion-panel-header>
            <ul class="flow-component-list" [attr.aria-label]="'Blocs ' + group.label">
              @for (component of group.components; track component.pluginId + ':' + component.id) {
                <li
                  class="flow-block-preview"
                  [style.--component-color]="colors(component).color"
                  [style.--component-background]="colors(component).background"
                >
                  <div class="block-band">
                    <span class="block-kind">{{ kindLabel(component.kind) }}</span>
                    <span class="block-handle" aria-hidden="true">⠿</span>
                    <span class="block-provider">{{ component.pluginName }}</span>
                  </div>
                  <div class="block-heading">
                    <strong>{{ component.name }}</strong>
                  </div>
                  <p class="block-summary">{{ component.description }}</p>
                  @if (component.outputs.length) {
                    <div class="block-outputs" aria-label="Sorties">
                      @for (output of component.outputs; track output.id) {
                        <div class="block-output">
                          <span class="output-label">{{ output.label }}</span>
                          <span class="output-port" aria-hidden="true"></span>
                        </div>
                      }
                    </div>
                  }
                </li>
              }
            </ul>
          </mat-expansion-panel>
        }
      </mat-accordion>
    } @else {
      <p class="empty-message">{{ emptyMessage() }}</p>
    }
  `,
  styles: `
    :host {
      display: block;
    }
    .component-groups {
      display: grid;
      gap: var(--space-2);
      padding: var(--space-3) 0 var(--space-1);
    }
    .flow-component-list {
      list-style: none;
      display: grid;
      justify-items: center;
      gap: 18px;
      margin: 0;
      padding: 14px var(--space-1) 10px;
    }
    .flow-block-preview {
      min-width: 0;
      width: min(100%, 520px);
      box-sizing: border-box;
      border: 1px solid var(--border-subtle);
      border-radius: var(--radius-lg);
      background: var(--surface);
      overflow: hidden;
      box-shadow: 0 2px 7px rgb(0 0 0 / 5%);
    }
    .block-band {
      display: grid;
      grid-template-columns: minmax(0, 1fr) 36px minmax(0, 1fr);
      align-items: center;
      gap: var(--space-2);
      min-height: 40px;
      padding: var(--space-2) 14px;
      background: var(--component-background);
    }
    .block-kind {
      color: var(--component-color);
      font-size: var(--font-body);
      font-weight: 600;
    }
    .block-provider {
      color: var(--component-color);
      font-size: var(--font-body);
      text-align: right;
      overflow-wrap: anywhere;
    }
    .block-handle {
      width: 36px;
      height: 28px;
      display: grid;
      place-items: center;
      color: var(--component-color);
      font-size: var(--font-headline);
      opacity: 0.35;
    }
    .block-heading {
      padding: 18px 28px 6px;
      text-align: center;
      color: var(--text-primary);
      font-size: var(--font-title-lg);
      line-height: 1.3;
      overflow-wrap: anywhere;
    }
    .block-summary {
      margin: 0 0 var(--space-3);
      padding: 0 20px;
      color: var(--text-secondary);
      font-size: var(--font-body);
      line-height: 1.5;
      text-align: center;
      overflow-wrap: anywhere;
    }
    .block-outputs {
      display: flex;
      justify-content: center;
      flex-wrap: wrap;
      gap: 18px;
      margin-top: var(--space-3);
      border-top: 1px solid var(--border-subtle);
      padding: var(--space-2) var(--space-3) 0;
    }
    .block-output {
      min-width: 52px;
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: 7px;
      color: var(--component-color);
      font-size: var(--font-body);
    }
    .output-port {
      display: block;
      width: 14px;
      height: 14px;
      box-sizing: border-box;
      border: 2px solid var(--component-color);
      border-radius: 50%;
      background: var(--surface);
    }
    .empty-message {
      padding: var(--space-4) var(--space-1);
    }
    @media (max-width: 480px) {
      .component-heading {
        align-items: flex-start;
        flex-direction: column;
        gap: 3px;
      }
    }
  `,
})
export class FlowComponentListComponent {
  readonly components = input.required<readonly FlowComponentDefinition[]>();
  readonly emptyMessage = input('Aucun bloc fourni par ce plugin.');

  protected readonly groups = computed<readonly FlowComponentGroup[]>(() => {
    const grouped = new Map<FlowComponentDefinition['mediaType'], FlowComponentDefinition[]>();
    for (const component of this.components()) {
      const list = grouped.get(component.mediaType) ?? [];
      list.push(component);
      grouped.set(component.mediaType, list);
    }
    return [...grouped.entries()]
      .sort(([left], [right]) => MEDIA_ORDER[left] - MEDIA_ORDER[right])
      .map(([mediaType, components]) => ({
        mediaType,
        label: MEDIA_LABELS[mediaType],
        components: components.sort((left, right) => left.name.localeCompare(right.name, 'fr')),
      }));
  });

  protected colors(component: FlowComponentDefinition) {
    return flowComponentColors(component);
  }

  protected kindLabel(kind: FlowComponentDefinition['kind']): string {
    return FLOW_COMPONENT_KIND_LABELS[kind];
  }

  protected mediaLabel(mediaType: FlowComponentDefinition['mediaType']): string {
    return MEDIA_LABELS[mediaType];
  }
}
