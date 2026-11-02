import { FlowComponentDefinition, FlowGraphNode } from './flow-component';
import { FlowDocument, FlowPluginCatalog, resolveFlowComponent } from './flow-validation';
import { LEGACY_FLOW_COMPONENTS } from './flow-component-catalog';

export function flowDocumentNodes(
  definition: FlowDocument,
  plugins: readonly FlowPluginCatalog[],
): FlowGraphNode[] {
  return definition.nodes.map((node) => {
    const resolved = resolveFlowComponent(plugins, node.component);
    const plugin = resolved?.plugin;
    const descriptor = resolved?.component;
    const legacyComponent = LEGACY_FLOW_COMPONENTS.find(
      (candidate) =>
        candidate.pluginId === node.component.pluginId &&
        candidate.id === node.component.componentId,
    );
    const component: FlowComponentDefinition = descriptor
      ? { ...descriptor, pluginId: plugin!.id, pluginName: plugin!.name }
      : legacyComponent
        ? legacyComponent
        : {
            id: node.component.componentId,
            name: node.name ?? node.component.componentId,
            description:
              'Cette version du composant est indisponible. Sa configuration est conservée.',
            kind: 'action',
            mediaType: 'general',
            pluginId: node.component.pluginId,
            pluginName: node.component.pluginId,
            outputs: definition.connections
              .filter((edge) => edge.sourceNodeId === node.id)
              .map((edge) => ({
                id: edge.sourceOutputId,
                label: edge.sourceOutputId,
                description: '',
              })),
          };
    return {
      id: node.id,
      name: node.name ?? undefined,
      component,
      reference: node.component,
      configuration: node.configuration,
      position: { x: 0, y: 0 },
    };
  });
}
