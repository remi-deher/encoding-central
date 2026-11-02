import type { FlowDocument, FlowComponentPackInstance } from './flow-validation';

export interface FlowComponentPackSummary {
  readonly id: string;
  readonly name: string;
  readonly description: string;
  readonly mediaType: string;
  readonly sourceFlowId: string;
  readonly sourceVersion: number;
  readonly sourceFlowName: string;
  readonly componentCount: number;
  readonly createdAt: string;
}

export interface FlowComponentPackDetails {
  readonly pack: FlowComponentPackSummary;
  readonly definition: FlowDocument;
  readonly inputNodeId: string;
  readonly outputs: FlowComponentPackInstance['outputs'];
}

export interface CreateFlowComponentPackRequest {
  readonly sourceFlowId: string;
  readonly sourceVersion: number;
  readonly name: string;
  readonly description: string;
}
