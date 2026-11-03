export type SystemLogLevel =
  | 'Trace'
  | 'Debug'
  | 'Information'
  | 'Warning'
  | 'Error'
  | 'Critical';

export interface SystemLogEntry {
  readonly id: string;
  readonly timestamp: string;
  readonly level: SystemLogLevel;
  readonly category: string;
  readonly message: string;
  readonly exception?: string | null;
  readonly workerId?: string | null;
  readonly workerName?: string | null;
  readonly eventId?: number | null;
}

export interface SystemLogFilterQuery {
  readonly minLevel?: SystemLogLevel;
  readonly category?: string;
  readonly search?: string;
  readonly workerId?: string;
  readonly limit?: number;
  readonly since?: string;
}

export interface SystemLogCategoriesResponse {
  readonly categories: readonly string[];
}

export interface SystemLogOverviewResponse {
  readonly entries: readonly SystemLogEntry[];
  readonly totalBuffered: number;
}
