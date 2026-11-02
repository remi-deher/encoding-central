import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { FlowSimulation } from './flow-simulation';

describe('FlowSimulation results', () => {
  it('discards a result received after the graph changed', async () => {
    TestBed.configureTestingModule({
      providers: [FlowSimulation, provideHttpClient(), provideHttpClientTesting()],
    });
    const simulation = TestBed.inject(FlowSimulation);
    simulation.prepare({
      formatVersion: 1,
      id: 'flow',
      version: 1,
      name: 'Flow',
      mediaType: 'video',
      nodes: [],
      connections: [],
    });
    const request = simulation.run(simulation.profile());
    const http = TestBed.inject(HttpTestingController);
    const pending = http.expectOne('/api/v1/flows/simulate');
    simulation.invalidate();
    pending.flush({ status: 'completed', detail: 'Terminé', steps: [] });
    await request;
    expect(simulation.result()).toBeNull();
    expect(simulation.busy()).toBe(false);
    http.verify();
  });
});
