import { flowConfigurationFieldError } from '../models/flow-graph';
import {
  ChangeDetectionStrategy,
  Component,
  input,
  output,
  OnChanges,
  OnDestroy,
  SimpleChanges,
} from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { MatInputModule } from '@angular/material/input';
import { Subscription } from 'rxjs';
import {
  FlowComponentConfiguration,
  FlowComponentConfigurationField,
  FlowComponentConfigurationSchema,
  FlowComponentConfigurationValue,
} from '../models/flow-component';
import {
  flowConfigurationValue,
  isFlowConfigurationFieldVisible,
} from '../models/flow-configuration';
import { flowFieldValidator } from './flow-configuration-controls';
import { conditionTreeSummary } from '../models/condition-tree';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-flow-inline-parameters',
  imports: [ReactiveFormsModule, MatFormFieldModule, MatSelectModule, MatInputModule],
  templateUrl: './flow-inline-parameters.html',
  styleUrl: './flow-inline-parameters.scss',
})
export class FlowInlineParameters implements OnChanges, OnDestroy {
  readonly schema = input<FlowComponentConfigurationSchema>();
  readonly values = input<FlowComponentConfiguration>({});
  readonly changed = output<{
    readonly fieldId: string;
    readonly value: FlowComponentConfigurationValue;
  }>();
  protected fields: readonly FlowComponentConfigurationField[] = [];
  protected form = new FormGroup<Record<string, FormControl<FlowComponentConfigurationValue>>>({});
  private subscriptions = new Subscription();
  private previousValues: FlowComponentConfiguration = {};
  ngOnChanges(changes: SimpleChanges): void {
    if (!changes['schema'] && this.fields.length) {
      for (const field of this.fields) {
        const next = flowConfigurationValue(field, this.values());
        const previous = flowConfigurationValue(field, this.previousValues);
        const control = this.form.controls[field.id];
        if (next !== previous && control.value !== next)
          control.setValue(next, { emitEvent: false });
      }
      this.previousValues = this.values();
      return;
    }
    this.previousValues = this.values();
    this.subscriptions.unsubscribe();
    this.subscriptions = new Subscription();
    this.fields = (this.schema()?.fields ?? []).filter(
      (field) => field.inline && !(field.type === 'text' && field.multiline),
    );
    const controls: Record<string, FormControl<FlowComponentConfigurationValue>> = {};
    for (const field of this.fields) {
      const control = new FormControl<FlowComponentConfigurationValue>(
        flowConfigurationValue(field, this.values()),
        { validators: flowFieldValidator(field) },
      );
      controls[field.id] = control;
      this.subscriptions.add(
        control.valueChanges.subscribe((value) => {
          if (control.valid) this.changed.emit({ fieldId: field.id, value });
        }),
      );
    }
    this.form = new FormGroup(controls);
  }
  protected visible(field: FlowComponentConfigurationField): boolean {
    const values = Object.fromEntries(
      (this.schema()?.fields ?? []).map((item) => [
        item.id,
        flowConfigurationValue(item, this.values()),
      ]),
    );
    return isFlowConfigurationFieldVisible(field, values);
  }
  protected numberOptions(
    field: Extract<FlowComponentConfigurationField, { type: 'number' }>,
  ): readonly number[] {
    const current = this.form.controls[field.id]?.value;
    return [
      ...new Set([...(field.suggestions ?? []), ...(typeof current === 'number' ? [current] : [])]),
    ]
      .filter((value) => !flowConfigurationFieldError(field, value))
      .sort((a, b) => a - b);
  }
  protected conditionSummary(
    field: Extract<FlowComponentConfigurationField, { type: 'condition-tree' }>,
  ): string {
    return conditionTreeSummary(this.form.controls[field.id]?.value, field.conditionFields);
  }
  ngOnDestroy(): void {
    this.subscriptions.unsubscribe();
  }
}
