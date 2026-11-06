import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { ExecutionTelemetry } from '../../../shared/services/execution-telemetry';
import { ExecutionTracking } from '../../../shared/services/execution-tracking';
import { FlowValidation } from '../../../shared/services/flow-validation';
import { ActivityFlowTraceComponent } from './activity-flow-trace.component';

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
}

const at = new Date(Date.UTC(2026, 0, 1)).toISOString();
const polled = (status: 'running' | 'completed') => ({
  nodeId: 'n1', nodeName: 'Encode', invocationId: 'i1', componentKind: 'execution', status,
  startedAt: at, completedAt: null, outputId: null, outputLabel: null, nextNodeId: null, commands: [],
});

describe('ActivityFlowTraceComponent live telemetry', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    FakeEventSource.instances = [];
    vi.stubGlobal('EventSource', FakeEventSource);
    TestBed.configureTestingModule({
      providers: [
        { provide: ExecutionTracking, useValue: { flow: () => of(null) } },
        { provide: FlowValidation, useValue: { catalog: () => Promise.resolve([]) } },
      ],
    });
  });
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  function create(state: string) {
    const fixture = TestBed.createComponent(ActivityFlowTraceComponent);
    fixture.componentRef.setInput('executionId', 'e1');
    fixture.componentRef.setInput('executionState', state);
    fixture.componentRef.setInput('steps', [polled('running')]);
    fixture.detectChanges();
    return fixture;
  }

  it('does not open a stream when the execution is not running', () => {
    create('succeeded');
    expect(FakeEventSource.instances.length).toBe(0);
  });

  it('marks the current node and completes steps from step events before the poll', () => {
    const fixture = create('running');
    const source = FakeEventSource.instances[0];
    expect(source.url).toBe('/api/v1/executions/e1/telemetry');
    const component = fixture.componentInstance as unknown as {
      activeNodeId(): string | null;
      effectiveSteps(): { status: string }[];
    };
    expect(component.activeNodeId()).toBeNull();
    source.emit('snapshot', {
      executionId: 'e1', workerId: 'w', startedAt: at, currentNodeId: 'n1',
      steps: [{ nodeId: 'n1', state: 'started', startedAt: at, finishedAt: null }],
      progress: null, logs: [],
    });
    expect(component.activeNodeId()).toBe('n1');
    source.emit('step', { nodeId: 'n1', state: 'completed', startedAt: at, finishedAt: at });
    expect(component.activeNodeId()).toBeNull();
    expect(component.effectiveSteps()[0].status).toBe('completed');
  });

  it('falls back to polled steps when the stream ends', () => {
    const fixture = create('running');
    const source = FakeEventSource.instances[0];
    source.emit('step', { nodeId: 'n1', state: 'failed', startedAt: at, finishedAt: at });
    const component = fixture.componentInstance as unknown as { effectiveSteps(): { status: string }[] };
    expect(component.effectiveSteps()[0].status).toBe('failed');
    source.emit('end');
    expect(component.effectiveSteps()[0].status).toBe('running');
  });

  it('closes the stream when destroyed', () => {
    const fixture = create('running');
    fixture.destroy();
    expect(FakeEventSource.instances[0].closed).toBe(true);
  });
});
