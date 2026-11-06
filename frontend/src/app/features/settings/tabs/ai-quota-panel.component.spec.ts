import { TestBed } from '@angular/core/testing';
import { of, Subject, throwError } from 'rxjs';
import { AiQuotaPanelComponent } from './ai-quota-panel.component';
import { AiToolsService } from '../../../shared/services/ai-tools.service';
import { AiCentralQuotas } from '../../../shared/models/ai-tools';

describe('AiQuotaPanelComponent', () => {
  const api = { getQuotas: vi.fn() };
  const view = (): AiCentralQuotas => ({
    workers: [
      {
        workerId: 'node',
        name: 'Salon',
        providers: [
          {
            provider: 'claude-cli',
            state: 'available',
            experimental: true,
            observedAt: new Date().toISOString(),
            checkedAt: new Date().toISOString(),
            windows: [
              {
                id: 'five_hour',
                name: '5 heures',
                usedPercent: 85,
                remainingPercent: 15,
                resetsAt: new Date(Date.now() + 3600_000).toISOString(),
              },
            ],
          },
        ],
      },
    ],
  });

  beforeEach(async () => {
    vi.clearAllMocks();
    api.getQuotas.mockReturnValue(of(view()));
    await TestBed.configureTestingModule({
      imports: [AiQuotaPanelComponent],
      providers: [{ provide: AiToolsService, useValue: api }],
    }).compileComponents();
  });

  it('affiche le quota restant, son échéance et l’alerte sans créer de budget en jetons', () => {
    const fixture = TestBed.createComponent(AiQuotaPanelComponent);
    fixture.detectChanges();
    const text = fixture.nativeElement.textContent;
    expect(text).toContain('15 % restant');
    expect(text).toContain('Quota bientôt atteint');
    expect(text).toContain('Lecture expérimentale');
    expect(text).toContain('Réinitialisation');
    expect(api.getQuotas).toHaveBeenCalledTimes(1);
  });

  it('affiche une barre de progression pendant le chargement, sans état vide', () => {
    api.getQuotas.mockReturnValueOnce(new Subject<AiCentralQuotas>());
    const fixture = TestBed.createComponent(AiQuotaPanelComponent);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('mat-progress-bar')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('app-empty-state')).toBeNull();
  });

  it('conserve les dernières observations sans jauge actuelle si la lecture échoue', () => {
    const fixture = TestBed.createComponent(AiQuotaPanelComponent);
    fixture.detectChanges();
    api.getQuotas.mockReturnValue(throwError(() => new Error('offline')));
    fixture.componentInstance.refresh();
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Donnée ancienne');
    expect(fixture.nativeElement.textContent).toContain(
      '15 % restant lors de la dernière observation',
    );
    expect(fixture.nativeElement.querySelector('mat-progress-bar')).toBeNull();
  });

  it('marque une fenêtre expirée comme ancienne sans présumer une réinitialisation à 100 %', () => {
    const data = view();
    api.getQuotas.mockReturnValue(
      of({
        workers: [
          {
            ...data.workers[0],
            providers: [
              {
                ...data.workers[0].providers[0],
                windows: [
                  { ...data.workers[0].providers[0].windows[0], resetsAt: '2020-01-01T00:00:00Z' },
                ],
              },
            ],
          },
        ],
      }),
    );
    const fixture = TestBed.createComponent(AiQuotaPanelComponent);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Donnée ancienne');
    expect(fixture.nativeElement.querySelector('mat-progress-bar')).toBeNull();
  });

  it('ignore une réponse en vol après un changement de connexion', () => {
    const pending = new Subject<AiCentralQuotas>();
    api.getQuotas.mockReturnValueOnce(pending).mockReturnValue(of({ workers: [] }));
    const fixture = TestBed.createComponent(AiQuotaPanelComponent);
    fixture.detectChanges();
    fixture.componentRef.setInput('refreshKey', 1);
    fixture.detectChanges();
    pending.next(view());
    fixture.detectChanges();
    expect(fixture.componentInstance.nodes()).toEqual([]);
    expect(api.getQuotas).toHaveBeenCalledTimes(2);
  });
});
