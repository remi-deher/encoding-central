import type { MediaType } from './media-type';
import type { ConditionGroup, ConditionFieldDefinition, ConditionPreset } from './condition-tree';
import type { EncodingProfileSnapshot } from './encoding-profiles';

export interface FlowComponentReference {
  readonly pluginId: string;
  readonly pluginVersion: string;
  readonly componentId: string;
  readonly componentVersion: string;
  readonly configurationSchemaVersion: number;
}
export interface FlowComponentDefinition {
  readonly compatiblePluginVersions?: readonly string[];
  readonly reference?: FlowComponentReference;
  readonly id: string;
  readonly producesFiles?: boolean;
  readonly name: string;
  readonly description: string;
  readonly kind: FlowComponentKind;
  readonly mediaType: 'video' | 'image' | 'audio' | 'subtitles' | 'general';
  /** Stable functional catalog group supplied by the plugin manifest. */
  readonly category?: FlowComponentCategory;
  readonly supportedFlowMediaTypes?: readonly MediaType[];
  readonly pluginId: string;
  readonly pluginName: string;
  readonly executionToolId?: string;
  readonly executionToolName?: string;
  readonly outputs: readonly FlowComponentOutput[];
  readonly configurationSchema?: FlowComponentConfigurationSchema;
}

export const FLOW_COMPONENT_CATEGORIES = {
  inputs: 'Entrées et déclencheurs',
  files: 'Fichiers et dossiers',
  discovery: 'Découverte et sélection',
  metadata: 'Analyse et métadonnées',
  encoding: 'Encodage et transcodage',
  video: 'Vidéo et image animée',
  audio: 'Audio et musique',
  images: 'Images',
  text: 'Sous-titres et texte',
  containers: 'Conteneurs et multiplexage',
  'media-rules': 'Filtres et règles média',
  validation: 'Contrôle qualité et validation',
  conditions: 'Conditions et aiguillage',
  variables: 'Variables et données',
  scripts: 'Scripts et automatisation',
  arr: 'Intégrations *ARR',
  'media-servers': 'Serveurs et bibliothèques média',
  communications: 'API, réseau et communication',
  cluster: 'Nœuds et cluster',
  orchestration: 'Exécution et orchestration',
  reporting: 'Journaux, rapports et notifications',
  utilities: 'Utilitaires',
} as const;
export type FlowComponentCategory = keyof typeof FLOW_COMPONENT_CATEGORIES;

/** La compatibilité concerne le média du flow, y compris ses pistes intégrées. */
export function isFlowComponentCompatible(
  component: FlowComponentDefinition,
  flowMediaType: MediaType,
): boolean {
  if (component.supportedFlowMediaTypes)
    return component.supportedFlowMediaTypes.includes(flowMediaType);
  if (component.kind === 'input')
    return (
      component.mediaType === 'general' ||
      component.mediaType === (flowMediaType === 'music' ? 'audio' : flowMediaType)
    );
  if (component.mediaType === 'general') return true;
  if (component.mediaType === 'audio')
    return flowMediaType === 'music' || flowMediaType === 'video';
  if (component.mediaType === 'subtitles')
    return flowMediaType === 'subtitles' || flowMediaType === 'video';
  return component.mediaType === flowMediaType;
}

export type FlowComponentKind =
  | 'pack'
  | 'input'
  | 'action'
  | 'condition'
  | 'variable'
  | 'script'
  | 'preparation'
  | 'execution'
  | 'terminal';

export const FLOW_COMPONENT_KIND_LABELS: Record<FlowComponentKind, string> = {
  pack: 'Pack',
  input: 'Entrée',
  action: 'Action',
  condition: 'Condition',
  variable: 'Variable',
  script: 'Script',
  preparation: 'Préparation',
  execution: 'Exécution',
  terminal: 'Fin',
};

export interface FlowComponentOutput {
  readonly id: string;
  readonly label: string;
  readonly description: string;
}

export interface FlowComponentConfigurationSchema {
  readonly version: 1;
  readonly fields: readonly FlowComponentConfigurationField[];
}

interface FlowComponentConfigurationFieldBase {
  readonly id: string;
  readonly label: string;
  readonly description?: string;
  readonly required?: boolean;
  /** Show this field in the common block, in schema order. */
  readonly inline?: boolean;
  readonly importance?: 'basic' | 'advanced';
  readonly section?: string;
  readonly legacyOnly?: boolean;
  readonly requiresPluginVersion?: string;
  readonly requiresPluginVersionValue?: string;
  readonly editor?:
    | 'languages'
    | 'language'
    | 'node'
    | 'node-group'
    | 'flow-node'
    | 'indices'
    | 'labels'
    | 'artifact'
    | 'font-set'
    | 'resource';
  readonly visibleWhenFieldId?: string;
  readonly visibleWhenValue?: string;
  readonly visibleWhenEmpty?: boolean;
}

export type FlowConfigurationRecord = Readonly<Record<string, string | number | boolean | null>>;
export type FlowComponentConfigurationField =
  | (FlowComponentConfigurationFieldBase & {
      readonly type: 'record-list';
      readonly itemFields: readonly FlowComponentConfigurationField[];
      readonly defaultValue?: readonly FlowConfigurationRecord[];
      readonly minItems?: number;
      readonly maxItems?: number;
    })
  | (FlowComponentConfigurationFieldBase & {
      readonly type: 'condition-tree';
      readonly defaultValue?: ConditionGroup;
      readonly conditionFields: readonly ConditionFieldDefinition[];
      readonly conditionPresets?: readonly ConditionPreset[];
    })
  | (FlowComponentConfigurationFieldBase & {
      readonly type: 'text';
      readonly defaultValue?: string;
      readonly placeholder?: string;
      readonly multiline?: boolean;
    })
  | (FlowComponentConfigurationFieldBase & {
      readonly type: 'secret';
      readonly defaultValue?: string;
      readonly placeholder?: string;
    })
  | (FlowComponentConfigurationFieldBase & {
      readonly type: 'script-reference';
      readonly defaultValue?: string;
    })
  | (FlowComponentConfigurationFieldBase & {
      readonly type: 'encoding-profile';
      readonly defaultValue?: EncodingProfileSnapshot;
    })
  | (FlowComponentConfigurationFieldBase & {
      readonly type: 'video-encoder';
      readonly defaultValue?: string;
    })
  | (FlowComponentConfigurationFieldBase & {
      readonly type: 'number';
      readonly defaultValue?: number;
      readonly min?: number;
      readonly max?: number;
      readonly step?: number;
      /** Optional shortlist for direct editing; the numeric range remains authoritative. */
      readonly suggestions?: readonly number[];
    })
  | (FlowComponentConfigurationFieldBase & {
      readonly type: 'boolean';
      readonly defaultValue?: boolean;
    })
  | (FlowComponentConfigurationFieldBase & {
      readonly type: 'select';
      readonly defaultValue?: string;
      readonly options: readonly {
        readonly value: string;
        readonly label: string;
        readonly supportedFlowMediaTypes?: readonly MediaType[];
      }[];
    });

export function getFlowConfigurationSchema(
  schema: FlowComponentConfigurationSchema | undefined,
  mediaType: MediaType,
): FlowComponentConfigurationSchema | undefined {
  if (!schema) return undefined;
  return {
    ...schema,
    fields: schema.fields.map((field) => {
      if (field.type === 'condition-tree')
        return {
          ...field,
          conditionFields: field.conditionFields.filter((condition) =>
            condition.supportedFlowMediaTypes.includes(mediaType),
          ),
          conditionPresets: field.conditionPresets?.filter((preset) =>
            preset.supportedFlowMediaTypes.includes(mediaType),
          ),
        };
      if (field.type !== 'select') return field;
      const options = field.options.filter(
        (option) =>
          !option.supportedFlowMediaTypes || option.supportedFlowMediaTypes.includes(mediaType),
      );
      return {
        ...field,
        options,
        defaultValue: options.some((option) => option.value === field.defaultValue)
          ? field.defaultValue
          : options[0]?.value,
      };
    }),
  };
}

export type FlowComponentConfigurationValue =
  | string
  | number
  | boolean
  | null
  | ConditionGroup
  | EncodingProfileSnapshot
  | readonly FlowConfigurationRecord[];
export type FlowComponentConfiguration = Readonly<Record<string, FlowComponentConfigurationValue>>;

export function createDefaultFlowComponentConfiguration(
  schema: FlowComponentConfigurationSchema | undefined,
): FlowComponentConfiguration {
  return Object.fromEntries(
    (schema?.fields ?? [])
      .filter((field) => !field.legacyOnly)
      .flatMap((field) => {
        if (field.defaultValue !== undefined) return [[field.id, field.defaultValue]];
        if (field.type === 'boolean') return [[field.id, false]];
        return [];
      }),
  );
}

export interface FlowComponentFilterOption {
  readonly value: string;
  readonly label: string;
}

export interface FlowGraphPosition {
  readonly x: number;
  readonly y: number;
}

export interface FlowGraphNode {
  readonly reference?: FlowComponentReference;
  readonly id: string;
  readonly name?: string;
  readonly component: FlowComponentDefinition;
  readonly position: FlowGraphPosition;
  readonly configuration: FlowComponentConfiguration;
}

export interface FlowGraphConnection {
  readonly id: string;
  readonly sourceNodeId: string;
  readonly sourceOutputId: string;
  readonly targetNodeId: string;
}

export interface FlowConnectionRequest {
  readonly sourceNodeId: string;
  readonly sourceOutputId: string;
  readonly targetNodeId: string;
}
