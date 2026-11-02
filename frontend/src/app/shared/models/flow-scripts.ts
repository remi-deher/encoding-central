export type FlowScriptLanguage = 'javascript' | 'python';

export interface FlowScriptDraft {
  readonly name: string;
  readonly language: FlowScriptLanguage;
  readonly description: string;
  readonly code: string;
}

export interface FlowScriptVersionSummary {
  readonly scriptId: string;
  readonly version: number;
  readonly name: string;
  readonly language: FlowScriptLanguage;
  readonly description: string;
  readonly createdAt: string;
}

export interface FlowScriptSummary {
  readonly id: string;
  readonly draftRevision: number;
  readonly draftName: string;
  readonly draftLanguage: FlowScriptLanguage;
  readonly draftDescription: string;
  readonly publishedVersion: number;
  readonly updatedAt: string;
  readonly versions: readonly FlowScriptVersionSummary[];
}

export interface FlowScriptDetails {
  readonly script: FlowScriptSummary;
  readonly draft: FlowScriptDraft;
}

export interface FlowScriptVersion extends FlowScriptVersionSummary {
  readonly code: string;
}
