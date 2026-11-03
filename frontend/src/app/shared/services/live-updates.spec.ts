import { TestBed } from '@angular/core/testing';
import { LiveUpdates } from './live-updates';

type Listener = (event: MessageEvent<string>) => void;

class FakeEventSource {
  static instances: FakeEventSource[] = [];
  readonly listeners = new Map<string, Listener[]>();
  closed = false;
  constructor(readonly url: string) {
    FakeEventSource.instances.push(this);
  }
  addEventListener(type: string, listener: Listener): void {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener]);
  }
  close(): void {
    this.closed = true;
  }
  emit(type: string, data: unknown): void {
    const event = { data: JSON.stringify(data) } as MessageEvent<string>;
    for (const listener of this.listeners.get(type) ?? []) listener(event);
  }
}

describe('LiveUpdates', () => {
  const original = (globalThis as unknown as { EventSource?: unknown }).EventSource;

  beforeEach(() => {
    vi.useFakeTimers();
    FakeEventSource.instances = [];
    (globalThis as unknown as { EventSource: unknown }).EventSource = FakeEventSource;
  });
  afterEach(() => {
    vi.useRealTimers();
    (globalThis as unknown as { EventSource?: unknown }).EventSource = original;
  });

  function drive(options?: { quietMs?: number; refreshMs?: number }) {
    const live = TestBed.inject(LiveUpdates);
    const calls = { count: 0 };
    const subscription = live.drive(['flows'], options).subscribe(() => calls.count++);
    return { calls, subscription, source: () => FakeEventSource.instances[0] };
  }

  it('emits at once, then only for the topics asked for, merging bursts', async () => {
    const { calls, subscription, source } = drive();
    expect(calls.count).toBe(1);
    source().emit('ready', {});
    source().emit('change', { topics: ['libraries'] });
    await vi.advanceTimersByTimeAsync(1000);
    expect(calls.count).toBe(1);
    source().emit('change', { topics: ['flows'] });
    source().emit('change', { topics: ['flows', 'plugins'] });
    await vi.advanceTimersByTimeAsync(1000);
    expect(calls.count).toBe(2);
    subscription.unsubscribe();
    expect(source().closed).toBe(true);
  });

  it('falls back to a slow reload only while the stream is silent', async () => {
    const { calls, subscription, source } = drive({ quietMs: 10_000 });
    source().emit('ping', {});
    await vi.advanceTimersByTimeAsync(10_000);
    expect(calls.count).toBe(1);
    source().emit('ping', {});
    await vi.advanceTimersByTimeAsync(5_000);
    expect(calls.count).toBe(1);
    await vi.advanceTimersByTimeAsync(30_000);
    expect(calls.count).toBeGreaterThan(1);
    subscription.unsubscribe();
  });

  it('keeps a permanent slow refresh for state no call changes', async () => {
    const { calls, subscription, source } = drive({ quietMs: 600_000, refreshMs: 30_000 });
    source().emit('ready', {});
    await vi.advanceTimersByTimeAsync(65_000);
    expect(calls.count).toBe(3);
    subscription.unsubscribe();
  });
});
