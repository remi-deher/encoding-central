import { SystemLogEntry, SystemLogLevel } from './system-logs';

export const LOG_LEVEL_RANK: Readonly<Record<SystemLogLevel, number>> = {
  Trace: 0,
  Debug: 1,
  Information: 2,
  Warning: 3,
  Error: 4,
  Critical: 5,
};
export const LOG_LEVEL_LABELS: Readonly<Record<SystemLogLevel, string>> = {
  Trace: 'Trace',
  Debug: 'Débogage',
  Information: 'Information',
  Warning: 'Avertissement',
  Error: 'Erreur',
  Critical: 'Critique',
};
export interface SystemLogGroup {
  readonly key: string;
  readonly entry: SystemLogEntry;
  readonly entries: readonly SystemLogEntry[];
  readonly firstSeen: string;
  readonly lastSeen: string;
}

/** Exact grouping only: no guessing from free text, no rewritten messages. */
export function systemLogGroupKey(entry: SystemLogEntry): string {
  return JSON.stringify([
    entry.level,
    entry.category,
    entry.message,
    entry.exception ?? '',
    entry.nodeId ?? '',
    entry.nodeName ?? '',
    entry.eventId ?? null,
  ]);
}
export function mergeSystemLogs(
  entries: readonly SystemLogEntry[],
  limit: number,
): readonly SystemLogEntry[] {
  const unique = new Map(entries.map((entry) => [entry.id, entry]));
  return [...unique.values()]
    .sort((a, b) => Date.parse(a.timestamp) - Date.parse(b.timestamp) || a.id.localeCompare(b.id))
    .slice(-Math.max(1, limit));
}
export function summarizeSystemLogs(entries: readonly SystemLogEntry[]): readonly SystemLogGroup[] {
  const groups = new Map<string, SystemLogEntry[]>();
  for (const entry of entries) {
    const key = systemLogGroupKey(entry);
    const group = groups.get(key) ?? [];
    group.push(entry);
    groups.set(key, group);
  }
  return [...groups.entries()]
    .map(([key, items]) => {
      const ordered = [...items].sort((a, b) => Date.parse(a.timestamp) - Date.parse(b.timestamp));
      const latest = ordered[ordered.length - 1]!;
      return {
        key,
        entry: latest,
        entries: ordered,
        firstSeen: ordered[0]!.timestamp,
        lastSeen: latest.timestamp,
      };
    })
    .sort(
      (a, b) =>
        LOG_LEVEL_RANK[b.entry.level] - LOG_LEVEL_RANK[a.entry.level] ||
        Date.parse(b.lastSeen) - Date.parse(a.lastSeen),
    );
}
export function formatSystemLog(entry: SystemLogEntry): string {
  const prefix = `[${entry.timestamp}] [${entry.level.toUpperCase()}] [${entry.category}]${entry.nodeName ? ` [@${entry.nodeName}]` : ''}`;
  return `${prefix} ${entry.message}${entry.exception ? `\n${entry.exception}` : ''}`;
}
export function serializeSystemLogs(entries: readonly SystemLogEntry[]): string {
  return entries.map(formatSystemLog).join('\n');
}
export function systemLogsCsv(entries: readonly SystemLogEntry[]): string {
  const cell = (value: unknown): string => {
    let text = value == null ? '' : String(value);
    // Keep spreadsheet programs from executing arbitrary log contents as formulas.
    if (/^[=+@\-\t\r]/.test(text)) text = `'${text}`;
    return `"${text.replace(/"/g, '""')}"`;
  };
  return [
    [
      'id',
      'timestamp',
      'level',
      'category',
      'nodeId',
      'nodeName',
      'eventId',
      'message',
      'exception',
    ],
    ...entries.map((e) => [
      e.id,
      e.timestamp,
      e.level,
      e.category,
      e.nodeId,
      e.nodeName,
      e.eventId,
      e.message,
      e.exception,
    ]),
  ]
    .map((row) => row.map(cell).join(','))
    .join('\r\n');
}
