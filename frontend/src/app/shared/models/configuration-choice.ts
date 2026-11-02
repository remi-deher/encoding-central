export interface ConfigurationChoice {
  readonly value: string;
  readonly label: string;
}

/** Preserve original codes and unknown references: suggestions are never migrations. */
export function configurationTokens(value: string): readonly string[] {
  return [
    ...new Set(
      value
        .split(',')
        .map((item) => item.trim())
        .filter(Boolean),
    ),
  ];
}

export const LANGUAGE_CHOICES: readonly ConfigurationChoice[] = [
  { value: 'fra', label: 'Français (fra)' },
  { value: 'fre', label: 'Français — code historique (fre)' },
  { value: 'eng', label: 'Anglais (eng)' },
  { value: 'jpn', label: 'Japonais (jpn)' },
  { value: 'deu', label: 'Allemand (deu)' },
  { value: 'spa', label: 'Espagnol (spa)' },
  { value: 'ita', label: 'Italien (ita)' },
  { value: 'por', label: 'Portugais (por)' },
  { value: 'zho', label: 'Chinois (zho)' },
  { value: 'kor', label: 'Coréen (kor)' },
  { value: 'und', label: 'Langue indéterminée (und)' },
];
