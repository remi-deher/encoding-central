import { FlowComponentConfiguration, FlowComponentConfigurationSchema } from './flow-component';
import { FlowDocument } from './flow-validation';

export interface SimulationMetadata {
  readonly format: Record<string, unknown>;
  readonly streams: readonly Record<string, unknown>[];
}
export interface SimulationFile {
  readonly name: string;
  readonly metadata: SimulationMetadata;
  readonly variables: Record<string, unknown>;
  readonly sourceNodeId: string;
  readonly workerNodeId: string;
  readonly libraryId?: string;
}
export interface SimulationAssumption {
  readonly outputId: string;
  readonly metadata?: SimulationMetadata;
  readonly variables?: Record<string, unknown>;
  readonly modified: boolean;
  readonly extension?: string;
  readonly workerNodeId?: string;
}
export interface SimulationRequest {
  readonly definition: FlowDocument;
  readonly file: SimulationFile;
  readonly assumptions: Readonly<Record<string, SimulationAssumption>>;
}
export interface SimulationStep {
  readonly nodeId: string;
  readonly name: string;
  readonly outputId: string | null;
  readonly detail: string;
  readonly assumed: boolean;
  readonly workerNodeId: string;
  readonly conditions?: readonly {
    readonly rule: {
      field: string;
      operator: string;
      value?: unknown;
      scope: string;
      variableName?: string;
    };
    readonly actual: unknown;
    readonly result: boolean | null;
    readonly error?: string;
  }[];
  readonly context: {
    readonly extension: string;
    readonly variables: Record<string, unknown>;
    readonly beforeMetadata: SimulationMetadata;
    readonly currentMetadata: SimulationMetadata;
    readonly plan: readonly unknown[];
  };
}
export interface SimulationResult {
  readonly status: 'invalid' | 'waiting' | 'failed' | 'completed';
  readonly detail: string;
  readonly steps: readonly SimulationStep[];
  readonly waitingNodeId?: string;
  readonly terminalResult?: string;
  readonly issues?: readonly { path: string; message: string }[];
}

export const SIMULATION_PROFILE_SCHEMA: FlowComponentConfigurationSchema = {
  version: 1,
  fields: [
    { id: 'name', type: 'text', label: 'Nom / chemin fictif du fichier', required: true },
    { id: 'size', type: 'number', label: 'Taille (octets)', min: 0, step: 1 },
    { id: 'duration', type: 'number', label: 'Durée (secondes)', min: 0 },
    {
      id: 'videoCodec',
      type: 'text',
      label: 'Codec vidéo / image',
      description: 'hevc, h264, av1… Laissez vide en l’absence de piste vidéo.',
    },
    { id: 'width', type: 'number', label: 'Largeur (pixels)', min: 1, step: 1 },
    { id: 'height', type: 'number', label: 'Hauteur (pixels)', min: 1, step: 1 },
    {
      id: 'audioCount',
      type: 'number',
      label: 'Nombre de pistes audio',
      min: 0,
      max: 100,
      step: 1,
      required: true,
    },
    {
      id: 'audioCodecs',
      type: 'text',
      label: 'Codecs audio, dans l’ordre des pistes',
      description: 'Séparés par des virgules : aac, ac3',
    },
    {
      id: 'audioLanguages',
      type: 'text',
      label: 'Langues audio, dans l’ordre des pistes',
      description:
        'Codes utilisés par les conditions : fra, eng. Une position vide représente une langue absente.',
    },
    {
      id: 'audioChannels',
      type: 'number',
      label: 'Canaux de la première piste audio',
      min: 1,
      step: 1,
    },
    {
      id: 'subtitleCount',
      type: 'number',
      label: 'Nombre de pistes de sous-titres',
      min: 0,
      max: 100,
      step: 1,
      required: true,
    },
    {
      id: 'subtitleLanguages',
      type: 'text',
      label: 'Langues des sous-titres, dans l’ordre des pistes',
      description: 'Séparées par des virgules : fra, eng',
    },
    { id: 'sourceNodeId', type: 'text', label: 'Identifiant du nœud d’origine', required: true },
    { id: 'workerNodeId', type: 'text', label: 'Identifiant du worker courant', required: true },
    { id: 'libraryId', type: 'text', label: 'Identifiant de la bibliothèque (facultatif)' },
    {
      id: 'variables',
      type: 'text',
      label: 'Variables initiales (objet JSON)',
      multiline: true,
      description: 'Exemple : {"priority": 2, "ready": true}',
    },
  ],
};

function text(values: FlowComponentConfiguration, id: string): string {
  return String(values[id] ?? '').trim();
}
function number(values: FlowComponentConfiguration, id: string): number | undefined {
  if (values[id] == null || values[id] === '') return undefined;
  const value = Number(values[id]);
  if (!Number.isFinite(value) || value < 0) throw new Error(`Nombre invalide : ${id}.`);
  return value;
}
export function simulationObject(value: string): Record<string, unknown> {
  const parsed: unknown = JSON.parse(value || '{}');
  if (parsed === null || Array.isArray(parsed) || typeof parsed !== 'object')
    throw new Error('Un objet JSON est attendu.');
  return parsed as Record<string, unknown>;
}
export function simulationFile(values: FlowComponentConfiguration): SimulationFile {
  const format: Record<string, unknown> = {};
  for (const id of ['size', 'duration']) {
    const value = number(values, id);
    if (value !== undefined) format[id] = value;
  }
  const streams: Record<string, unknown>[] = [];
  if (text(values, 'videoCodec')) {
    const video: Record<string, unknown> = {
      codec_type: 'video',
      codec_name: text(values, 'videoCodec'),
    };
    for (const id of ['width', 'height']) {
      const value = number(values, id);
      if (value !== undefined) video[id] = value;
    }
    streams.push(video);
  }
  for (const type of ['audio', 'subtitle']) {
    const count = number(values, type === 'audio' ? 'audioCount' : 'subtitleCount') ?? 0;
    if (!Number.isInteger(count) || count > 100) throw new Error('Maximum : 100 pistes par type.');
    const languages = text(values, type === 'audio' ? 'audioLanguages' : 'subtitleLanguages').split(
      ',',
    );
    const codecs = text(values, 'audioCodecs').split(',');
    for (let index = 0; index < count; index++) {
      const stream: Record<string, unknown> = { codec_type: type };
      if (languages[index]?.trim()) stream['tags'] = { language: languages[index].trim() };
      if (type === 'audio') {
        if (codecs[index]?.trim()) stream['codec_name'] = codecs[index].trim();
        const channels = number(values, 'audioChannels');
        if (index === 0 && channels !== undefined) stream['channels'] = channels;
      }
      streams.push(stream);
    }
  }
  return {
    name: text(values, 'name'),
    metadata: { format, streams },
    variables: simulationObject(text(values, 'variables')),
    sourceNodeId: text(values, 'sourceNodeId'),
    workerNodeId: text(values, 'workerNodeId'),
    ...(text(values, 'libraryId') ? { libraryId: text(values, 'libraryId') } : {}),
  };
}
export function simulationProfile(file: SimulationFile): FlowComponentConfiguration {
  const video = file.metadata.streams.find((stream) => stream['codec_type'] === 'video');
  const audio = file.metadata.streams.filter((stream) => stream['codec_type'] === 'audio');
  const subtitles = file.metadata.streams.filter((stream) => stream['codec_type'] === 'subtitle');
  const languages = (tracks: readonly Record<string, unknown>[]) =>
    tracks
      .map((track) => (track['tags'] as { language?: string } | undefined)?.language ?? '')
      .join(', ');
  return {
    name: file.name,
    size: file.metadata.format['size'] == null ? null : Number(file.metadata.format['size']),
    duration:
      file.metadata.format['duration'] == null ? null : Number(file.metadata.format['duration']),
    videoCodec: String(video?.['codec_name'] ?? ''),
    width: Number(video?.['width']) || null,
    height: Number(video?.['height']) || null,
    audioCount: audio.length,
    audioCodecs: audio.map((track) => String(track['codec_name'] ?? '')).join(', '),
    audioLanguages: languages(audio),
    audioChannels: Number(audio[0]?.['channels']) || null,
    subtitleCount: subtitles.length,
    subtitleLanguages: languages(subtitles),
    sourceNodeId: file.sourceNodeId,
    workerNodeId: file.workerNodeId,
    libraryId: file.libraryId ?? '',
    variables: JSON.stringify(file.variables, null, 2),
  };
}
