import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { of } from 'rxjs';
import { signal } from '@angular/core';
import { AuthService } from '../../../core/auth/auth.service';
import { AiToolsTabComponent } from './ai-tools-tab.component';
import { AiToolsService } from '../../../shared/services/ai-tools.service';
import { NotificationService } from '../../../shared/services/notification.service';
import { AiSettings, AiToolInfo } from '../../../shared/models/ai-tools';

describe('AiToolsTabComponent', () => {
  let aiServiceMock: {
    getModelCatalog: ReturnType<typeof vi.fn>;
    getSettingsModelCatalog: ReturnType<typeof vi.fn>;
    listSessions: ReturnType<typeof vi.fn>;
    getQuotas: ReturnType<typeof vi.fn>;
    getUsage: ReturnType<typeof vi.fn>;
    getCluster: ReturnType<typeof vi.fn>;
    listTools: ReturnType<typeof vi.fn>;
    getSettings: ReturnType<typeof vi.fn>;
    updateSettings: ReturnType<typeof vi.fn>;
    startDeviceAuth: ReturnType<typeof vi.fn>;
    revokeAuth: ReturnType<typeof vi.fn>;
  };
  let notificationServiceMock: { show: ReturnType<typeof vi.fn> };

  const sampleTools: AiToolInfo[] = [
    {
      id: 'codex-cli',
      name: 'Codex CLI',
      description: 'Assistant CLI officiel OpenAI Codex.',
      category: 'Génération & Refactoring',
      isInstalled: true,
      authStatus: {
        toolId: 'codex-cli',
        state: 'authorized',
        accountName: 'test@example.com',
      },
      capabilities: ['flow_generation', 'flow_refactor'],
    },
  ];

  const sampleSettings: AiSettings = {
    defaultProvider: 'ollama',
    ollamaBaseUrl: 'http://localhost:11434',
    ollamaModel: 'llama3:latest',
    openAiBaseUrl: 'https://api.openai.com/v1',
    openAiModel: 'gpt-4o-mini',
    hasOpenAiApiKey: true,
    timeoutSeconds: 120,
    subtitleBatchSize: 50,
    assistantConversationRetentionDays: 90,
    assistantActivityRetentionDays: 30,
  };

  async function setup() {
    aiServiceMock = {
      getModelCatalog: vi.fn().mockReturnValue(of({ nodes: [], policy: { disabledModels: [] } })),
      getSettingsModelCatalog: vi
        .fn()
        .mockReturnValue(of({ nodes: [], policy: { disabledModels: [] } })),
      listSessions: vi.fn().mockReturnValue(of({ items: [], offset: 0, hasMore: false })),
      getQuotas: vi.fn().mockReturnValue(of({ nodes: [] })),
      getUsage: vi.fn().mockReturnValue(
        of({
          days: 30,
          since: '',
          nodes: [],
          truncated: false,
          total: {
            turns: 0,
            inputTokens: 0,
            outputTokens: 0,
            cachedInputTokens: 0,
            turnsWithoutUsage: 0,
          },
        }),
      ),
      getCluster: vi.fn().mockReturnValue(of({ nodes: [] })),
      listTools: vi.fn().mockReturnValue(of(sampleTools)),
      getSettings: vi.fn().mockReturnValue(of(sampleSettings)),
      updateSettings: vi.fn().mockReturnValue(of(sampleSettings)),
      startDeviceAuth: vi.fn(),
      revokeAuth: vi.fn().mockReturnValue(of({ revoked: true })),
    };

    notificationServiceMock = {
      show: vi.fn(),
    };

    await TestBed.configureTestingModule({
      imports: [AiToolsTabComponent],
      providers: [
        { provide: AuthService, useValue: { isAdmin: signal(true) } },
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: AiToolsService, useValue: aiServiceMock },
        { provide: NotificationService, useValue: notificationServiceMock },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(AiToolsTabComponent);
    fixture.detectChanges();
    return { fixture, component: fixture.componentInstance };
  }

  it('charge la configuration et la liste des outils au démarrage', async () => {
    const { component } = await setup();
    expect(aiServiceMock.getSettings).toHaveBeenCalled();
    expect(aiServiceMock.listTools).toHaveBeenCalled();
    expect(component.defaultProvider()).toBe('ollama');
    expect(component.hasOpenAiApiKey()).toBe(true);
    expect(component.tools().length).toBe(1);
  });

  it('sélectionner une fiche ne change pas le moteur par défaut', async () => {
    const { component, fixture } = await setup();
    component.selectedProvider.set('claude-cli');
    fixture.detectChanges();
    expect(component.defaultProvider()).toBe('ollama');
    expect(fixture.nativeElement.textContent).not.toContain('Simuler');
    expect(fixture.nativeElement.querySelectorAll('input[name="ollamaModel"]').length).toBe(0);
  });

  it('préserve les modifications pendant une actualisation des connexions', async () => {
    const { component } = await setup();
    expect(component.hasUnsavedChanges()).toBe(false);
    component.ollamaModel.set('unsaved-model');
    component.openAiApiKey.set('unsaved-private-key');
    component.refreshTools();
    expect(component.hasUnsavedChanges()).toBe(true);
    expect(component.ollamaModel()).toBe('unsaved-model');
    expect(component.openAiApiKey()).toBe('unsaved-private-key');
  });

  it('enregistre les paramètres modifiés via updateSettings', async () => {
    const { component } = await setup();
    component.defaultProvider.set('openai');
    component.openAiModel.set('gpt-4o');
    component.saveSettings();

    expect(aiServiceMock.updateSettings).toHaveBeenCalledWith(
      expect.objectContaining({
        defaultProvider: 'openai',
        openAiModel: 'gpt-4o',
      }),
    );
    expect(notificationServiceMock.show).toHaveBeenCalledWith(
      expect.stringContaining('enregistrée avec succès'),
    );
  });

  it('enregistre les fournisseurs par usage et la politique locale', async () => {
    const { component } = await setup();
    component.assistantProvider.set('ollama');
    component.flowProvider.set('openai');
    component.assistantLocalOnly.set(true);
    component.saveSettings();

    expect(aiServiceMock.updateSettings).toHaveBeenCalledWith(
      expect.objectContaining({
        assistantProvider: 'ollama',
        flowProvider: 'openai',
        assistantLocalOnly: true,
        flowLocalOnly: false,
      }),
    );
  });
});
