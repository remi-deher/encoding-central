import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, Router } from '@angular/router';
import { BehaviorSubject, NEVER, Observable, of } from 'rxjs';
import { ActivityPageComponent } from './activity-page.component';
import { LibraryActivity } from '../../shared/services/library-activity';
import { ExecutionTracking } from '../../shared/services/execution-tracking';
import { ExecutionDetailPolling } from '../../shared/services/execution-detail-polling';
import { CentralApi } from '../../shared/services/central-api';
import { PageShellContextService } from '../../core/layout/page-shell-context';
import { FileDownload } from '../../shared/services/file-download';
import { NotificationService } from '../../shared/services/notification.service';
import { MatDialog } from '@angular/material/dialog';
import { ExecutionResponse } from '../../shared/models/execution';
import { LibraryActivityFile } from '../../shared/models/library-activity';

const executionId = 'a91df0a9-94c0-4f51-8557-4992386f0830';
const execution: ExecutionResponse = {
  id: executionId, media: '/library/movie.mkv', library: 'Films', flow: 'Encode HD', flowVersion: 2,
  state: 'running', workerId: 'worker-1', worker: 'Worker 1', progress: 30, detail: 'Transcodage',
  attempt: 1, revision: 3, queuedAt: '2026-11-04T10:00:00Z', startedAt: '2026-11-04T10:01:00Z',
  finishedAt: null, updatedAt: '2026-11-04T10:02:00Z',
};

function emptyActivity() {
  return {
    files: [], counts: { untreated: 0, active: 0, done: 0, failed: 0, disabled: 0 },
    totalCount: 0, limit: 50, offset: 0, observedAt: '2026-11-04T10:02:00Z',
  };
}

describe('ActivityPageComponent execution deep link', () => {
  let router: { navigate: ReturnType<typeof vi.fn> };
  let tracking: { state$: Observable<never>; execution: ReturnType<typeof vi.fn> };

  function create(params: Record<string, string> = {}, response: ExecutionResponse | null = execution) {
    router = { navigate: vi.fn().mockResolvedValue(true) };
    tracking = { state$: NEVER, execution: vi.fn().mockReturnValue(of(response)) };
    const queryParamMap = new BehaviorSubject(convertToParamMap(params));
    const route = {
      snapshot: { queryParamMap: convertToParamMap(params) },
      queryParamMap: queryParamMap.asObservable(),
    };
    TestBed.configureTestingModule({
      imports: [ActivityPageComponent],
      providers: [
        { provide: ActivatedRoute, useValue: route }, { provide: Router, useValue: router },
        { provide: LibraryActivity, useValue: { stream: () => NEVER, list: () => of(emptyActivity()) } },
        { provide: ExecutionTracking, useValue: tracking },
        { provide: ExecutionDetailPolling, useValue: { observe: () => ({ events: of({ message: '', rows: [] }), artifacts: of({ message: '', rows: [] }), pipeline: of({ message: '', steps: [] }) }) } },
        { provide: CentralApi, useValue: { listLibraries: () => of([]), listNodes: () => of([]) } },
        { provide: PageShellContextService, useValue: { register: () => () => undefined } },
        { provide: FileDownload, useValue: {} },
        { provide: NotificationService, useValue: { show: vi.fn() } },
        { provide: MatDialog, useValue: { open: vi.fn() } },
      ],
    });
    return TestBed.createComponent(ActivityPageComponent);
  }

  it('opens an execution from a URL id while preserving existing filters', () => {
    const fixture = create({ execution: executionId, view: 'active', node: 'worker-1', flow: 'Encode HD' });
    const component = fixture.componentInstance as unknown as {
      selectedExecution: () => ExecutionResponse | null;
      selectedFile: () => { relativePath: string } | null;
      status: () => string; nodeFilter: () => string | null; flowFilter: () => string | null;
    };

    expect(tracking.execution).toHaveBeenCalledWith(executionId);
    expect(component.selectedExecution()?.id).toBe(executionId);
    expect(component.selectedFile()?.relativePath).toBe('/library/movie.mkv');
    expect(component.status()).toBe('active');
    expect(component.nodeFilter()).toBe('worker-1');
    expect(component.flowFilter()).toBe('Encode HD');
  });

  it('adds the selected execution to the URL with replaceUrl', () => {
    const fixture = create({ view: 'active', node: 'worker-1' });
    const file = {
      id: 'file-1', executionId, relativePath: '/library/movie.mkv',
    } as LibraryActivityFile;
    (fixture.componentInstance as unknown as { open(file: LibraryActivityFile): void }).open(file);

    expect(router.navigate).toHaveBeenCalledWith([], expect.objectContaining({
      queryParams: { execution: executionId }, queryParamsHandling: 'merge', replaceUrl: true,
    }));
  });

  it('removes the execution query when the drawer closes using replaceUrl', () => {
    const fixture = create({ execution: executionId });
    (fixture.componentInstance as unknown as { close(): void }).close();
    expect(router.navigate).toHaveBeenCalledWith([], expect.objectContaining({
      queryParams: { execution: null }, queryParamsHandling: 'merge', replaceUrl: true,
    }));
  });

  it('ignores an unknown execution id without showing an error', () => {
    const unknownId = 'a91df0a9-94c0-4f51-8557-4992386f0831';
    const fixture = create({ execution: unknownId }, null);
    const component = fixture.componentInstance as unknown as {
      selectedExecution: () => ExecutionResponse | null; selectedFile: () => unknown;
    };
    expect(tracking.execution).toHaveBeenCalledWith(unknownId);
    expect(component.selectedExecution()).toBeNull();
    expect(component.selectedFile()).toBeNull();
    expect(router.navigate).toHaveBeenCalledWith([], expect.objectContaining({
      queryParams: { execution: null }, replaceUrl: true,
    }));
  });
});
