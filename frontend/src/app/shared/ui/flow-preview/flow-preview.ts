import { IconComponent } from '../icon.component';
import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import {
  FLOW_COMPONENT_KIND_LABELS,
  FlowComponentDefinition,
  FlowComponentReference,
} from '../../models/flow-component';
import { flowComponentColors } from '../../models/flow-presentation';
import { FlowDocument } from '../../models/flow-validation';
import { MEDIA_TYPE_OPTIONS } from '../../models/media-type';

interface PreviewConfigurationValue {
  readonly label: string;
  readonly value: string;
}

interface PreviewConnection {
  readonly label: string;
  readonly target: string;
}

interface PreviewNode {
  readonly id: string;
  readonly title: string;
  readonly kind: string;
  readonly plugin: string;
  readonly description: string;
  readonly color: string;
  readonly background: string;
  readonly configuration: readonly PreviewConfigurationValue[];
  readonly connections: readonly PreviewConnection[];
  readonly hasOutputs: boolean;
}

function sameReference(
  left: FlowComponentReference,
  candidate: FlowComponentDefinition | undefined,
): boolean {
  const right = candidate?.reference;
  return (
    !!right &&
    left.pluginId === right.pluginId &&
    (left.pluginVersion === right.pluginVersion ||
      candidate.compatiblePluginVersions?.includes(left.pluginVersion) === true) &&
    left.componentId === right.componentId &&
    left.componentVersion === right.componentVersion &&
    left.configurationSchemaVersion === right.configurationSchemaVersion
  );
}

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-flow-preview',
  imports: [IconComponent],
  templateUrl: './flow-preview.html',
  styleUrl: './flow-preview.scss',
})
export class FlowPreviewComponent {
  readonly flow = input<FlowDocument | null>(null);
  readonly blocks = input<FlowDocument['nodes']>([]);
  readonly components = input<readonly FlowComponentDefinition[]>([]);
  readonly mode = input<'flow' | 'blocks'>('flow');

  protected readonly mediaTypeLabel = computed(() => {
    const mediaType = this.flow()?.mediaType;
    return mediaType
      ? (MEDIA_TYPE_OPTIONS.find((item) => item.value === mediaType)?.label ?? mediaType)
      : null;
  });

  protected readonly previewNodes = computed<readonly PreviewNode[]>(() => {
    const flow = this.flow();
    const components = this.components();
    const sourceNodes = flow?.nodes ?? this.blocks();
    const flowConnections = flow?.connections ?? [];
    const nodes = this.inTopologicalOrder(sourceNodes, flowConnections);
    const titleById = new Map(
      sourceNodes.map((node) => {
        const descriptor = components.find((candidate) => sameReference(node.component, candidate));
        return [node.id, node.name || descriptor?.name || node.component.componentId] as const;
      }),
    );

    return nodes.map((node) => {
      const descriptor = components.find((candidate) => sameReference(node.component, candidate));
      const safeConfiguration = (descriptor?.configurationSchema?.fields ?? []).flatMap((field) => {
        const value = node.configuration[field.id];
        if (value === undefined || value === null) return [];
        if (field.type === 'select') {
          const option = field.options.find((item) => item.value === value);
          return option ? [{ label: field.label, value: option.label }] : [];
        }
        if (field.type === 'number' && typeof value === 'number')
          return [{ label: field.label, value: String(value) }];
        if (field.type === 'boolean' && typeof value === 'boolean')
          return [{ label: field.label, value: value ? 'Oui' : 'Non' }];
        return [];
      });
      const outputs = descriptor?.outputs ?? [];
      const connections = outputs.flatMap(
        (output) =>
          flowConnections
            .filter(
              (connection) =>
                connection.sourceNodeId === node.id && connection.sourceOutputId === output.id,
            )
            .map((connection) => ({
              label: output.label,
              target: titleById.get(connection.targetNodeId) ?? 'Bloc indisponible',
            })) ?? [],
      );
      const colors = descriptor
        ? flowComponentColors(descriptor)
        : { color: '#555e6b', background: '#f0f2f5' };
      return {
        id: node.id,
        title: node.name || descriptor?.name || node.component.componentId,
        kind: descriptor ? FLOW_COMPONENT_KIND_LABELS[descriptor.kind] : 'Bloc',
        plugin: descriptor?.pluginName ?? node.component.pluginId,
        description: descriptor?.description ?? 'Description du composant indisponible.',
        color: colors.color,
        background: colors.background,
        configuration: safeConfiguration,
        connections,
        hasOutputs: outputs.length > 0,
      };
    });
  });

  private inTopologicalOrder<T extends { readonly id: string }>(
    nodes: readonly T[],
    connections: FlowDocument['connections'],
  ): readonly T[] {
    const byId = new Map(nodes.map((node) => [node.id, node]));
    const indegree = new Map(nodes.map((node) => [node.id, 0]));
    for (const connection of connections) {
      if (indegree.has(connection.targetNodeId) && byId.has(connection.sourceNodeId))
        indegree.set(connection.targetNodeId, (indegree.get(connection.targetNodeId) ?? 0) + 1);
    }
    const ready = nodes.filter((node) => indegree.get(node.id) === 0);
    const ordered: T[] = [];
    while (ready.length) {
      const node = ready.shift()!;
      ordered.push(node);
      for (const connection of connections.filter((item) => item.sourceNodeId === node.id)) {
        if (!indegree.has(connection.targetNodeId)) continue;
        const remaining = (indegree.get(connection.targetNodeId) ?? 0) - 1;
        indegree.set(connection.targetNodeId, remaining);
        if (remaining === 0) {
          const target = byId.get(connection.targetNodeId);
          if (target) ready.push(target);
        }
      }
    }
    return ordered.length === nodes.length ? ordered : nodes;
  }
}
