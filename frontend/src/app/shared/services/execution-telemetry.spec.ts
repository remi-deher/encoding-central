import { TestBed } from '@angular/core/testing';
import { ExecutionTelemetry } from './execution-telemetry';

class FakeEventSource {
  static instances: FakeEventSource[] = [];
  readonly listeners = new Map<string, (event: MessageEvent<string>) => void>();
  closed = false;
  constructor(readonly url: string) {
    FakeEventSource.instances.push(this);
  }
  addEventListener(type: string, listener: (event: MessageEvent<string>) => void): void {
    this.listeners.set(type, listener);
  }
  close(): void {
    this.closed = true;
  }
  emit(type: string, data: unknown = {}): void {
    this.listeners.get(type)?.({ data: JSON.stringify(data) } as MessageEvent<string>);
  }
  fail(): void {
    this.listeners.get('error')?.({} as MessageEvent<string>);
  }
}

const startedAt = new Date(Date.UTC(2026, 0, 1)).toISOString();
const snapshot = {
  executionId: 'e1',
  workerId: 'w1',
  startedAt,
  currentNodeId: 'n1',
  steps: [{ nodeId: 'n1', state: 'started', startedAt, finishedAt: null }],
  progress: null,
  logs: [{ nodeId: 'n1', commandId: 'c1', stream: 'stderr', seq: 1, text: 'a', at: startedAt }],
};
const line = (seq: number, text = `l${seq}`) => ({
  nodeId: 'n1', commandId: 'c1', stream: 'stderr', seq, text, at: startedAt,
});

describe('ExecutionTelemetry', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date(Date.UTC(2026, 0, 1)));
    FakeEventSource.instances = [];
    vi.stubGlobal('EventSource', FakeEventSource);
  });
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('applies snapshot, steps, progress and logs, deduplicating by seq', () => {
    const handle = TestBed.inject(ExecutionTelemetry).execution('e1');
    const source = FakeEventSource.instances[0];
    expect(source.url).toBe('/api/v1/executions/e1/telemetry');
    expect(handle.connected()).toBe(false);
    source.emit('snapshot', snapshot);
    expect(handle.connected()).toBe(true);
    expect(handle.currentNodeId()).toBe('n1');
    expect(handle.logs().map((l) => l.text)).toEqual(['a']);

    source.emit('log', [line(1, 'dup'), line(2), line(3)]);
    expect(handle.logs().map((l) => l.text)).toEqual(['a', 'l2', 'l3']);
    source.emit('progress', { nodeId: 'n1', commandId: 'c1', percent: 42, bytesProcessed: null, elapsedMs: 5, at: startedAt });
    expect(handle.progress()?.percent).toBe(42);
    source.emit('step', { nodeId: 'n1', state: 'completed', startedAt, finishedAt: startedAt });
    expect(handle.steps()[0].state).toBe('completed');
    expect(handle.currentNodeId()).toBeNull();
    handle.close();
  });

  it('caps logs at 2000 lines', () => {
    const handle = TestBed.inject(ExecutionTelemetry).execution('e1');
    const source = FakeEventSource.instances[0];
    source.emit('snapshot', { ...snapshot, logs: [] });
    source.emit('log', Array.from({ length: 2500 }, (_, i) => line(i + 1)));
    expect(handle.logs().length).toBe(2000);
    expect(handle.logs()[1999].text).toBe('l2500');
    handle.close();
  });

  it('ticks elapsedMs every second', () => {
    const handle = TestBed.inject(ExecutionTelemetry).execution('e1');
    FakeEventSource.instances[0].emit('snapshot', snapshot);
    vi.advanceTimersByTime(3000);
    expect(handle.elapsedMs()).toBe(3000);
    handle.close();
  });

  it('closes and freezes on end', () => {
    const handle = TestBed.inject(ExecutionTelemetry).execution('e1');
    const source = FakeEventSource.instances[0];
    source.emit('snapshot', snapshot);
    vi.advanceTimersByTime(2000);
    source.emit('end');
    expect(handle.ended()).toBe(true);
    expect(handle.connected()).toBe(false);
    expect(source.closed).toBe(true);
    vi.advanceTimersByTime(60_000);
    expect(FakeEventSource.instances.length).toBe(1);
    expect(handle.elapsedMs()).toBe(2000);
  });

  it('reconnects with growing backoff after an error', () => {
    const handle = TestBed.inject(ExecutionTelemetry).execution('e1');
    FakeEventSource.instances[0].fail();
    expect(handle.connected()).toBe(false);
    vi.advanceTimersByTime(999);
    expect(FakeEventSource.instances.length).toBe(1);
    vi.advanceTimersByTime(1);
    expect(FakeEventSource.instances.length).toBe(2);
    FakeEventSource.instances[1].fail();
    vi.advanceTimersByTime(1999);
    expect(FakeEventSource.instances.length).toBe(2);
    vi.advanceTimersByTime(1);
    expect(FakeEventSource.instances.length).toBe(3);
    handle.close();
  });

  it('reports connected=false when silent for more than 30 s, and recovers', () => {
    const handle = TestBed.inject(ExecutionTelemetry).execution('e1');
    const source = FakeEventSource.instances[0];
    source.emit('ping');
    expect(handle.connected()).toBe(true);
    vi.advanceTimersByTime(29_000);
    source.emit('ping');
    vi.advanceTimersByTime(29_000);
    expect(handle.connected()).toBe(true);
    vi.advanceTimersByTime(1_001);
    expect(handle.connected()).toBe(false);
    expect(source.closed).toBe(true);
    vi.advanceTimersByTime(1000);
    FakeEventSource.instances[1].emit('snapshot', snapshot);
    expect(handle.connected()).toBe(true);
    handle.close();
  });

  it('closes the stream on close()', () => {
    const handle = TestBed.inject(ExecutionTelemetry).execution('e1');
    handle.close();
    expect(FakeEventSource.instances[0].closed).toBe(true);
    expect(handle.connected()).toBe(false);
  });

  it('tracks running executions and logs of a worker', () => {
    const handle = TestBed.inject(ExecutionTelemetry).worker('w1');
    const source = FakeEventSource.instances[0];
    expect(source.url).toBe('/api/v1/workers/w1/telemetry');
    source.emit('snapshot', [{ executionId: 'e1', startedAt, currentNodeId: 'n1', progress: null }]);
    expect(handle.running().length).toBe(1);
    source.emit('progress', { executionId: 'e1', nodeId: 'n2', commandId: 'c1', percent: 10, bytesProcessed: null, elapsedMs: 1, at: startedAt });
    expect(handle.running()[0].progress?.percent).toBe(10);
    expect(handle.running()[0].currentNodeId).toBe('n2');
    source.emit('log', { executionId: 'e1', logs: [line(1)] });
    expect(handle.logsByExecution()['e1'].length).toBe(1);
    source.emit('end', { executionId: 'e1' });
    expect(handle.running()).toEqual([]);
    expect(source.closed).toBe(false);
    handle.close();
  });
});
