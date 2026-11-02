import { TestBed } from '@angular/core/testing';
import { FlowSimulationStatusComponent } from './flow-simulation-status.component';
import { FlowGraphNode } from '../models/flow-component';
import { SimulationResult, SimulationStep } from '../models/flow-simulation';

const node: FlowGraphNode = {
  id: 'step',
  position: { x: 0, y: 0 },
  configuration: {},
  component: {
    id: 'action',
    name: 'Action',
    description: '',
    kind: 'action',
    mediaType: 'video',
    pluginId: 'base',
    pluginName: 'Base',
    outputs: [],
  },
};
const metadata = { format: {}, streams: [] };
const step: SimulationStep = {
  nodeId: 'step',
  name: 'Action',
  outputId: null,
  detail: 'Diagnostic',
  assumed: false,
  workerNodeId: 'worker',
  context: {
    extension: 'mkv',
    variables: {},
    beforeMetadata: metadata,
    currentMetadata: metadata,
    plan: [],
  },
};

function render(result: SimulationResult | null) {
  const fixture = TestBed.createComponent(FlowSimulationStatusComponent);
  fixture.componentRef.setInput('node', node);
  fixture.componentRef.setInput('result', result);
  fixture.componentRef.setInput('compact', true);
  fixture.detectChanges();
  return fixture;
}

describe('FlowSimulationStatusComponent compact diagnostics', () => {
  it('shows no diagnostic before simulation', () => {
    expect(render(null).nativeElement.querySelector('button')).toBeNull();
  });

  it('distinguishes waiting for a choice from a completed step', () => {
    const waiting = render({
      status: 'waiting',
      detail: 'Choisir une sortie',
      steps: [],
      waitingNodeId: 'step',
    });
    const button = waiting.nativeElement.querySelector('button') as HTMLButtonElement;
    expect(button.getAttribute('aria-label')).toContain('En attente de votre choix');
    expect(button.classList.contains('stopped')).toBe(true);
    const completed = render({ status: 'completed', detail: '', steps: [step] });
    expect(completed.nativeElement.querySelector('button').getAttribute('aria-label')).toContain(
      'Étape parcourue',
    );
    expect(completed.nativeElement.querySelector('button').classList.contains('stopped')).toBe(
      false,
    );
  });

  it('marks assumed steps explicitly and keeps diagnostic activation accessible', () => {
    const fixture = render({
      status: 'completed',
      detail: '',
      steps: [{ ...step, assumed: true }],
    });
    const button = fixture.nativeElement.querySelector('button') as HTMLButtonElement;
    expect(button.classList.contains('assumed')).toBe(true);
    expect(button.getAttribute('aria-label')).toContain('Passage supposé');
    let requested = false;
    fixture.componentInstance.detailsRequested.subscribe(() => (requested = true));
    button.click();
    expect(requested).toBe(true);
  });
});
