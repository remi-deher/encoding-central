import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { signal } from '@angular/core';
import { AiModelCatalogComponent } from './ai-model-catalog.component';
import { AiToolsService } from '../../../shared/services/ai-tools.service';
import { AuthService } from '../../../core/auth/auth.service';
import { AiClusterModelCatalog } from '../../../shared/models/ai-tools';

const model = (id: string, label: string) => ({
  id,
  label,
  reasoningEfforts: ['low', 'high'],
  isDefault: false,
  reasoningKnown: true,
});
const catalog: AiClusterModelCatalog = {
  nodes: [
    {
      nodeId: 'a',
      name: 'Salon',
      providers: [
        {
          provider: 'claude-cli',
          state: 'available',
          models: [model('claude-opus-5-5', 'Opus · claude-opus-5-5'), model('haiku', 'Haiku')],
          observedAt: null,
          checkedAt: '2030-01-01T00:00:00Z',
        },
      ],
    },
    {
      nodeId: 'b',
      name: 'Bureau',
      providers: [
        {
          provider: 'claude-cli',
          state: 'available',
          models: [model('claude-opus-5-5', 'Opus · claude-opus-5-5')],
          observedAt: null,
          checkedAt: '2030-01-01T00:00:00Z',
        },
      ],
    },
  ],
  policy: { disabledModels: [{ provider: 'claude-cli', model: 'haiku' }] },
};

describe('AiModelCatalogComponent', () => {
  const updateModelPolicy = vi.fn().mockReturnValue(of({ disabledModels: [] }));
  async function setup() {
    await TestBed.configureTestingModule({
      imports: [AiModelCatalogComponent],
      providers: [
        {
          provide: AiToolsService,
          useValue: { getModelCatalog: () => of(catalog), updateModelPolicy },
        },
        { provide: AuthService, useValue: { isAdmin: signal(true) } },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(AiModelCatalogComponent);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('groups models by provider with one switch per model and the nodes that lack it', async () => {
    const element = await setup();
    expect(element.querySelectorAll('section.provider')).toHaveLength(1);
    expect(element.textContent).toContain('2 nœuds sur 2');
    expect(element.querySelectorAll('mat-slide-toggle')).toHaveLength(2);
    expect(element.textContent).toContain('seulement Salon');
    expect(element.textContent).toContain('claude-opus-5-5');
  });

  it('enables every model of a provider in one action', async () => {
    const element = await setup();
    const button = [...element.querySelectorAll('button')].find(
      (item) => item.textContent?.trim() === 'Tout activer',
    ) as HTMLButtonElement;
    button.click();
    expect(updateModelPolicy).toHaveBeenCalledOnce();
    expect(updateModelPolicy).toHaveBeenCalledWith('claude-cli', 'haiku', true);
  });
});
