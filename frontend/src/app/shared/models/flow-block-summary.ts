import { FlowGraphNode, getFlowConfigurationSchema } from './flow-component';
import { MediaType } from './media-type';
import { flowConfigurationValue } from './flow-configuration';
import { conditionTreeSummary } from './condition-tree';
/** Schema-declared settings, condition summaries and profile names; never script bodies or secrets. */
export function flowBlockSummary(node: FlowGraphNode, mediaType: MediaType): string {
  const fields =
    getFlowConfigurationSchema(node.component.configurationSchema, mediaType)?.fields ?? [];
  return fields
    .filter(
      (field) =>
        (field.inline || field.type === 'condition-tree' || field.type === 'encoding-profile') &&
        field.type !== 'secret',
    )
    .flatMap((field) => {
      const value = flowConfigurationValue(field, node.configuration);
      if (field.type === 'condition-tree')
        return [conditionTreeSummary(value, field.conditionFields)];
      if (
        field.type === 'encoding-profile' &&
        value &&
        typeof value === 'object' &&
        'name' in value
      )
        return [field.label + ' : ' + value.name];
      if (
        value === null ||
        value === undefined ||
        value === '' ||
        typeof value === 'object' ||
        (field.type === 'text' && field.multiline)
      )
        return [];
      const label =
        field.type === 'select'
          ? (field.options.find((option) => option.value === value)?.label ?? String(value))
          : typeof value === 'boolean'
            ? value
              ? 'Oui'
              : 'Non'
            : String(value);
      return [field.label + ' : ' + label];
    })
    .slice(0, 3)
    .join(' · ');
}
