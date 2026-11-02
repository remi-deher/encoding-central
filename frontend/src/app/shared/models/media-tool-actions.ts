import type { FlowComponentConfigurationField, FlowComponentDefinition } from './flow-component';
import type { MediaType } from './media-type';

type Field = FlowComponentConfigurationField;
type Tool = 'ffmpeg' | 'mkvtoolnix' | 'ffprobe' | 'mediainfo' | 'imagemagick';
const toolNames: Record<Tool, string> = {
  ffmpeg: 'FFmpeg',
  mkvtoolnix: 'MKVToolNix',
  ffprobe: 'FFprobe',
  mediainfo: 'MediaInfo',
  imagemagick: 'ImageMagick',
};
const supportedFlowMediaTypes = (
  tool: Tool,
  mediaType: FlowComponentDefinition['mediaType'],
  id: string,
): readonly MediaType[] | undefined => {
  if (tool === 'mkvtoolnix') return ['video'];
  if (tool === 'mediainfo')
    return mediaType === 'video'
      ? ['video']
      : id === 'base-inspect-track'
        ? ['video', 'music']
        : ['video', 'music', 'image'];
  if (tool === 'ffprobe' && mediaType === 'general')
    return ['video', 'music', 'image', 'subtitles'];
  return undefined;
};
const text = (id: string, label: string, required = false, description?: string): Field => ({
  id,
  label,
  type: 'text',
  required,
  description,
});
const number = (
  id: string,
  label: string,
  defaultValue: number,
  min: number,
  max?: number,
  step = 1,
  suggestions?: readonly number[],
): Field => ({
  id,
  label,
  type: 'number',
  inline: true,
  defaultValue,
  min,
  max,
  step,
  suggestions,
});
const select = (
  id: string,
  label: string,
  options: readonly [string, string][],
  defaultValue = options[0][0],
): Field => ({
  id,
  label,
  type: 'select',
  inline: true,
  defaultValue,
  options: options.map(([value, label]) => ({ value, label })),
});
const boolean = (id: string, label: string, defaultValue = false): Field => ({
  id,
  label,
  type: 'boolean',
  inline: true,
  defaultValue,
});
const track = (): Field => number('trackId', 'Identifiant de piste', 0, 0);
const source = (): Field =>
  text(
    'sourceFile',
    'Fichier à ajouter',
    true,
    'Référence à un fichier local ou à un résultat produit par le flow.',
  );
const languages = (): Field[] => [
  text('language', 'Langue', true, 'Code de langue, par exemple fra ou eng.'),
  text('trackName', 'Nom de la piste'),
];
const selection = (): Field[] => [
  select('trackType', 'Type de piste', [
    ['all', 'Tous'],
    ['video', 'Vidéo'],
    ['audio', 'Audio'],
    ['subtitles', 'Sous-titres'],
  ]),
  text(
    'trackIds',
    'Identifiants des pistes',
    false,
    'Identifiants séparés par des virgules ; vide pour toutes les pistes du type choisi.',
  ),
  text('language', 'Filtrer par langue', false, 'Facultatif, par exemple fra ou eng.'),
];
const reading = (): Field[] => [
  {
    ...text(
      'resultVariable',
      'Variable de résultat',
      true,
      'Nom de la variable accessible aux actions et conditions suivantes.',
    ),
    defaultValue: 'mediaDetails',
  } as Field,
  boolean('refreshMetadata', 'Actualiser les métadonnées', false),
];
const dimensions = (): Field[] => [
  number('width', 'Largeur maximale (pixels)', 1920, 1),
  number('height', 'Hauteur maximale (pixels)', 1080, 1),
  boolean('preserveAspectRatio', 'Conserver les proportions', true),
];
const action = (
  id: string,
  name: string,
  description: string,
  tool: Tool,
  mediaType: FlowComponentDefinition['mediaType'],
  fields: readonly Field[] = [],
  pluginId = 'base',
): FlowComponentDefinition => ({
  id,
  name,
  description,
  kind: 'action',
  mediaType,
  supportedFlowMediaTypes: supportedFlowMediaTypes(tool, mediaType, id),
  pluginId,
  pluginName: pluginId === 'video' ? 'Vidéo' : 'Base',
  executionToolId: tool,
  executionToolName: toolNames[tool],
  outputs: [
    { id: 'success', label: 'Suite', description: 'L’action a terminé son traitement.' },
    { id: 'error', label: 'Erreur', description: 'L’action n’a pas pu terminer son traitement.' },
  ],
  configurationSchema: {
    version: 1,
    fields: [
      ...([
        'base-transcode-video',
        'base-transcode-audio',
        'base-normalize-audio',
        'base-resize-video',
        'video-remux-media',
        'base-remux-matroska',
        'base-add-audio-track',
        'base-add-subtitle-track',
        'base-remove-tracks',
        'base-reorder-tracks',
        'base-edit-track-properties',
        'base-edit-matroska-metadata',
        'base-remove-attachments',
        'base-replace-chapters',
      ].includes(id)
        ? [
            {
              id: 'processingMode',
              label: 'Mode de traitement',
              type: 'select' as const,
              defaultValue: 'plan',
              description:
                'Ajouter les réglages au plan, puis utiliser Exécuter le traitement ; ou réaliser une opération autonome.',
              options: [
                { value: 'plan', label: 'Ajouter au plan' },
                { value: 'direct', label: 'Opération autonome' },
              ],
            },
          ]
        : []),
      ...fields,
    ],
  },
});

/** Catalogue déclaratif de l’éditeur ; les adaptateurs d’exécution seront fournis par le contrat média. */
export const MEDIA_TOOL_ACTIONS: readonly FlowComponentDefinition[] = [
  action(
    'base-transcode-video',
    'Encoder la vidéo',
    'Encode la vidéo selon le codec et la qualité choisis.',
    'ffmpeg',
    'video',
    [
      select('videoCodec', 'Codec vidéo', [
        ['hevc', 'H.265 / HEVC'],
        ['av1', 'AV1'],
        ['h264', 'H.264'],
      ]),
      number('quality', 'Qualité cible (CRF)', 23, 0, 51, 1, [18, 20, 23, 26, 28]),
      select(
        'preset',
        'Preset',
        [
          ['slow', 'Lent'],
          ['medium', 'Moyen'],
          ['fast', 'Rapide'],
        ],
        'medium',
      ),
      select('hardwareAcceleration', 'Accélération matérielle', [
        ['cpu', 'CPU'],
        ['nvenc', 'NVIDIA NVENC'],
        ['qsv', 'Intel Quick Sync'],
        ['vaapi', 'VAAPI'],
      ]),
      boolean('retryWithCpu', 'Repli sur CPU si l’encodage matériel échoue', false),
    ],
  ),
  action(
    'base-transcode-audio',
    'Encoder l’audio',
    'Convertit une piste audio et règle son débit et ses canaux.',
    'ffmpeg',
    'audio',
    [
      track(),
      select('audioCodec', 'Codec audio', [
        ['aac', 'AAC'],
        ['opus', 'Opus'],
        ['flac', 'FLAC'],
        ['ac3', 'AC-3'],
      ]),
      number('bitrateKbps', 'Débit cible avec perte (kbit/s)', 192, 32, 1536, 32),
      select('channels', 'Canaux', [
        ['source', 'Conserver'],
        ['2', 'Stéréo'],
        ['6', '5.1'],
        ['8', '7.1'],
      ]),
    ],
  ),
  action(
    'base-normalize-audio',
    'Normaliser le volume',
    'Ajuste le niveau sonore de la piste sélectionnée.',
    'ffmpeg',
    'audio',
    [
      track(),
      number('targetLoudness', 'Niveau cible (LUFS)', -16, -30, -5),
      number('truePeak', 'True peak maximal (dBTP)', -1.5, -9, 0, 0.1),
    ],
  ),
  action(
    'base-resize-video',
    'Redimensionner la vidéo',
    'Réencode la vidéo aux dimensions souhaitées en conservant ses proportions.',
    'ffmpeg',
    'video',
    [
      number('width', 'Largeur maximale (pixels)', 1920, 1),
      number('height', 'Hauteur maximale (pixels)', 1080, 1),
    ],
  ),
  action(
    'video-remux-media',
    'Changer de conteneur',
    'Copie les pistes compatibles dans un conteneur MKV ou MP4.',
    'ffmpeg',
    'video',
    [
      select('container', 'Conteneur', [
        ['mkv', 'Matroska (MKV)'],
        ['mp4', 'MP4'],
      ]),
    ],
    'video',
  ),
  action(
    'base-extract-audio',
    'Extraire une piste audio',
    'Copie ou convertit une piste audio dans un fichier séparé.',
    'ffmpeg',
    'audio',
    [
      track(),
      select('audioCodec', 'Mode de sortie', [
        ['copy', 'Copie du codec source'],
        ['aac', 'AAC'],
        ['flac', 'FLAC'],
        ['opus', 'Opus'],
      ]),
    ],
  ),
  action(
    'base-video-thumbnail',
    'Créer une miniature',
    'Extrait une image à la position choisie dans la vidéo.',
    'ffmpeg',
    'video',
    [
      number('positionSeconds', 'Position (secondes)', 0, 0),
      number('width', 'Largeur (pixels)', 640, 1),
      number('height', 'Hauteur (pixels)', 360, 1),
    ],
  ),
  action(
    'base-verify-decoding',
    'Vérifier le décodage',
    'Décode les pistes sélectionnées et signale les erreurs rencontrées.',
    'ffmpeg',
    'video',
    selection(),
  ),

  action(
    'base-remux-matroska',
    'Créer ou remuxer un MKV',
    'Regroupe les pistes choisies dans un conteneur Matroska sans les réencoder.',
    'mkvtoolnix',
    'video',
    [
      ...selection(),
      text('title', 'Titre du conteneur'),
      boolean('keepAttachments', 'Conserver les pièces jointes', true),
    ],
  ),
  action(
    'base-add-audio-track',
    'Ajouter une piste audio',
    'Ajoute un fichier audio au conteneur MKV.',
    'mkvtoolnix',
    'audio',
    [source(), ...languages(), boolean('defaultTrack', 'Piste par défaut')],
  ),
  action(
    'base-add-subtitle-track',
    'Ajouter des sous-titres',
    'Ajoute un fichier de sous-titres au conteneur MKV.',
    'mkvtoolnix',
    'subtitles',
    [source(), ...languages(), boolean('forcedTrack', 'Sous-titres forcés')],
  ),
  action(
    'base-remove-tracks',
    'Retirer des pistes',
    'Produit un MKV sans les pistes sélectionnées.',
    'mkvtoolnix',
    'video',
    selection(),
  ),
  action(
    'base-extract-track',
    'Extraire une piste',
    'Extrait une piste Matroska dans son format natif, sans conversion de codec.',
    'mkvtoolnix',
    'general',
    [track()],
  ),
  action(
    'base-reorder-tracks',
    'Réordonner les pistes',
    'Définit l’ordre des pistes dans le conteneur MKV.',
    'mkvtoolnix',
    'video',
    [
      text(
        'trackOrder',
        'Ordre des identifiants',
        true,
        'Identifiants séparés par des virgules, par exemple 0,2,1,3.',
      ),
    ],
  ),
  action(
    'base-edit-track-properties',
    'Modifier les propriétés d’une piste',
    'Règle la langue, le nom et les indicateurs d’une piste MKV.',
    'mkvtoolnix',
    'video',
    [
      track(),
      ...languages(),
      boolean('defaultTrack', 'Piste par défaut'),
      boolean('forcedTrack', 'Piste forcée'),
    ],
  ),
  action(
    'base-edit-matroska-metadata',
    'Modifier le titre du conteneur',
    'Change le titre d’une copie de travail du fichier MKV.',
    'mkvtoolnix',
    'video',
    [text('title', 'Titre du conteneur', true)],
  ),
  action(
    'base-extract-attachments',
    'Extraire les pièces jointes',
    'Extrait les polices, images et autres pièces jointes du MKV.',
    'mkvtoolnix',
    'video',
    [
      text(
        'attachmentIds',
        'Identifiants des pièces jointes',
        false,
        'Séparés par des virgules ; vide pour toutes les pièces jointes.',
      ),
    ],
  ),
  action(
    'base-remove-attachments',
    'Retirer les pièces jointes',
    'Produit une copie de travail du MKV sans les pièces jointes sélectionnées.',
    'mkvtoolnix',
    'video',
    [
      text(
        'attachmentIds',
        'Identifiants des pièces jointes',
        false,
        'Séparés par des virgules ; vide pour toutes les pièces jointes.',
      ),
    ],
  ),
  action(
    'base-extract-chapters',
    'Extraire les chapitres',
    'Exporte les chapitres du MKV dans un fichier séparé.',
    'mkvtoolnix',
    'video',
    [
      select('format', 'Format', [
        ['xml', 'XML Matroska'],
        ['simple', 'Texte simple'],
      ]),
    ],
  ),
  action(
    'base-replace-chapters',
    'Remplacer les chapitres',
    'Applique un fichier de chapitres à une copie de travail du MKV.',
    'mkvtoolnix',
    'video',
    [source()],
  ),
  action(
    'base-extract-subtitles',
    'Extraire les sous-titres',
    'Extrait les pistes de sous-titres sélectionnées dans leur format natif.',
    'mkvtoolnix',
    'subtitles',
    [
      text(
        'trackIds',
        'Identifiants des pistes',
        false,
        'Séparés par des virgules ; vide pour toutes les pistes de sous-titres.',
      ),
    ],
  ),

  action(
    'base-probe-media',
    'Analyser le média',
    'Lit les métadonnées du scan courant : conteneur, durée, taille et débit.',
    'ffprobe',
    'general',
    reading(),
  ),
  action(
    'base-probe-tracks',
    'Lister les pistes',
    'Lit les identifiants, types, codecs et langues des pistes.',
    'ffprobe',
    'general',
    reading(),
  ),
  action(
    'base-probe-video',
    'Lire les caractéristiques vidéo',
    'Lit la résolution, la fréquence d’images et le format de pixels.',
    'ffprobe',
    'video',
    [track(), ...reading()],
  ),
  action(
    'base-probe-audio',
    'Lire les caractéristiques audio',
    'Lit les canaux, la fréquence d’échantillonnage et le débit.',
    'ffprobe',
    'audio',
    [track(), ...reading()],
  ),

  action(
    'base-inspect-media',
    'Lire le rapport technique complet',
    'Lit les caractéristiques techniques disponibles avec MediaInfo.',
    'mediainfo',
    'general',
    reading(),
  ),
  action(
    'base-inspect-hdr',
    'Lire les caractéristiques HDR',
    'Lit le format HDR, la profondeur de couleur et les informations disponibles.',
    'mediainfo',
    'video',
    [track(), ...reading()],
  ),
  action(
    'base-inspect-track',
    'Lire les caractéristiques d’une piste',
    'Lit les détails techniques de la piste sélectionnée.',
    'mediainfo',
    'general',
    [track(), ...reading()],
  ),
  action(
    'base-export-media-report',
    'Exporter le rapport JSON',
    'Produit un rapport MediaInfo JSON comme résultat du flow.',
    'mediainfo',
    'general',
  ),

  action(
    'base-convert-image',
    'Convertir une image',
    'Convertit l’image dans le format choisi.',
    'imagemagick',
    'image',
    [
      select('format', 'Format', [
        ['webp', 'WebP'],
        ['avif', 'AVIF'],
        ['jpeg', 'JPEG'],
        ['png', 'PNG'],
      ]),
      number('quality', 'Qualité', 85, 1, 100),
    ],
  ),
  action(
    'base-resize-image',
    'Redimensionner une image',
    'Redimensionne l’image avec ou sans conservation des proportions.',
    'imagemagick',
    'image',
    dimensions(),
  ),
  action(
    'base-crop-image',
    'Recadrer une image',
    'Recadre une zone en pixels ou selon des proportions centrées.',
    'imagemagick',
    'image',
    [
      text(
        'aspectRatio',
        'Proportions cibles',
        false,
        'Facultatif, par exemple 16:9. Si renseigné, applique un recadrage centré plutôt que la zone en pixels.',
      ),
      number('x', 'Position horizontale (pixels)', 0, 0),
      number('y', 'Position verticale (pixels)', 0, 0),
      number('width', 'Largeur (pixels)', 1920, 1),
      number('height', 'Hauteur (pixels)', 1080, 1),
    ],
  ),
];
