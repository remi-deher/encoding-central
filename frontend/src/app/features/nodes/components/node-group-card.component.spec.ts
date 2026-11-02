import { TestBed } from '@angular/core/testing';
import { NodeGroupCardComponent } from './node-group-card.component';
import { NodeGroupResponse, NodeResponse } from '../../../shared/models/cluster';

describe('NodeGroupCardComponent', () => {
  const dummyGroup: NodeGroupResponse = {
    id: '11111111-1111-1111-1111-111111111111',
    name: 'Transcodeurs-Linux',
    description: 'Workers réservés au transcodage lourd',
    mode: 'automatic',
    criteria: {
      operatingSystem: 'Linux',
      minimumMemoryMb: 16384,
    },
    labels: { gpu: 'nvidia' },
    nodeIds: [],
    createdAt: '2026-10-17T00:00:00Z',
    updatedAt: '2026-10-17T00:00:00Z',
  };

  const dummyNodes: readonly NodeResponse[] = [
    {
      id: '00000000-0000-0000-0000-000000000001',
      name: 'worker-1',
      displayName: 'Node 1',
      roles: ['worker'],
      maxConcurrentTasks: 2,
      status: 'online',
      lastSeenAt: '2026-10-17T00:00:00Z',
      capabilities: [],
      inventory: {
        operatingSystem: 'Linux',
        architecture: 'X64',
        processorCount: 8,
        memoryMb: 32768,
      },
    },
  ];

  function createComponent(group = dummyGroup) {
    const fixture = TestBed.createComponent(NodeGroupCardComponent);
    fixture.componentRef.setInput('group', group);
    fixture.componentRef.setInput('nodes', dummyNodes);
    fixture.componentRef.setInput('isAdmin', true);
    fixture.detectChanges();
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }

  it('renders group title, badge and description', () => {
    const { element } = createComponent();
    expect(element.textContent).toContain('Transcodeurs-Linux');
    expect(element.textContent).toContain('Automatique');
    expect(element.textContent).toContain('Workers réservés au transcodage lourd');
  });

  it('displays resolved member nodes and criteria badges', () => {
    const { element } = createComponent();
    // 1 node matches Linux + 16GB
    expect(element.textContent).toContain('Node 1');
    expect(element.textContent).toContain('OS: Linux');
    expect(element.textContent).toContain('RAM ≥ 16 Go');
    expect(element.textContent).toContain('gpu=nvidia');
  });

  it('emits open when title button is clicked', () => {
    const { fixture, element } = createComponent();
    let opened = false;
    fixture.componentInstance.open.subscribe(() => (opened = true));

    const titleBtn = element.querySelector('.group-title') as HTMLButtonElement;
    titleBtn.click();
    expect(opened).toBe(true);
  });
});
