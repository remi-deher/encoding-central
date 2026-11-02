import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { AiSessionSummary } from '../../../shared/models/ai-tools';
import { AiSessionHistoryComponent } from './ai-session-history.component';
import { AiToolsService } from '../../../shared/services/ai-tools.service';
import { NotificationService } from '../../../shared/services/notification.service';

describe('AiSessionHistoryComponent', () => {
  const session: AiSessionSummary = {
    id: 'application-session',
    provider: 'codex-cli',
    status: 'completed',
    turnCount: 1,
    attempt: 1,
    scope: { operation: 'flow-create' },
    usage: { inputTokens: 123, outputTokens: 12 },
  };
  const api = { listSessions: vi.fn(), getSession: vi.fn() };
  const notifications = { show: vi.fn() };

  beforeEach(async () => {
    vi.clearAllMocks();
    api.listSessions.mockReturnValue(of({ items: [session], offset: 0, hasMore: false }));
    api.getSession.mockReturnValue(
      of({
        id: session.id,
        systemPrompt: 'Consignes',
        turns: [
          { requestId: 'request', userPrompt: '<script>private</script>', content: 'Réponse' },
        ],
      }),
    );
    await TestBed.configureTestingModule({
      imports: [AiSessionHistoryComponent],
      providers: [
        { provide: AiToolsService, useValue: api },
        { provide: NotificationService, useValue: notifications },
      ],
    }).compileComponents();
  });

  it('charge les résumés sans charger les échanges et indique les coûts absents', () => {
    const fixture = TestBed.createComponent(AiSessionHistoryComponent);
    fixture.componentInstance.refresh();
    fixture.detectChanges();
    const text = fixture.nativeElement.textContent;
    expect(text).toContain('123');
    expect(text).toContain('Création de flow');
    expect(text).not.toContain('Coût estimé');
    expect(text).not.toContain('private');
    expect(api.getSession).not.toHaveBeenCalled();
    expect(fixture.nativeElement.querySelector('nav button:last-child').disabled).toBe(true);
  });

  it('consulte et ferme un échange sans interpréter le texte comme du HTML', () => {
    const fixture = TestBed.createComponent(AiSessionHistoryComponent);
    fixture.componentInstance.refresh();
    fixture.componentInstance.inspect(session);
    fixture.detectChanges();
    expect(api.getSession).toHaveBeenCalledWith(session.id);
    expect(fixture.nativeElement.textContent).toContain('<script>private</script>');
    expect(fixture.nativeElement.querySelector('script')).toBeNull();
    fixture.componentInstance.inspect(session);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).not.toContain('private');
  });

  it('garde les résultats précédents si une actualisation échoue', () => {
    const fixture = TestBed.createComponent(AiSessionHistoryComponent);
    fixture.componentInstance.refresh();
    api.listSessions.mockReturnValue(throwError(() => new Error('offline')));
    fixture.componentInstance.refresh();
    expect(fixture.componentInstance.page()?.items.length).toBe(1);
    expect(fixture.componentInstance.loading()).toBe(false);
    expect(notifications.show).toHaveBeenCalled();
  });
});
