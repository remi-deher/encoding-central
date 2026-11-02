import { TestBed } from '@angular/core/testing';
import { FlowEditorAssistant, FlowAssistantSession } from './flow-editor-assistant.service';
import { FlowDocument } from '../models/flow-validation';
describe('FlowEditorAssistant', () => {
  const flow: FlowDocument = {
    formatVersion: 1,
    id: 'flow',
    version: 1,
    name: 'Flow',
    mediaType: 'video',
    nodes: [],
    connections: [],
  };
  const session = (apply = vi.fn(async () => true)): FlowAssistantSession => ({
    label: 'Flow complet',
    flow,
    components: [],
    prompt: '',
    apply,
  });
  it('prepares contextual suggestions without applying them and retains refused stale proposals', async () => {
    const service = TestBed.inject(FlowEditorAssistant),
      proposal = session(vi.fn(async () => false)),
      prepare = vi.fn(async () => proposal);
    service.register({}, prepare);
    await service.open('selected-node');
    expect(prepare).toHaveBeenCalledWith('selected-node');
    expect(proposal.apply).not.toHaveBeenCalled();
    expect(await service.apply(flow)).toBe(false);
    expect(service.session()).toBe(proposal);
  });
  it('only closes a proposal after an explicitly successful application', async () => {
    const service = TestBed.inject(FlowEditorAssistant),
      proposal = session();
    service.register({}, async () => proposal);
    await service.open();
    expect(await service.apply(flow)).toBe(true);
    expect(proposal.apply).toHaveBeenCalledWith(flow);
    expect(service.session()).toBeNull();
  });
  it('does not mount an assistant for an editor destroyed while its context was loading', async () => {
    const service = TestBed.inject(FlowEditorAssistant),
      owner = {};
    let resolve!: (value: FlowAssistantSession) => void;
    service.register(owner, () => new Promise((r) => (resolve = r)));
    const opening = service.open();
    service.unregister(owner);
    resolve(session());
    await opening;
    expect(service.session()).toBeNull();
    expect(service.hasEditor()).toBe(false);
  });
});
