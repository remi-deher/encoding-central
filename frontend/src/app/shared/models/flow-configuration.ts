import type {
  FlowComponentConfiguration,
  FlowComponentConfigurationField,
  FlowComponentConfigurationValue,
} from './flow-component';
export function flowConfigurationValue(
  field: FlowComponentConfigurationField,
  values: FlowComponentConfiguration,
): FlowComponentConfigurationValue {
  if (Object.prototype.hasOwnProperty.call(values, field.id)) return values[field.id];
  if (
    field.type === 'encoding-profile' &&
    ['quality', 'preset', 'hardwareAcceleration'].some((key) =>
      Object.prototype.hasOwnProperty.call(values, key),
    )
  )
    return null;
  return (
    field.defaultValue ??
    (field.type === 'record-list'
      ? []
      : field.type === 'boolean'
        ? false
        : field.type === 'number'
          ? null
          : '')
  );
}

export function isFlowConfigurationFieldVisible(
  field: FlowComponentConfigurationField,
  values: FlowComponentConfiguration,
): boolean {
  if (!field.visibleWhenFieldId) return true;
  const value = values[field.visibleWhenFieldId];
  if (field.visibleWhenEmpty)
    return (
      value === undefined ||
      value === null ||
      value === '' ||
      (Array.isArray(value) && value.length === 0)
    );
  return field.visibleWhenValue === undefined || value === field.visibleWhenValue;
}
