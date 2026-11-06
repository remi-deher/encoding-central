import { DestroyRef, Injectable, Signal, computed, signal } from '@angular/core';
import {
  ExecutionTelemetryLogLine,
  ExecutionTelemetryProgress,
  ExecutionTelemetrySnapshot,
  ExecutionTelemetryStep,
  WorkerRunningExecution,
} from '../models/central';

export const TELEMETRY_MAX_LOG_LINES = 2000;
export const TELEMETRY_SILENCE_MS = 30_000;
const BACKOFF_START_MS = 1_000;
const BACKOFF_MAX_MS = 30_000;

export interface ExecutionTelemetryHandle {
  readonly snapshot: Signal<ExecutionTelemetrySnapshot | null>;
  readonly currentNodeId: Signal<string | null>;
  readonly steps: Signal<readonly ExecutionTelemetryStep[]>;
  readonly progress: Signal<ExecutionTelemetryProgress | null>;
  /** Dernières lignes (2000 au plus), toutes commandes confondues, dans l'ordre d'arrivée. */
  readonly logs: Signal<readonly ExecutionTelemetryLogLine[]>;
  readonly elapsedMs: Signal<number>;
  /** Faux tant que le flux n'a rien reçu depuis 30 s ou n'est pas ouvert : gardez votre sondage. */
  readonly connected: Signal<boolean>;
  readonly ended: Signal<boolean>;
  close(): void;
}

export interface WorkerTelemetryHandle {
  readonly running: Signal<readonly WorkerRunningExecution[]>;
  /** Lignes récentes par identifiant d'exécution (2000 au plus par exécution). */
  readonly logsByExecution: Signal<Readonly<Record<string, readonly ExecutionTelemetryLogLine[]>>>;
  readonly connected: Signal<boolean>;
  close(): void;
}

interface StreamCallbacks {
  readonly message: (type: string, data: unknown) => void;
  readonly connected: (value: boolean) => void;
  /** Absent : un événement `end` ne ferme pas le flux (flux d'un worker). */
  readonly done?: () => void;
}

const EVENTS = ['snapshot', 'step', 'progress', 'log', 'end', 'ping'] as const;

/**
 * Télémétrie temps réel d'une exécution ou d'un worker, en SSE (ADR 0045).
 * Au mieux : `connected` retombe à faux si le flux se tait, l'appelant garde alors son sondage.
 */
@Injectable({ providedIn: 'root' })
export class ExecutionTelemetry {
  execution(id: string, destroyRef?: DestroyRef): ExecutionTelemetryHandle {
    const snapshot = signal<ExecutionTelemetrySnapshot | null>(null);
    const steps = signal<readonly ExecutionTelemetryStep[]>([]);
    const currentNodeId = signal<string | null>(null);
    const progress = signal<ExecutionTelemetryProgress | null>(null);
    const logs = signal<readonly ExecutionTelemetryLogLine[]>([]);
    const connected = signal(false);
    const ended = signal(false);
    const now = signal(Date.now());
    const frozenAt = signal<number | null>(null);
    const lastSeq = new Map<string, number>();
    let ticker: ReturnType<typeof setInterval> | undefined;

    const startedAt = computed(() => {
      const value = snapshot()?.startedAt;
      const parsed = value ? Date.parse(value) : NaN;
      return Number.isNaN(parsed) ? null : parsed;
    });
    const elapsedMs = computed(() => {
      const start = startedAt();
      if (start === null) return progress()?.elapsedMs ?? 0;
      return Math.max(0, (frozenAt() ?? now()) - start);
    });

    const addLogs = (lines: readonly ExecutionTelemetryLogLine[]) => {
      const fresh = lines.filter((line) => {
        const last = lastSeq.get(line.commandId);
        if (last !== undefined && line.seq <= last) return false;
        lastSeq.set(line.commandId, line.seq);
        return true;
      });
      if (fresh.length === 0) return;
      logs.update((current) => [...current, ...fresh].slice(-TELEMETRY_MAX_LOG_LINES));
    };
    const applyStep = (step: ExecutionTelemetryStep) => {
      steps.update((current) =>
        current.some((entry) => entry.nodeId === step.nodeId)
          ? current.map((entry) => (entry.nodeId === step.nodeId ? step : entry))
          : [...current, step],
      );
      if (step.state === 'started') currentNodeId.set(step.nodeId);
      else if (currentNodeId() === step.nodeId) currentNodeId.set(null);
    };
    const stop = () => {
      if (ticker !== undefined) clearInterval(ticker);
      ticker = undefined;
    };

    const stream = openStream(`/api/v1/executions/${encodeURIComponent(id)}/telemetry`, {
      connected: (value) => connected.set(value),
      done: () => {
        ended.set(true);
        frozenAt.set(Date.now());
        stop();
      },
      message: (type, data) => {
        if (type === 'snapshot' && isObject(data)) {
          const value = data as unknown as ExecutionTelemetrySnapshot;
          lastSeq.clear();
          snapshot.set(value);
          steps.set([...(value.steps ?? [])]);
          currentNodeId.set(value.currentNodeId ?? null);
          progress.set(value.progress ?? null);
          logs.set([]);
          addLogs(value.logs ?? []);
        } else if (type === 'step' && isObject(data)) applyStep(data as unknown as ExecutionTelemetryStep);
        else if (type === 'progress' && isObject(data)) progress.set(data as unknown as ExecutionTelemetryProgress);
        else if (type === 'log') addLogs(asLines(data));
      },
    });
    ticker = setInterval(() => now.set(Date.now()), 1000);
    destroyRef?.onDestroy(() => handle.close());

    const handle: ExecutionTelemetryHandle = {
      snapshot: snapshot.asReadonly(),
      currentNodeId: currentNodeId.asReadonly(),
      steps: steps.asReadonly(),
      progress: progress.asReadonly(),
      logs: logs.asReadonly(),
      elapsedMs,
      connected: connected.asReadonly(),
      ended: ended.asReadonly(),
      close: () => {
        stream.close();
        stop();
        connected.set(false);
      },
    };
    return handle;
  }

  worker(id: string, destroyRef?: DestroyRef): WorkerTelemetryHandle {
    const running = signal<readonly WorkerRunningExecution[]>([]);
    const logsByExecution = signal<Readonly<Record<string, readonly ExecutionTelemetryLogLine[]>>>({});
    const connected = signal(false);
    const lastSeq = new Map<string, number>();

    const executionIdOf = (data: unknown): string | null =>
      isObject(data) && typeof data['executionId'] === 'string' ? (data['executionId'] as string) : null;
    const patch = (executionId: string, change: (entry: WorkerRunningExecution) => WorkerRunningExecution) =>
      running.update((list) => list.map((entry) => (entry.executionId === executionId ? change(entry) : entry)));

    const stream = openStream(`/api/v1/workers/${encodeURIComponent(id)}/telemetry`, {
      connected: (value) => connected.set(value),
      message: (type, data) => {
        if (type === 'snapshot') {
          const list = Array.isArray(data) ? data : isObject(data) && Array.isArray(data['running']) ? data['running'] : [];
          const next = list as WorkerRunningExecution[];
          running.set(next);
          const alive = new Set(next.map((entry) => entry.executionId));
          logsByExecution.update((current) =>
            Object.fromEntries(Object.entries(current).filter(([key]) => alive.has(key))),
          );
          return;
        }
        const executionId = executionIdOf(data);
        if (!executionId) return;
        if (type === 'end') {
          running.update((list) => list.filter((entry) => entry.executionId !== executionId));
        } else if (type === 'progress' && isObject(data)) {
          const value = data as unknown as ExecutionTelemetryProgress;
          patch(executionId, (entry) => ({ ...entry, progress: value, currentNodeId: value.nodeId ?? entry.currentNodeId }));
        } else if (type === 'step' && isObject(data)) {
          const value = data as unknown as ExecutionTelemetryStep;
          if (!running().some((entry) => entry.executionId === executionId)) {
            running.update((list) => [
              ...list,
              { executionId, startedAt: value.startedAt, currentNodeId: value.nodeId, progress: null },
            ]);
          } else if (value.state === 'started') {
            patch(executionId, (entry) => ({ ...entry, currentNodeId: value.nodeId }));
          }
        } else if (type === 'log') {
          const fresh = asLines(data).filter((line) => {
            const key = `${executionId}:${line.commandId}`;
            const last = lastSeq.get(key);
            if (last !== undefined && line.seq <= last) return false;
            lastSeq.set(key, line.seq);
            return true;
          });
          if (fresh.length === 0) return;
          logsByExecution.update((current) => ({
            ...current,
            [executionId]: [...(current[executionId] ?? []), ...fresh].slice(-TELEMETRY_MAX_LOG_LINES),
          }));
        }
      },
    });
    destroyRef?.onDestroy(() => handle.close());
    const handle: WorkerTelemetryHandle = {
      running: running.asReadonly(),
      logsByExecution: logsByExecution.asReadonly(),
      connected: connected.asReadonly(),
      close: () => {
        stream.close();
        connected.set(false);
      },
    };
    return handle;
  }
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** Le serveur envoie un lot de lignes ; une ligne seule est aussi acceptée. */
function asLines(data: unknown): ExecutionTelemetryLogLine[] {
  if (Array.isArray(data)) return data as ExecutionTelemetryLogLine[];
  if (isObject(data) && Array.isArray(data['lines'])) return data['lines'] as ExecutionTelemetryLogLine[];
  if (isObject(data) && Array.isArray(data['logs'])) return data['logs'] as ExecutionTelemetryLogLine[];
  if (isObject(data) && typeof data['text'] === 'string') return [data as unknown as ExecutionTelemetryLogLine];
  return [];
}

/** Ouvre un EventSource avec reprise à délai croissant et veille de silence (30 s). */
function openStream(url: string, callbacks: StreamCallbacks): { close(): void } {
  let source: EventSource | undefined;
  let closed = false;
  let delay = BACKOFF_START_MS;
  let retry: ReturnType<typeof setTimeout> | undefined;
  let watchdog: ReturnType<typeof setTimeout> | undefined;

  const clearTimers = () => {
    if (retry !== undefined) clearTimeout(retry);
    if (watchdog !== undefined) clearTimeout(watchdog);
    retry = watchdog = undefined;
  };
  const close = () => {
    closed = true;
    clearTimers();
    source?.close();
    source = undefined;
  };
  const scheduleRetry = () => {
    if (closed) return;
    source?.close();
    source = undefined;
    callbacks.connected(false);
    if (watchdog !== undefined) clearTimeout(watchdog);
    watchdog = undefined;
    retry = setTimeout(connect, delay);
    delay = Math.min(delay * 2, BACKOFF_MAX_MS);
  };
  const touch = () => {
    callbacks.connected(true);
    if (watchdog !== undefined) clearTimeout(watchdog);
    watchdog = setTimeout(scheduleRetry, TELEMETRY_SILENCE_MS);
  };
  const connect = () => {
    retry = undefined;
    if (closed || typeof EventSource === 'undefined') return;
    const current = new EventSource(url, { withCredentials: true });
    source = current;
    watchdog = setTimeout(scheduleRetry, TELEMETRY_SILENCE_MS);
    for (const type of EVENTS) {
      current.addEventListener(type, (event: Event) => {
        if (closed || source !== current) return;
        delay = BACKOFF_START_MS;
        touch();
        if (type === 'ping') return;
        let data: unknown = {};
        try {
          const raw = (event as MessageEvent<string>).data;
          data = raw ? JSON.parse(raw) : {};
        } catch {
          return;
        }
        if (type === 'end' && callbacks.done) {
          callbacks.message(type, data);
          callbacks.done();
          close();
          callbacks.connected(false);
          return;
        }
        callbacks.message(type, data);
      });
    }
    current.addEventListener('error', () => {
      if (!closed && source === current) scheduleRetry();
    });
  };
  connect();
  return { close };
}
