import { AiAssistantActivity } from '../../shared/models/ai-tools';
import { ExecutionResponse } from '../../shared/models/execution';
import { aiItem, aiOperationName, executionGroup, executionItem } from './activity-items';

const execution = (state: ExecutionResponse['state']): ExecutionResponse => ({
  id: 'e1',
  media: 'Film.mkv',
  library: 'Films',
  flow: 'HEVC',
  flowVersion: 2,
  state,
  nodeId: 'n1',
  node: 'nœud-1',
  progress: 40,
  detail: 'Encodage',
  attempt: 1,
  revision: 1,
  queuedAt: '2026-01-01T10:00:00Z',
  startedAt: '2026-01-01T10:01:00Z',
  finishedAt: null,
  updatedAt: '2026-01-01T10:02:00Z',
});

const ai = (status: AiAssistantActivity['status']): AiAssistantActivity => ({
  requestId: 'r1',
  conversationId: null,
  provider: 'claude-cli',
  status,
  startedAt: '2026-01-01T10:00:00Z',
  updatedAt: '2026-01-01T10:00:05Z',
  durationMilliseconds: 5000,
  errorCategory: null,
});

describe('activity items', () => {
  it('shows the file name with library context and keeps the complete path in execution details', () => {
    const item = executionItem({
      ...execution('succeeded'),
      media: 'Séries/Saison 01/Épisode.mkv',
    });
    expect(item.title).toBe('Épisode.mkv');
    expect(item.subtitle).toContain('Films');
    expect(item.execution?.media).toBe('Séries/Saison 01/Épisode.mkv');
  });
  it('groups executions by state', () => {
    expect(executionGroup('queued')).toBe('queue');
    expect(executionGroup('cancelling')).toBe('active');
    expect(executionGroup('failed')).toBe('errors');
    expect(executionGroup('deferred')).toBe('done');
  });

  it('shows progress only while running', () => {
    expect(executionItem(execution('running')).progress).toBe(40);
    expect(executionItem(execution('queued')).progress).toBeNull();
  });

  it('maps AI requests into the same groups', () => {
    expect(aiItem(ai('running')).group).toBe('active');
    expect(aiItem(ai('failed')).group).toBe('errors');
    expect(aiItem(ai('proposal-ready')).group).toBe('done');
    expect(aiItem(ai('succeeded')).key).toBe('ai:r1');
  });

  it('titles an AI request with its conversation name, with a fallback to the operation', () => {
    const named = aiItem({ ...ai('succeeded'), conversationTitle: ' Diagnostic du flow HEVC ' });
    expect(named.title).toBe('Diagnostic du flow HEVC');
    expect(named.subtitle).toContain('Assistant');
    expect(aiItem(ai('succeeded')).title).toBe(aiOperationName(undefined));
    expect(aiItem({ ...ai('succeeded'), conversationTitle: '  ' }).title).toBe(
      aiOperationName(undefined),
    );
  });
});
