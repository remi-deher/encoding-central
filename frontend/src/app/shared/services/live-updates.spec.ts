import { TestBed } from '@angular/core/testing';
import { LiveUpdates } from './live-updates';

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
  emit(type: string, data: unknown): void {
    this.listeners.get(type)?.({ data: JSON.stringify(data) } as MessageEvent<string>);
  }
}

describe('LiveUpdates', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    FakeEventSource.instances = [];
    vi.stubGlobal('EventSource', FakeEventSource);
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('loads at once, then reloads when the wanted topic changes', () => {
    const live = TestBed.inject(LiveUpdates);
    const loads = vi.fn();
    const subscription = live.drive(['workers']).subscribe(loads);
    expect(loads).toHaveBeenCalledTimes(1);

    const source = FakeEventSource.instances[0];
    expect(source.url).toBe('/api/v1/live');
    source.emit('change', { topics: ['workers'] });
    vi.advanceTimersByTime(300);
    expect(loads).toHaveBeenCalledTimes(2);
    subscription.unsubscribe();
    expect(source.closed).toBe(true);
  });

  it('ignores changes on other topics and merges close announcements', () => {
    const live = TestBed.inject(LiveUpdates);
    const loads = vi.fn();
    const subscription = live.drive(['workers']).subscribe(loads);
    const source = FakeEventSource.instances[0];

    source.emit('change', { topics: ['libraries'] });
    vi.advanceTimersByTime(300);
    expect(loads).toHaveBeenCalledTimes(1);

    source.emit('change', { topics: ['workers'] });
    source.emit('change', { topics: ['workers'] });
    vi.advanceTimersByTime(300);
    expect(loads).toHaveBeenCalledTimes(2);
    subscription.unsubscribe();
  });

  it('falls back to a slow reload only while the stream is silent', () => {
    const live = TestBed.inject(LiveUpdates);
    const loads = vi.fn();
    const subscription = live.drive(['workers'], { quietMs: 10_000 }).subscribe(loads);
    const source = FakeEventSource.instances[0];
    source.emit('ping', {});
    vi.advanceTimersByTime(10_000);
    expect(loads).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(40_000);
    expect(loads.mock.calls.length).toBeGreaterThan(1);
    subscription.unsubscribe();
  });

  it('keeps a permanent slow refresh for state no call changes', () => {
    const live = TestBed.inject(LiveUpdates);
    const loads = vi.fn();
    const subscription = live
      .drive(['flows'], { quietMs: 600_000, refreshMs: 30_000 })
      .subscribe(loads);
    FakeEventSource.instances[0].emit('ready', {});
    vi.advanceTimersByTime(65_000);
    expect(loads).toHaveBeenCalledTimes(3);
    subscription.unsubscribe();
  });
});
