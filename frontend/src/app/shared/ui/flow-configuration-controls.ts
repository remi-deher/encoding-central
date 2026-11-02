import { ValidatorFn } from '@angular/forms';
import { FlowComponentConfigurationField } from '../models/flow-component';
import { flowConfigurationFieldError } from '../models/flow-graph';
export function flowFieldValidator(field: FlowComponentConfigurationField): ValidatorFn {
  return (control) => {
    const error = flowConfigurationFieldError(field, control.value);
    return error ? { configuration: error } : null;
  };
}
