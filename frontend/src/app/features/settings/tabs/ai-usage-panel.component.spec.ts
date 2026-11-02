import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { AiUsagePanelComponent } from './ai-usage-panel.component';
import { AiToolsService } from '../../../shared/services/ai-tools.service';
import { AiClusterUsage } from '../../../shared/models/ai-tools';

describe('AiUsagePanelComponent', () => {
  const api = { getUsage: vi.fn() };
  const totals = (input: number, output: number, turns: number, missing = 0) => ({
    turns,
    inputTokens: input,
    outputTokens: output,
    cachedInputTokens: 0,
    costUsd: null,
    turnsWithoutUsage: missing,
  });
  const usage: AiClusterUsage = {
    days: 30,
    since: '2026-09-21T00:00:00Z',
    truncated: false,
    total: totals(150, 25, 4, 1),
    nodes: [
      {
        nodeId: 'a',
        name: 'nas-51',
        total: totals(150, 25, 3),
        providers: [{ provider: 'openai', totals: totals(150, 25, 3) }],
      },
    ],
  };

  beforeEach(() => {
    vi.clearAllMocks();
    TestBed.configureTestingModule({
      imports: [AiUsagePanelComponent],
      providers: [{ provide: AiToolsService, useValue: api }],
    });
  });

  it('affiche le total du cluster, chaque nœud et les échanges sans consommation', () => {
    api.getUsage.mockReturnValue(of(usage));
    const fixture = TestBed.createComponent(AiUsagePanelComponent);
    fixture.detectChanges();
    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Total du cluster');
    expect(text).toContain('nas-51');
    expect(text).toContain('1 échange sans consommation rapportée');
    expect(api.getUsage).toHaveBeenCalledWith(30);
  });

  it('recharge la période choisie et signale une erreur sans afficher de chiffres', () => {
    api.getUsage.mockReturnValueOnce(of(usage)).mockReturnValue(throwError(() => new Error('x')));
    const fixture = TestBed.createComponent(AiUsagePanelComponent);
    fixture.detectChanges();
    fixture.componentInstance.select(7);
    fixture.detectChanges();
    expect(api.getUsage).toHaveBeenLastCalledWith(7);
    expect(fixture.nativeElement.textContent).toContain('Consommation indisponible');
  });
});
