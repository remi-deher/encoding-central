import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { AiAssistantActivity } from '../models/ai-tools';
import { AiToolsService } from './ai-tools.service';

type Listener = (event: MessageEvent<string>) => void;

class FakeEventSource {
  static readonly CLOSED = 2;
  static instances: FakeEventSource[] = [];
  readonly listeners = new Map<string, Listener[]>();
  readyState = 1;
  closed = false;
  onerror: (() => void) | null = null;
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

describe('AI assistant activity stream', () => {
  const original = (globalThis as unknown as { EventSource?: unknown }).EventSource;
  const requestId = '11111111-1111-1111-1111-111111111111';

  beforeEach(() => {
    FakeEventSource.instances = [];
    (globalThis as unknown as { EventSource: unknown }).EventSource = FakeEventSource;
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
  });
  afterEach(() => {
    (globalThis as unknown as { EventSource?: unknown }).EventSource = original;
  });

  it('emits each progress change and completes when the server ends the request', () => {
    const seen: string[] = [];
    let completed = false;
    TestBed.inject(AiToolsService)
      .streamAssistantActivity(requestId)
      .subscribe({
        next: (activity) => seen.push(activity.progress ?? activity.status),
        complete: () => (completed = true),
      });
    const source = FakeEventSource.instances[0];
    expect(source.url).toBe(`/api/v1/tools/ai/assistant/activity/${requestId}/stream`);
    source.emit('activity', { status: 'running', progress: 'preparing' } as AiAssistantActivity);
    source.emit('activity', { status: 'running', progress: 'generating' } as AiAssistantActivity);
    source.emit('ping', {});
    expect(seen).toEqual(['preparing', 'generating']);
    expect(completed).toBe(false);
    source.emit('end', {});
    expect(completed).toBe(true);
    expect(source.closed).toBe(true);
  });

  it('errors, so the page falls back to polling, once the stream is refused for good', () => {
    let failed = false;
    TestBed.inject(AiToolsService)
      .streamAssistantActivity(requestId)
      .subscribe({ error: () => (failed = true) });
    const source = FakeEventSource.instances[0];
    source.readyState = 0; // reconnexion en cours : ce n'est pas un échec
    source.onerror?.();
    expect(failed).toBe(false);
    source.readyState = FakeEventSource.CLOSED;
    source.onerror?.();
    expect(failed).toBe(true);
  });

  it('errors at once without EventSource support', () => {
    (globalThis as unknown as { EventSource?: unknown }).EventSource = undefined;
    let failed = false;
    TestBed.inject(AiToolsService)
      .streamAssistantActivity(requestId)
      .subscribe({ error: () => (failed = true) });
    expect(failed).toBe(true);
  });
});
