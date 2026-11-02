import type { FlowComponentDefinition, FlowComponentOutput } from './flow-component';

const MEDIA = {
  video: { color: 'var(--flow-video)', background: 'var(--flow-video-soft)', icon: 'video' },
  image: { color: 'var(--flow-video)', background: 'var(--flow-video-soft)', icon: 'image' },
  audio: { color: 'var(--flow-audio)', background: 'var(--flow-audio-soft)', icon: 'music' },
  subtitles: { color: 'var(--warning)', background: 'var(--warning-soft)', icon: 'subtitles' },
  general: {
    color: 'var(--text-muted)',
    background: 'var(--surface-container-high)',
    icon: 'settings',
  },
} as const;

export function flowComponentColors(component: FlowComponentDefinition) {
  const media =
    component.kind === 'condition'
      ? { color: 'var(--success)', background: 'var(--success-soft)', icon: 'condition' as const }
      : MEDIA[component.mediaType];
  return {
    ...media,
    plugin: media.color,
    icon:
      component.kind === 'pack'
        ? ('pack' as const)
        : component.kind === 'script'
          ? ('terminal' as const)
          : component.kind === 'input'
            ? ('upload' as const)
            : media.icon,
  };
}

const OUTPUT_PRESENTATIONS = {
  yes: { icon: 'check', tone: 'success' },
  true: { icon: 'check', tone: 'success' },
  oui: { icon: 'check', tone: 'success' },
  vrai: { icon: 'check', tone: 'success' },
  success: { icon: 'check', tone: 'success' },
  succes: { icon: 'check', tone: 'success' },
  conforme: { icon: 'check', tone: 'success' },
  no: { icon: 'close', tone: 'warning' },
  false: { icon: 'close', tone: 'warning' },
  non: { icon: 'close', tone: 'warning' },
  faux: { icon: 'close', tone: 'warning' },
  'non-conforme': { icon: 'close', tone: 'warning' },
  error: { icon: 'warning', tone: 'danger' },
  erreur: { icon: 'warning', tone: 'danger' },
  failure: { icon: 'warning', tone: 'danger' },
  failed: { icon: 'warning', tone: 'danger' },
  next: { icon: 'forward', tone: 'neutral' },
  suite: { icon: 'forward', tone: 'neutral' },
  continue: { icon: 'forward', tone: 'neutral' },
  continuer: { icon: 'forward', tone: 'neutral' },
  skipped: { icon: 'skip', tone: 'neutral' },
  ignored: { icon: 'skip', tone: 'neutral' },
  ignore: { icon: 'skip', tone: 'neutral' },
  'non-applicable': { icon: 'skip', tone: 'neutral' },
  waiting: { icon: 'clock', tone: 'warning' },
  pending: { icon: 'clock', tone: 'warning' },
  deferred: { icon: 'clock', tone: 'warning' },
  'en-attente': { icon: 'clock', tone: 'warning' },
  differe: { icon: 'clock', tone: 'warning' },
  video: { icon: 'video', tone: 'video' },
  audio: { icon: 'music', tone: 'audio' },
  image: { icon: 'image', tone: 'video' },
  subtitles: { icon: 'subtitles', tone: 'warning' },
  'sous-titres': { icon: 'subtitles', tone: 'warning' },
} as const;

/** Only exact, known identifiers/labels have semantics; third-party outputs keep their number. */
export function flowOutputPresentation(output: FlowComponentOutput) {
  const normalize = (value: string) =>
    value
      .normalize('NFD')
      .replace(/[\u0300-\u036f]/g, '')
      .trim()
      .toLowerCase()
      .replace(/[ _]+/g, '-');
  const lookup = (value: string) => {
    const key = normalize(value);
    return Object.hasOwn(OUTPUT_PRESENTATIONS, key)
      ? OUTPUT_PRESENTATIONS[key as keyof typeof OUTPUT_PRESENTATIONS]
      : undefined;
  };
  return lookup(output.id) ?? lookup(output.label) ?? { icon: null, tone: 'neutral' as const };
}
