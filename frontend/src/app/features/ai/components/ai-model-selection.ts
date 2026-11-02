import { AiAssistantModelOption } from '../../../shared/models/ai-tools';

export function aiModelKey(
  option: Pick<AiAssistantModelOption, 'provider' | 'model' | 'nodeId'>,
): string {
  return `${option.provider}\n${option.model ?? ''}\n${option.nodeId ?? ''}`;
}

export function aiTypeLabel(provider: string): string {
  return (
    (
      {
        'codex-cli': 'ChatGPT',
        'claude-cli': 'Claude',
        openai: 'OpenAI · API',
        anthropic: 'Claude · API',
        ollama: 'Ollama',
      } as Record<string, string>
    )[provider] ?? provider
  );
}

export function aiEffortLabel(effort: string): string {
  return (
    (
      {
        none: 'Aucun',
        minimal: 'Minimal',
        low: 'Faible',
        medium: 'Moyen',
        high: 'Élevé',
        xhigh: 'Très élevé',
        max: 'Maximum',
      } as Record<string, string>
    )[effort] ?? effort
  );
}
