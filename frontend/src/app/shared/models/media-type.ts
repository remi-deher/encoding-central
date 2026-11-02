export type MediaType = 'video' | 'image' | 'music' | 'subtitles';

export interface MediaTypeOption {
  readonly value: MediaType;
  readonly label: string;
}

export const MEDIA_TYPE_OPTIONS: readonly MediaTypeOption[] = [
  { value: 'video', label: 'Vidéo' },
  { value: 'image', label: 'Image' },
  { value: 'music', label: 'Musique' },
  { value: 'subtitles', label: 'Sous-titres' },
];

/** Icône de chaque type de média (jeu d’icônes partagé). */
export const MEDIA_TYPE_ICONS = {
  video: 'video',
  image: 'image',
  music: 'music',
  subtitles: 'subtitles',
} as const satisfies Record<MediaType, string>;
