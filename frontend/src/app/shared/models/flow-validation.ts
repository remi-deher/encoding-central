import type {
  FlowComponentDefinition,
  FlowGraphNode,
  FlowGraphConnection,
  FlowComponentConfiguration,
  FlowComponentConfigurationSchema,
  FlowComponentReference,
} from './flow-component';
import type { MediaType } from './media-type';

export interface FlowPluginCatalog {
  readonly id: string;
  readonly version: string;
  readonly name: string;
  readonly enabled?: boolean;
  readonly configurationSchema?: FlowComponentConfigurationSchema;
  readonly components: readonly (Omit<FlowComponentDefinition, 'pluginId' | 'pluginName'> & {
    readonly id: string;
    readonly version: string;
  })[];
}

export interface PluginConfigurationResponse {
  readonly pluginId: string;
  readonly revision: number;
  readonly values: FlowComponentConfiguration;
  readonly configuredSecrets: readonly string[];
}

export interface FlowDocument {
  readonly formatVersion: number;
  readonly id: string;
  readonly version: number;
  readonly name: string;
  readonly mediaType: MediaType;
  readonly nodes: readonly {
    readonly id: string;
    readonly name: string | null;
    readonly component: NonNullable<FlowGraphNode['reference']>;
    readonly configuration: FlowComponentConfiguration;
  }[];
  readonly connections: readonly FlowGraphConnection[];
  readonly componentPacks?: readonly FlowComponentPackInstance[];
}
export interface FlowComponentPackInstance {
  readonly id: string;
  readonly packId: string;
  readonly packVersion: number;
  readonly name: string;
  readonly inputNodeId: string;
  readonly nodeIds: readonly string[];
  readonly outputs: readonly {
    readonly id: string;
    readonly label: string;
    readonly sourceNodeId: string;
    readonly sourceOutputId: string;
  }[];
}
export interface FlowVersionResponse {
  readonly draftRevision?: number | null;
  readonly publishedVersion?: number | null;
  readonly definition: FlowDocument;
  readonly backupStatus: string;
  readonly createdAt: string;
}
/** A draft's next version is not an already published version. */
export function flowPublishedVersion(saved: FlowVersionResponse): number {
  return saved.draftRevision != null ? (saved.publishedVersion ?? 0) : saved.definition.version;
}
export function flowDocumentKey(definition: FlowDocument): string {
  const canonical = (value: unknown): unknown => {
    if (Array.isArray(value)) return value.map(canonical);
    if (value !== null && typeof value === 'object')
      return Object.fromEntries(
        Object.entries(value)
          .sort(([left], [right]) => left.localeCompare(right))
          .map(([key, item]) => [key, canonical(item)]),
      );
    return value;
  };
  return JSON.stringify(canonical({ ...definition, version: 0 }));
}
export interface FlowSummary {
  readonly isDraft: boolean;
  readonly id: string;
  readonly name: string;
  readonly mediaType: MediaType;
  readonly version: number;
  readonly nodeCount: number;
  readonly backupStatus: string;
  readonly updatedAt: string;
}

export interface FlowValidationIssue {
  readonly code: string;
  readonly path: string;
  readonly message: string;
}

export interface FlowValidationResult {
  readonly isValid: boolean;
  readonly issues: readonly FlowValidationIssue[];
}

/** Resolve only exact component/schema versions and explicitly declared plugin compatibility. */
export function resolveFlowComponent(
  plugins: readonly FlowPluginCatalog[],
  reference: FlowComponentReference,
) {
  const movedRemux = reference.pluginId === 'base' && reference.componentId === 'base-remux-media';
  const pluginId = movedRemux ? 'video' : reference.pluginId;
  const componentId = movedRemux ? 'video-remux-media' : reference.componentId;
  for (const plugin of plugins) {
    if (plugin.id !== pluginId) continue;
    const component = plugin.components.find(
      (component) =>
        component.id === componentId &&
        component.version === reference.componentVersion &&
        component.configurationSchema?.version === reference.configurationSchemaVersion &&
        (movedRemux ||
          plugin.version === reference.pluginVersion ||
          component.compatiblePluginVersions?.includes(reference.pluginVersion)),
    );
    if (component) return { plugin, component };
  }
  return undefined;
}

export function flowComponentReference(
  component: FlowComponentDefinition,
  plugins: readonly FlowPluginCatalog[],
) {
  if (component.reference) return component.reference;
  const plugin = plugins.find((plugin) => plugin.id === component.pluginId);
  const descriptor = plugin?.components.find((candidate) => candidate.id === component.id);
  return {
    pluginId: component.pluginId,
    pluginVersion: plugin?.version ?? '',
    componentId: component.id,
    componentVersion: descriptor?.version ?? '',
    configurationSchemaVersion: descriptor?.configurationSchema?.version ?? 0,
  };
}
