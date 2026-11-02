import type { FlowDocument } from './flow-validation';
import type { MediaType } from './media-type';

export interface FlowTemplateSummary {
  readonly id: string;
  readonly name: string;
  readonly description: string;
  readonly mediaType: MediaType;
  readonly sourceFlowId: string;
  readonly sourceVersion: number;
  readonly sourceFlowName: string;
  readonly nodeCount: number;
  readonly createdAt: string;
  readonly revision: number;
  readonly configurableFieldCount: number;
}

export interface FlowTemplateDetails {
  readonly template: FlowTemplateSummary;
  readonly definition: FlowDocument;
  readonly configuration: FlowTemplateConfiguration;
}

export interface FlowTemplateConfiguration {
  readonly nodes: readonly { readonly nodeId: string; readonly fieldIds: readonly string[] }[];
}

export interface FlowTemplateNodeValues {
  readonly nodeId: string;
  readonly values: Readonly<Record<string, unknown>>;
}

export interface CreateFlowTemplateFromFlowRequest {
  readonly sourceFlowId: string;
  readonly sourceVersion: number;
  readonly name: string;
  readonly description: string;
  readonly configuration: FlowTemplateConfiguration;
}

export interface UpdateFlowTemplateRequest {
  readonly expectedRevision: number;
  readonly name: string;
  readonly description: string;
  readonly definition: FlowDocument;
  readonly configuration: FlowTemplateConfiguration;
}

export function isTemplateConfigurableField(field: {
  readonly id: string;
  readonly type: string;
  readonly label?: string;
  readonly editor?: string;
}): boolean {
  const id = `${field.id} ${field.label ?? ''}`
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .toLowerCase();
  const tokens = new Set(id.split(/[^a-z0-9]+/).filter(Boolean));
  return (
    ![
      'secret',
      'script-reference',
      'condition-tree',
      'record-list',
      'path',
      'file-path',
      'directory-path',
      'file',
      'directory',
    ].includes(field.type) &&
    field.editor?.toLowerCase() !== 'artifact' &&
    !hasLocalPath(tokens)
  );
}

function hasLocalPath(tokens: ReadonlySet<string>): boolean {
  return (
    ['path', 'paths', 'chemin', 'chemins', 'directory', 'folder', 'dossier'].some((part) =>
      tokens.has(part),
    ) ||
    ((tokens.has('file') || tokens.has('fichier')) &&
      [
        'source',
        'input',
        'output',
        'local',
        'current',
        'original',
        'temp',
        'temporary',
        'artifact',
      ].some((part) => tokens.has(part)))
  );
}
