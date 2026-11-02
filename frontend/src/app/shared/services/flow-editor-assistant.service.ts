import { Injectable, signal } from '@angular/core';
import { FlowDocument } from '../models/flow-validation';
import { FlowComponentDefinition } from '../models/flow-component';
export interface FlowAssistantSession {
  readonly label: string;
  readonly flow: FlowDocument;
  readonly components: readonly FlowComponentDefinition[];
  readonly prompt: string;
  readonly apply: (proposal: FlowDocument) => Promise<boolean>;
}
/** Single entry point for the widget, editor toolbar and contextual actions. */
@Injectable({ providedIn: 'root' })
export class FlowEditorAssistant {
  readonly session = signal<FlowAssistantSession | null>(null);
  readonly opening = signal(false);
  readonly applying = signal(false);
  private editor?: {
    owner: object;
    prepare: (nodeId?: string) => Promise<FlowAssistantSession | null>;
  };
  register(
    owner: object,
    prepare: (nodeId?: string) => Promise<FlowAssistantSession | null>,
  ): void {
    this.editor = { owner, prepare };
  }
  unregister(owner: object): void {
    if (this.editor?.owner === owner) {
      this.editor = undefined;
      this.session.set(null);
    }
  }
  hasEditor(): boolean {
    return !!this.editor;
  }
  async open(nodeId?: string): Promise<void> {
    if (!this.editor || this.opening()) return;
    this.opening.set(true);
    const editor = this.editor;
    try {
      const session = await editor.prepare(nodeId);
      if (this.editor === editor && session) this.session.set(session);
    } finally {
      this.opening.set(false);
    }
  }
  async apply(flow: FlowDocument): Promise<boolean> {
    const current = this.session();
    if (!current || this.applying()) return false;
    this.applying.set(true);
    try {
      const result = await current.apply(flow);
      if (result && this.session() === current) this.session.set(null);
      return result;
    } finally {
      this.applying.set(false);
    }
  }
}
