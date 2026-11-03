import { inject, Injectable } from '@angular/core';
import { NotificationService } from '../../../shared/services/notification.service';
import { canConnectFlowGraph } from '../../../shared/models/flow-graph';
import {
  createDefaultFlowComponentConfiguration,
  isFlowComponentCompatible,
  getFlowConfigurationSchema,
  FlowComponentDefinition,
  FlowComponentOutput,
  FlowGraphConnection,
  FlowGraphNode,
} from '../../../shared/models/flow-component';
import { FlowContinuationSource } from '../../../shared/models/flow-sequence';
import { FlowGraphStore } from './flow-graph.store';

/**
 * Opérations d’édition du graphe : insertions (composant, pack), suppressions,
 * duplications et connexions, avec validation des règles de liaison et des
 * ports de packs.
 */
@Injectable()
export class FlowGraphEditingStore {
  private readonly notifications = inject(NotificationService);
  readonly graph = inject(FlowGraphStore);

  cancelInsertion(): void {
    this.graph.componentDestination.set(null);
  }

  onTerminationRequested(event: {
    readonly sourceNodeId: string;
    readonly output: FlowComponentOutput;
  }): void {
    const component = this.graph.components
      .components()
      .find((component) => component.id === 'base-complete-flow');
    if (!component) return;
    this.graph.componentDestination.set({ kind: 'branch', ...event });
    this.addComponent(component);
  }

  onBranchComponentRequested(event: {
    readonly sourceNodeId: string;
    readonly output: FlowComponentOutput;
  }): void {
    this.graph.componentDestination.set({ kind: 'branch', ...event });
  }

  onContinuationRequested(sources: readonly FlowContinuationSource[]): void {
    if (!sources.length) return;
    this.graph.componentDestination.set({ kind: 'continuation', sources });
  }

  addComponent(
    component: FlowComponentDefinition,
    position?: { x: number; y: number },
  ): { added: boolean; nodeId?: string } {
    if (component.kind === 'input') return { added: false };
    if (!isFlowComponentCompatible(component, this.graph.components.flowMediaType())) {
      this.notifications.show('Ce bloc n’est pas compatible avec le type de média du flow.');
      return { added: false };
    }
    const destination = this.graph.componentDestination();
    if (!destination && !position) return { added: false };
    const node: FlowGraphNode = {
      id: `flow-node-${this.graph.nextNodeId++}`,
      component,
      position: { x: 0, y: 0 },
      configuration: createDefaultFlowComponentConfiguration(
        getFlowConfigurationSchema(
          component.configurationSchema,
          this.graph.components.flowMediaType(),
        ),
      ),
    };
    if (position) {
      this.graph.setGraph([...this.graph.nodes(), node], this.graph.connections());
      this.graph.componentDestination.set(null);
      return { added: true, nodeId: node.id };
    }
    if (!destination) return { added: false };
    const packOwner =
      destination.kind === 'branch'
        ? this.graph.componentPacks().find((pack) => pack.nodeIds.includes(destination.sourceNodeId))
        : undefined;
    const nodes = [...this.graph.nodes(), node];
    if (destination.kind === 'continuation') {
      let next = [...this.graph.connections()];
      for (const source of destination.sources) {
        if (
          !canConnectFlowGraph(nodes, next, {
            sourceNodeId: source.sourceNodeId,
            sourceOutputId: source.output.id,
            targetNodeId: node.id,
          })
        ) {
          this.notifications.show(
            'Une branche a changé. Choisissez à nouveau où ajouter la suite.',
          );
          return { added: false };
        }
        next.push(this.graph.createConnection(source.sourceNodeId, source.output.id, node.id));
      }
      this.graph.setGraph(nodes, next);
      const continuationPack = destination.sources.length
        ? this.graph
            .componentPacks()
            .find((pack) =>
              destination.sources.every((source) => pack.nodeIds.includes(source.sourceNodeId)),
            )
        : undefined;
      if (continuationPack)
        this.graph.componentPacks.update((packs) =>
          packs.map((pack) =>
            pack.id === continuationPack.id
              ? {
                  ...pack,
                  nodeIds: [...pack.nodeIds, node.id],
                  outputs: [
                    ...pack.outputs.filter(
                      (port) =>
                        !destination.sources.some(
                          (source) =>
                            port.sourceNodeId === source.sourceNodeId &&
                            port.sourceOutputId === source.output.id,
                        ),
                    ),
                    ...node.component.outputs.map((output) => ({
                      id: `${node.id}:${output.id}`,
                      label: output.label,
                      sourceNodeId: node.id,
                      sourceOutputId: output.id,
                    })),
                  ],
                }
              : pack,
          ),
        );
      this.graph.componentDestination.set(null);
      return { added: true, nodeId: node.id };
    }
    const existing =
      destination.kind === 'branch'
        ? this.graph
            .connections()
            .find(
              (edge) =>
                edge.sourceNodeId === destination.sourceNodeId &&
                edge.sourceOutputId === destination.output.id,
            )
        : undefined;
    const remaining = this.graph.connections().filter((edge) => edge.id !== existing?.id);
    if (
      destination.kind === 'branch' &&
      !canConnectFlowGraph(nodes, remaining, {
        sourceNodeId: destination.sourceNodeId,
        sourceOutputId: destination.output.id,
        targetNodeId: node.id,
      })
    ) {
      this.notifications.show('Cette sortie n’est plus disponible.');
      return { added: false };
    }
    const continuation =
      component.outputs.find((output) => output.id === 'success') ?? component.outputs[0];
    if (existing && !continuation) {
      this.notifications.show(
        'Une étape terminale ne peut pas être insérée avant une suite. Ajoutez-la à la fin d’un chemin.',
      );
      return { added: false };
    }
    const connections =
      destination.kind === 'branch'
        ? [
            ...remaining,
            this.graph.createConnection(destination.sourceNodeId, destination.output.id, node.id),
            ...(existing && continuation
              ? [this.graph.createConnection(node.id, continuation.id, existing.targetNodeId)]
              : []),
          ]
        : remaining;
    this.graph.setGraph(nodes, connections);
    if (packOwner)
      this.graph.componentPacks.update((packs) =>
        packs.map((pack) =>
          pack.id === packOwner.id
            ? {
                ...pack,
                nodeIds: [...pack.nodeIds, node.id],
                outputs: (() => {
                  const crossesBoundary =
                    !existing || !pack.nodeIds.includes(existing.targetNodeId);
                  const replaced = pack.outputs.some(
                    (port) =>
                      port.sourceNodeId === destination.sourceNodeId &&
                      port.sourceOutputId === destination.output.id,
                  );
                  return crossesBoundary
                    ? [
                        ...pack.outputs.filter(
                          (port) =>
                            !(
                              port.sourceNodeId === destination.sourceNodeId &&
                              port.sourceOutputId === destination.output.id
                            ),
                        ),
                        ...(replaced || !existing
                          ? continuation
                            ? [
                                {
                                  id: `${node.id}:${continuation.id}`,
                                  label: continuation.label,
                                  sourceNodeId: node.id,
                                  sourceOutputId: continuation.id,
                                },
                              ]
                            : []
                          : []),
                      ]
                    : pack.outputs;
                })(),
              }
            : pack,
        ),
      );
    this.graph.componentDestination.set(null);
    return { added: true, nodeId: node.id };
  }

  onConnectionRequested(event: {
    readonly sourceNodeId: string;
    readonly sourceOutputId: string;
    readonly targetNodeId: string;
  }): void {
    const sourcePack = this.graph
      .componentPacks()
      .find((pack) => pack.nodeIds.includes(event.sourceNodeId));
    const targetPack = this.graph
      .componentPacks()
      .find((pack) => pack.nodeIds.includes(event.targetNodeId));
    const samePack = sourcePack?.id === targetPack?.id;
    if (
      (sourcePack &&
        !samePack &&
        !sourcePack.outputs.some(
          (port) =>
            port.sourceNodeId === event.sourceNodeId &&
            port.sourceOutputId === event.sourceOutputId,
        )) ||
      (targetPack && !samePack && event.targetNodeId !== targetPack.inputNodeId)
    ) {
      this.notifications.show('Cette liaison traverse le pack sans utiliser ses ports déclarés.');
      return;
    }
    if (!canConnectFlowGraph(this.graph.nodes(), this.graph.connections(), event)) {
      this.notifications.show(
        'Connexion impossible : vérifiez la sortie, le point d’entrée et l’absence de cycle.',
      );
      return;
    }
    const connections = [
      ...this.graph.connections(),
      this.graph.createConnection(event.sourceNodeId, event.sourceOutputId, event.targetNodeId),
    ];
    this.graph.setGraph([...this.graph.nodes()], connections);
    this.synchronizePackOutputs(connections);
  }

  onConnectionsChanged(connections: readonly FlowGraphConnection[]): void {
    const nodes = this.graph.nodes();
    if (
      connections.some(
        (edge, index) =>
          !canConnectFlowGraph(
            nodes,
            connections.filter((_, other) => other !== index),
            edge,
          ),
      )
    ) {
      this.notifications.show('Cette réorganisation produirait une connexion invalide.');
      return;
    }
    if (
      connections.some((edge) => {
        const sourcePack = this.graph
          .componentPacks()
          .find((pack) => pack.nodeIds.includes(edge.sourceNodeId));
        const targetPack = this.graph
          .componentPacks()
          .find((pack) => pack.nodeIds.includes(edge.targetNodeId));
        const samePack = sourcePack?.id === targetPack?.id;
        return (
          (sourcePack &&
            !samePack &&
            !sourcePack.outputs.some(
              (port) =>
                port.sourceNodeId === edge.sourceNodeId &&
                port.sourceOutputId === edge.sourceOutputId,
            )) ||
          (targetPack && !samePack && edge.targetNodeId !== targetPack.inputNodeId)
        );
      })
    ) {
      this.notifications.show(
        'Cette réorganisation relierait un bloc en dehors des ports du pack.',
      );
      return;
    }
    this.graph.setGraph(nodes, connections);
    this.synchronizePackOutputs(connections);
  }

  private synchronizePackOutputs(connections: readonly FlowGraphConnection[]): void {
    this.graph.componentPacks.update((packs) =>
      packs.map((pack) => ({
        ...pack,
        outputs: pack.outputs.filter(
          (port) =>
            !connections.some(
              (edge) =>
                edge.sourceNodeId === port.sourceNodeId &&
                edge.sourceOutputId === port.sourceOutputId &&
                pack.nodeIds.includes(edge.targetNodeId),
            ),
        ),
      })),
    );
  }

  duplicateNode(nodeId: string): void {
    const original = this.graph.nodes().find((n) => n.id === nodeId);
    if (
      !original ||
      original.component.kind === 'input' ||
      original.component.kind === 'pack' ||
      this.graph.componentPacks().some((p) => p.nodeIds.includes(nodeId))
    ) {
      this.notifications.show(
        'Cette étape appartient à une entrée ou à un pack et ne peut pas être dupliquée seule.',
      );
      return;
    }
    const node = {
      ...original,
      id: 'flow-node-' + this.graph.nextNodeId++,
      name: (original.name || original.component.name) + ' — Copie',
      configuration: structuredClone(original.configuration),
    };
    this.graph.setGraph([...this.graph.nodes(), node], this.graph.connections());
    this.notifications.show('Copie ajoutée parmi les blocs non reliés, dans les deux vues.');
  }

  removeNode(nodeId: string): void {
    const visualPack = this.graph
      .componentPacks()
      .find((pack) => `flow-pack-${pack.id}` === nodeId);
    if (visualPack) {
      const removed = new Set(visualPack.nodeIds);
      this.graph.setGraph(
        this.graph.nodes().filter((node) => !removed.has(node.id)),
        this.graph.connections().filter(
          (edge) => !removed.has(edge.sourceNodeId) && !removed.has(edge.targetNodeId),
        ),
      );
      this.graph.componentPacks.update((packs) => packs.filter((pack) => pack.id !== visualPack.id));
      return;
    }
    const packOwner = this.graph.componentPacks().find((pack) => pack.nodeIds.includes(nodeId));
    if (packOwner?.inputNodeId === nodeId) {
      this.notifications.show(
        'L’entrée du pack est son point d’ancrage ; supprimez le pack entier pour la retirer.',
      );
      return;
    }
    if (this.graph.nodes().find((node) => node.id === nodeId)?.component.kind === 'input') return;
    const incoming = this.graph.connections().filter((edge) => edge.targetNodeId === nodeId);
    const outgoing = this.graph.connections().filter((edge) => edge.sourceNodeId === nodeId);
    const restoredPackOutputs =
      packOwner && outgoing.length === 1 && !packOwner.nodeIds.includes(outgoing[0].targetNodeId)
        ? incoming.flatMap((edge) => {
            const source = this.graph.nodes().find((candidate) => candidate.id === edge.sourceNodeId);
            const output = source?.component.outputs.find(
              (candidate) => candidate.id === edge.sourceOutputId,
            );
            return source && output
              ? [
                  {
                    id: `${source.id}:${output.id}`,
                    label: output.label,
                    sourceNodeId: source.id,
                    sourceOutputId: output.id,
                  },
                ]
              : [];
          })
        : [];
    let remaining = this.graph.connections().filter(
      (edge) => edge.sourceNodeId !== nodeId && edge.targetNodeId !== nodeId,
    );
    if (outgoing.length === 1)
      remaining = [
        ...remaining,
        ...incoming.map((edge) => ({ ...edge, targetNodeId: outgoing[0].targetNodeId })),
      ];
    this.graph.setGraph(
      this.graph.nodes().filter((node) => node.id !== nodeId),
      remaining,
    );
    if (packOwner)
      this.graph.componentPacks.update((packs) =>
        packs
          .map((pack) =>
            pack.id === packOwner.id
              ? {
                  ...pack,
                  nodeIds: pack.nodeIds.filter((id) => id !== nodeId),
                  outputs: pack.outputs
                    .filter((port) => port.sourceNodeId !== nodeId)
                    .concat(restoredPackOutputs),
                }
              : pack,
          )
          .filter((pack) => pack.nodeIds.length > 0),
      );
    if (outgoing.length > 1)
      this.notifications.show(
        'Étape supprimée. Les blocs de ses branches sont conservés parmi les blocs non reliés.',
      );
  }

  removeConnection(connectionId: string): void {
    const removed = this.graph.connections().find((connection) => connection.id === connectionId);
    const connections = this.graph
      .connections()
      .filter((connection) => connection.id !== connectionId);
    if (connections.length !== this.graph.connections().length) {
      this.graph.setGraph([...this.graph.nodes()], connections);
      const owner =
        removed &&
        this.graph
          .componentPacks()
          .find(
            (pack) =>
              pack.nodeIds.includes(removed.sourceNodeId) &&
              pack.nodeIds.includes(removed.targetNodeId),
          );
      const source = removed && this.graph.nodes().find((node) => node.id === removed.sourceNodeId);
      const output = source?.component.outputs.find(
        (candidate) => candidate.id === removed?.sourceOutputId,
      );
      if (
        owner &&
        removed &&
        source &&
        output &&
        !connections.some(
          (edge) =>
            edge.sourceNodeId === removed.sourceNodeId &&
            edge.sourceOutputId === removed.sourceOutputId,
        )
      ) {
        this.graph.componentPacks.update((packs) =>
          packs.map((pack) =>
            pack.id === owner.id &&
            !pack.outputs.some(
              (port) => port.sourceNodeId === source.id && port.sourceOutputId === output.id,
            )
              ? {
                  ...pack,
                  outputs: [
                    ...pack.outputs,
                    {
                      id: `${source.id}:${output.id}`,
                      label: output.label,
                      sourceNodeId: source.id,
                      sourceOutputId: output.id,
                    },
                  ],
                }
              : pack,
          ),
        );
      }
    }
  }
}
