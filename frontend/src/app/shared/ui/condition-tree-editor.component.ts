import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import {
  CONDITION_MAX_DEPTH,
  CONDITION_MAX_NODES,
  ConditionExpression,
  ConditionFieldDefinition,
  ConditionGroup,
  ConditionPreset,
  ConditionRule,
  conditionNodeCount,
  conditionTreeError,
  defaultConditionRule,
  updateConditionTree,
} from '../models/condition-tree';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    NgTemplateOutlet,
    FormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
  ],
  selector: 'app-condition-tree-editor',
  templateUrl: './condition-tree-editor.component.html',
  styleUrl: './condition-tree-editor.component.scss',
})
export class ConditionTreeEditorComponent {
  readonly value = input<ConditionGroup>();
  readonly fields = input.required<readonly ConditionFieldDefinition[]>();
  readonly presets = input<readonly ConditionPreset[]>([]);
  readonly valueChange = output<ConditionGroup>();
  protected readonly root = computed<ConditionGroup>(
    () => this.value() ?? { kind: 'group', match: 'all', children: [] },
  );
  protected readonly error = computed(() => conditionTreeError(this.root(), this.fields()));
  protected readonly count = computed(() => conditionNodeCount(this.root()));
  protected readonly maxNodes = CONDITION_MAX_NODES;
  protected readonly maxDepth = CONDITION_MAX_DEPTH;
  protected field(id: string) {
    return this.fields().find((field) => field.id === id);
  }
  protected childPath(path: readonly number[], index: number) {
    return [...path, index];
  }
  protected applyPreset(id: string) {
    const preset = this.presets().find((preset) => preset.id === id);
    if (preset) this.valueChange.emit(structuredClone(preset.expression));
  }
  protected change(
    path: readonly number[],
    change: (node: ConditionExpression) => ConditionExpression,
  ) {
    this.valueChange.emit(updateConditionTree(this.root(), path, change));
  }
  protected match(path: readonly number[], value: ConditionGroup['match']) {
    this.change(path, (node) => (node.kind === 'group' ? { ...node, match: value } : node));
  }
  protected rule(path: readonly number[], patch: Partial<ConditionRule>) {
    this.change(path, (node) => (node.kind === 'rule' ? { ...node, ...patch } : node));
  }
  protected changeField(path: readonly number[], id: string) {
    const field = this.field(id);
    if (field) this.change(path, () => defaultConditionRule(field));
  }
  protected operator(path: readonly number[], operator: string) {
    this.change(path, (node) => {
      if (node.kind !== 'rule') return node;
      if (operator === 'exists' || operator === 'missing') {
        const { value: _value, ...withoutValue } = node;
        return { ...withoutValue, operator };
      }
      return {
        ...node,
        operator,
        value: node.value ?? defaultConditionRule(this.field(node.field)!).value,
      };
    });
  }
  protected add(path: readonly number[], group: boolean) {
    if (
      !this.fields().length ||
      this.count() + (group ? 2 : 1) > CONDITION_MAX_NODES ||
      path.length + (group ? 3 : 2) > CONDITION_MAX_DEPTH
    )
      return;
    const rule = defaultConditionRule(
      this.fields().find((field) => field.id === 'file.extension') ?? this.fields()[0],
    );
    const child: ConditionExpression = group
      ? { kind: 'group', match: 'all', children: [rule] }
      : rule;
    this.change(path, (node) =>
      node.kind === 'group' ? { ...node, children: [...node.children, child] } : node,
    );
  }
  protected remove(path: readonly number[]) {
    const index = path[path.length - 1];
    this.change(path.slice(0, -1), (node) =>
      node.kind === 'group'
        ? { ...node, children: node.children.filter((_, current) => current !== index) }
        : node,
    );
  }
  protected move(path: readonly number[], delta: number) {
    const index = path[path.length - 1];
    this.change(path.slice(0, -1), (node) => {
      if (node.kind !== 'group' || !node.children[index + delta]) return node;
      const children = [...node.children];
      [children[index], children[index + delta]] = [children[index + delta], children[index]];
      return { ...node, children };
    });
  }
}
