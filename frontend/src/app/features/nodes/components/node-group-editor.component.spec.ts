import { TestBed } from '@angular/core/testing';
import { NodeGroupEditorComponent, SaveNodeGroupPayload } from './node-group-editor.component';
import { NodeResponse } from '../../../shared/models/cluster';

describe('NodeGroupEditorComponent', () => {
  const dummyNodes: readonly NodeResponse[] = [
    {
      id: '00000000-0000-0000-0000-000000000001',
      name: 'worker-linux',
      displayName: 'Fast Worker Linux',
      roles: ['worker'],
      maxConcurrentTasks: 4,
      status: 'online',
      lastSeenAt: '2026-10-17T00:00:00Z',
      capabilities: [
        { toolId: 'ffmpeg', version: '7.0', mediaTypes: ['video'], accelerationTypes: [] },
      ],
      inventory: {
        operatingSystem: 'Linux',
        architecture: 'X64',
        processorCount: 16,
        memoryMb: 32768,
      },
    },
    {
      id: '00000000-0000-0000-0000-000000000002',
      name: 'server-win',
      displayName: 'Main Server',
      roles: ['server'],
      maxConcurrentTasks: 1,
      status: 'online',
      lastSeenAt: '2026-10-17T00:00:00Z',
      capabilities: [],
      inventory: {
        operatingSystem: 'Windows',
        architecture: 'X64',
        processorCount: 4,
        memoryMb: 8192,
      },
    },
  ];

  function createComponent() {
    const fixture = TestBed.createComponent(NodeGroupEditorComponent);
    fixture.componentRef.setInput('nodes', dummyNodes);
    fixture.detectChanges();
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }

  it('renders mode switch and initial inputs', () => {
    const { element } = createComponent();
    expect(element.textContent).toContain('Type de groupe');
    expect(element.textContent).toContain('Groupe manuel');
    expect(element.textContent).toContain('Groupe automatique');
    expect(element.querySelector('input[name="groupName"]')).toBeTruthy();
  });

  it('allows selecting manual nodes and emits payload on save', () => {
    const { fixture, element } = createComponent();
    fixture.componentInstance['name'].set('Pool-Workers');
    fixture.detectChanges();

    // In manual mode, dummyNodes are listed
    const checkboxes = element.querySelectorAll('mat-checkbox');
    expect(checkboxes.length).toBe(2);

    // Click on the first checkbox (worker-linux)
    const firstCheckInput = checkboxes[0].querySelector('input') as HTMLInputElement;
    firstCheckInput.click();
    fixture.detectChanges();

    let emittedPayload: SaveNodeGroupPayload | undefined;
    fixture.componentInstance.saved.subscribe((payload) => (emittedPayload = payload));

    const submitBtn = element.querySelector('button[type="submit"]') as HTMLButtonElement;
    expect(submitBtn.disabled).toBe(false);
    submitBtn.click();
    fixture.detectChanges();

    expect(emittedPayload).toBeDefined();
    expect(emittedPayload!.name).toBe('Pool-Workers');
    expect(emittedPayload!.mode).toBe('manual');
    expect(emittedPayload!.nodeIds).toEqual(['00000000-0000-0000-0000-000000000001']);
    expect(emittedPayload!.criteria).toEqual({});
  });

  it('evaluates live matching in automatic mode', () => {
    const { fixture, element } = createComponent();
    // Switch to automatic mode
    const autoBtn = element.querySelectorAll('.mode-card')[1] as HTMLButtonElement;
    autoBtn.click();
    fixture.detectChanges();

    expect(element.textContent).toContain('Critères d’attribution automatique');
    expect(element.textContent).toContain('Aperçu des nœuds correspondants');

    // Tool chip selection
    const toolChip = element.querySelector('.tool-chip') as HTMLButtonElement;
    expect(toolChip).toBeTruthy();
    expect(toolChip.textContent!.trim()).toBe('ffmpeg');
    toolChip.click();
    fixture.detectChanges();

    // Live preview should show 1 matching node (worker-linux has ffmpeg)
    expect(element.textContent).toContain('1 nœud éligible');
    expect(element.textContent).toContain('Fast Worker Linux');
  });

  it('adds and removes labels with format validation', () => {
    const { fixture, element } = createComponent();
    const addBtn = element.querySelector('.add-label-btn') as HTMLButtonElement;

    // Invalid key test
    fixture.componentInstance['newLabelKey'].set('invalid key with spaces');
    fixture.detectChanges();
    addBtn.click();
    fixture.detectChanges();
    expect(element.textContent).toContain('Format de clé invalide');

    // Valid key test
    fixture.componentInstance['newLabelKey'].set('gpu');
    fixture.componentInstance['newLabelValue'].set('nvidia');
    fixture.detectChanges();
    addBtn.click();
    fixture.detectChanges();

    expect(element.querySelectorAll('.label-chip').length).toBe(1);
    expect(element.textContent).toContain('gpu');
    expect(element.textContent).toContain('nvidia');

    // Remove label
    const removeBtn = element.querySelector('.remove-chip-btn') as HTMLButtonElement;
    removeBtn.click();
    fixture.detectChanges();
    expect(element.querySelectorAll('.label-chip').length).toBe(0);
  });
});
