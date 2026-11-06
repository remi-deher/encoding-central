import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { WorkerRunningExecution } from '../../../shared/models/central';
import {
  ExecutionTelemetry,
  WorkerTelemetryHandle,
} from '../../../shared/services/execution-telemetry';
import { ExecutionTracking } from '../../../shared/services/execution-tracking';
import { WorkerRunningTasksComponent } from './worker-running-tasks.component';

describe('WorkerRunningTasksComponent', () => {
  let running: ReturnType<typeof signal<readonly WorkerRunningExecution[]>>;
  let logsByExecution: ReturnType<
    typeof signal<
      Readonly<
        Record<
          string,
          readonly {
            commandId: string;
            seq: number;
            at: string;
            text: string;
            nodeId: string;
            stream: string;
          }[]
        >
      >
    >
  >;
  let connected: ReturnType<typeof signal<boolean>>;
  let executionTelemetry: { worker: ReturnType<typeof vi.fn> };
  let tracking: { execution: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    running = signal<readonly WorkerRunningExecution[]>([]);
    logsByExecution = signal({});
    connected = signal(false);
    executionTelemetry = { worker: vi.fn() };
    executionTelemetry.worker.mockReturnValue({
      running: running.asReadonly(),
      logsByExecution: logsByExecution.asReadonly(),
      connected: connected.asReadonly(),
      close: () => undefined,
    } satisfies WorkerTelemetryHandle);
    tracking = { execution: vi.fn() };
    tracking.execution.mockReturnValue(
      of({
        id: 'exec-1',
        media: '/media/feature-film.mkv',
        flow: 'Encode HD',
        currentPhase: 'Transcodage',
      } as never),
    );
    TestBed.configureTestingModule({
      imports: [WorkerRunningTasksComponent],
      providers: [
        { provide: ExecutionTelemetry, useValue: executionTelemetry },
        { provide: ExecutionTracking, useValue: tracking },
      ],
    });
  });

  function create() {
    const fixture = TestBed.createComponent(WorkerRunningTasksComponent);
    fixture.componentRef.setInput('workerId', 'worker-1');
    fixture.componentRef.setInput('activityCount', 3);
    fixture.detectChanges();
    return fixture;
  }

  it('falls back to worker activity counts while telemetry is disconnected', () => {
    const fixture = create();
    expect(fixture.nativeElement.textContent).toContain('3 tâches actives');
    expect(fixture.nativeElement.textContent).not.toContain('Aucun traitement en cours');
  });

  it('shows the idle state after a connected empty snapshot', () => {
    const fixture = create();
    connected.set(true);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Aucun traitement en cours.');
    expect(fixture.nativeElement.textContent).not.toContain('Télémétrie indisponible');
  });

  it('renders running task metadata, progress, live logs and an activity link', () => {
    const fixture = create();
    const startedAt = new Date(Date.now() - 65_000).toISOString();
    running.set([
      {
        executionId: 'exec-1',
        startedAt,
        currentNodeId: 'node-transcode',
        progress: {
          nodeId: 'node-transcode',
          commandId: 'cmd-1',
          percent: 43,
          bytesProcessed: null,
          elapsedMs: 1000,
          at: startedAt,
        },
      },
    ]);
    logsByExecution.set({
      'exec-1': [
        {
          commandId: 'cmd-1',
          seq: 1,
          at: startedAt,
          text: 'frame=42 fps=24',
          nodeId: 'node-transcode',
          stream: 'stdout',
        },
      ],
    });
    connected.set(true);
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;
    expect(element.textContent).toContain('feature-film.mkv');
    expect(element.textContent).toContain('Encode HD');
    expect(element.textContent).toContain('Transcodage');
    expect(element.textContent).toMatch(/1 min 0[45] s/);
    expect(element.querySelector('progress')?.getAttribute('value')).toBe('43');
    expect(element.textContent).toContain('frame=42 fps=24');
    expect(element.querySelector('a.execution-link')?.getAttribute('href')).toContain(
      'execution=exec-1',
    );
    expect(tracking.execution).toHaveBeenCalledWith('exec-1');
  });
});
