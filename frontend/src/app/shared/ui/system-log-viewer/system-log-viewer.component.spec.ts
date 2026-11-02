import { TestBed } from '@angular/core/testing';
import { of, Subject } from 'rxjs';
import { SystemLogViewerComponent } from './system-log-viewer.component';
import { SystemLogsService } from '../../services/system-logs.service';
import { ClipboardService } from '../../services/clipboard.service';
import { FileDownload } from '../../services/file-download';
import { NotificationService } from '../../services/notification.service';
import { SystemLogEntry } from '../../models/system-logs';

describe('SystemLogViewerComponent', () => {
  let stream: Subject<SystemLogEntry>;
  let service: {
    getLogs: ReturnType<typeof vi.fn>;
    getCategories: ReturnType<typeof vi.fn>;
    createLogStream: ReturnType<typeof vi.fn>;
  };
  let clipboard: { copy: ReturnType<typeof vi.fn> };
  let downloads: { save: ReturnType<typeof vi.fn> };
  const info: SystemLogEntry = {
    id: 'one',
    timestamp: '2026-10-19T08:00:00Z',
    level: 'Information',
    category: 'Encoding.Api',
    message: 'Server started successfully',
  };
  const error: SystemLogEntry = {
    id: 'two',
    timestamp: '2026-10-19T08:00:01Z',
    level: 'Error',
    category: 'Encoding.Worker',
    message: 'Worker execution failed',
    exception: 'InvalidOperationException\n  at Worker()',
  };
  beforeEach(async () => {
    stream = new Subject();
    service = {
      getLogs: vi.fn().mockReturnValue(of({ entries: [info], totalBuffered: 1 })),
      getCategories: vi.fn().mockReturnValue(of(['Encoding.Api', 'Encoding.Worker'])),
      createLogStream: vi.fn().mockReturnValue(stream),
    };
    clipboard = { copy: vi.fn().mockResolvedValue(true) };
    downloads = { save: vi.fn() };
    await TestBed.configureTestingModule({
      imports: [SystemLogViewerComponent],
      providers: [
        { provide: SystemLogsService, useValue: service },
        { provide: ClipboardService, useValue: clipboard },
        { provide: FileDownload, useValue: downloads },
        { provide: NotificationService, useValue: { show: vi.fn() } },
      ],
    }).compileComponents();
  });
  function setup() {
    const fixture = TestBed.createComponent(SystemLogViewerComponent);
    fixture.detectChanges();
    return fixture;
  }
  it('defaults to a summary of original messages and groups exact repetitions', () => {
    service.getLogs.mockReturnValue(
      of({
        entries: [info, error, { ...error, id: 'three', timestamp: '2026-10-19T08:00:02Z' }],
        totalBuffered: 3,
      }),
    );
    const fixture = setup();
    const component = fixture.componentInstance;
    expect(component['view']()).toBe('summary');
    expect(component['groups']()).toHaveLength(2);
    expect(component['groups']()[0].entries).toHaveLength(2);
    expect(component['groups']()[0].entry.message).toBe(error.message);
    expect(fixture.nativeElement.querySelector('.terminal-console')).toBeNull();
  });
  it('opens matching console lines without removing their context or exceptions', () => {
    service.getLogs.mockReturnValue(of({ entries: [error, info], totalBuffered: 2 }));
    const fixture = setup();
    const component = fixture.componentInstance;
    component.showGroup(component['groups']()[0]);
    fixture.detectChanges();
    const pre = fixture.nativeElement.querySelector('pre') as HTMLPreElement;
    expect(pre.textContent).toContain(error.exception);
    expect(pre.textContent).toContain(info.message);
    expect(pre.querySelectorAll('.is-focused')).toHaveLength(1);
  });
  it('preserves filters across views and copies and exports only matching events', async () => {
    const fixture = setup();
    const component = fixture.componentInstance;
    stream.next(error);
    component['searchText'].set('InvalidOperationException');
    fixture.detectChanges();
    component.changeView('console');
    fixture.detectChanges();
    expect(component['filteredEntries']().map((e) => e.id)).toEqual(['two']);
    await component.copyLogs();
    expect(clipboard.copy).toHaveBeenCalledWith(expect.stringContaining(error.exception!));
    expect(clipboard.copy.mock.calls[0][0]).not.toContain(info.message);
    component.exportJson();
    component.exportTxt();
    component.exportCsv();
    expect(downloads.save.mock.calls.map((call) => call[1])).toEqual([
      expect.stringMatching(/\.json$/),
      expect.stringMatching(/\.txt$/),
      expect.stringMatching(/\.csv$/),
    ]);
  });
  it('merges late history, ignores SSE replay and bounds the stream', () => {
    const history = new Subject<{ entries: readonly SystemLogEntry[]; totalBuffered: number }>();
    service.getLogs.mockReturnValue(history);
    const fixture = setup();
    stream.next(error);
    history.next({ entries: [info], totalBuffered: 1 });
    stream.next(error);
    expect(fixture.componentInstance['displayedEntries']().map((e) => e.id)).toEqual([
      'one',
      'two',
    ]);
    fixture.componentRef.setInput('maxBufferSize', 1);
    fixture.detectChanges();
    stream.next(error);
    stream.next({ ...info, id: 'latest', timestamp: '2026-10-19T09:00:00Z' });
    expect(fixture.componentInstance['displayedEntries']().map((e) => e.id)).toEqual(['latest']);
    fixture.destroy();
  });
  it('buffers, deduplicates and orders pending events on resume', () => {
    const fixture = setup();
    const component = fixture.componentInstance;
    component.togglePause();
    stream.next({ ...error, id: 'three', timestamp: '2026-10-19T08:00:02Z' });
    stream.next(error);
    stream.next(error);
    expect(component['pendingCount']()).toBe(2);
    expect(component['displayedEntries']()).toHaveLength(1);
    component.togglePause();
    expect(component['displayedEntries']().map((e) => e.id)).toEqual(['one', 'two', 'three']);
    expect(component['pendingCount']()).toBe(0);
  });
  it('reloads history and SSE for a different node and tears down old subscriptions', () => {
    const fixture = setup();
    service.getLogs.mockReturnValue(of({ entries: [], totalBuffered: 0 }));
    fixture.componentRef.setInput('nodeId', 'other');
    fixture.detectChanges();
    expect(service.getLogs).toHaveBeenLastCalledWith(expect.objectContaining({ nodeId: 'other' }));
    expect(service.createLogStream).toHaveBeenLastCalledWith(
      expect.objectContaining({ nodeId: 'other' }),
      expect.any(Function),
    );
    expect(fixture.componentInstance['displayedEntries']()).toHaveLength(0);
    fixture.destroy();
    expect(stream.observed).toBe(false);
  });
  it('clears the local display and never deletes server logs', () => {
    const fixture = setup();
    fixture.componentInstance.clearLogs();
    fixture.detectChanges();
    expect(fixture.componentInstance['filteredEntries']()).toHaveLength(0);
    expect(fixture.nativeElement.textContent).toContain('Aucun événement');
  });
});
