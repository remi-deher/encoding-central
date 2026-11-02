export const BADGE_SEPARATOR = '|';

interface MediaStreamLike {
  readonly codec_type?: string;
  readonly codec_name?: string;
  readonly width?: number | string;
  readonly height?: number | string;
  readonly channels?: number | string;
}

export interface MediaMetadataLike {
  readonly streams?: readonly unknown[];
}

const STANDARD_HEIGHTS = [
  [2160, '2160p'],
  [1440, '1440p'],
  [1080, '1080p'],
  [720, '720p'],
  [576, '576p'],
  [480, '480p'],
] as const;

function channelLabel(channels: number): string {
  if (channels === 1) return 'Mono';
  if (channels === 2) return 'Stereo';
  if (channels === 6) return '5.1';
  if (channels === 8) return '7.1';
  return `${channels} canaux`;
}

function heightLabel(height: number): string {
  const standard = STANDARD_HEIGHTS.find(([minimum]) => height >= minimum);
  return standard ? standard[1] : `${height}p`;
}

/** Résume les pistes principales d’un scan média en pastilles : codec vidéo, codec audio, canaux, résolution. */
export function mediaBadges(metadata: MediaMetadataLike | null | undefined): string[] {
  const streams = (metadata?.streams ?? []) as readonly MediaStreamLike[];
  const video = streams.find((stream) => stream?.codec_type === 'video');
  const audio = streams.find((stream) => stream?.codec_type === 'audio');
  const badges: string[] = [];
  if (video?.codec_name) badges.push(video.codec_name.toUpperCase());
  if (audio?.codec_name) badges.push(audio.codec_name.toUpperCase());
  const channels = Number(audio?.channels);
  if (Number.isFinite(channels) && channels > 0) badges.push(channelLabel(channels));
  const height = Number(video?.height);
  if (Number.isFinite(height) && height > 0) badges.push(heightLabel(height));
  return badges;
}

export function joinBadges(badges: readonly string[]): string {
  return badges.join(BADGE_SEPARATOR);
}

export function splitBadges(value: unknown): string[] {
  return typeof value === 'string' && value ? value.split(BADGE_SEPARATOR).filter(Boolean) : [];
}
