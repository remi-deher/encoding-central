import type {
  FlowComponentDefinition,
  FlowComponentOutput,
  FlowComponentConfigurationSchema,
} from './flow-component';
import type { MediaType } from './media-type';

const next: FlowComponentOutput = {
  id: 'success',
  label: 'Suite',
  description: 'Continuer sur ce chemin.',
};
const error: FlowComponentOutput = {
  id: 'error',
  label: 'Erreur',
  description: 'Traiter l’échec de cette étape.',
};
const skipped: FlowComponentOutput = {
  id: 'skipped',
  label: 'Ignoré',
  description: 'Aucun traitement nécessaire.',
};
const mediaInput = (
  type: FlowComponentDefinition['mediaType'],
  name: string,
  id: string,
): FlowComponentDefinition => ({
  id,
  name,
  description: 'Reçoit le fichier et analyse ses métadonnées avant les actions du flow.',
  kind: 'input',
  mediaType: type,
  pluginId: 'base',
  pluginName: 'Base',
  outputs: [next],
});
export const MEDIA_INPUT_COMPONENTS: Readonly<Record<MediaType, FlowComponentDefinition>> = {
  video: mediaInput('video', 'Fichier vidéo', 'base-input-video'),
  image: mediaInput('image', 'Fichier image', 'base-input-image'),
  music: mediaInput('audio', 'Fichier audio', 'base-input-music'),
  subtitles: mediaInput('subtitles', 'Fichier de sous-titres', 'base-input-subtitles'),
};

export const GENERIC_FILE_INPUT: FlowComponentDefinition = {
  id: 'base-input-file',
  name: 'Fichier',
  kind: 'input',
  mediaType: 'general',
  pluginId: 'base',
  pluginName: 'Base',
  description:
    'Reçoit un fichier. Les scripts préparatoires peuvent précéder la lecture des métadonnées média.',
  outputs: [next],
};

export function getFlowInputComponents(mediaType: MediaType): readonly FlowComponentDefinition[] {
  return [GENERIC_FILE_INPUT, MEDIA_INPUT_COMPONENTS[mediaType]];
}

export function createFlowInputConfigurationSchema(
  mediaType: MediaType,
): FlowComponentConfigurationSchema {
  return {
    version: 1,
    fields: [
      {
        id: 'inputComponentId',
        type: 'select',
        label: 'Type d’entrée',
        required: true,
        defaultValue: GENERIC_FILE_INPUT.id,
        description:
          'Fichier générique pour préparer le flow avant analyse, ou entrée spécialisée avec scan immédiat.',
        options: getFlowInputComponents(mediaType).map((component) => ({
          value: component.id,
          label: component.name,
        })),
      },
    ],
  };
}
export const FLOW_CONTROL_COMPONENTS: readonly FlowComponentDefinition[] = [
  {
    id: 'base-script',
    name: 'Exécuter un script',
    description:
      'Prépare les variables et les règles du flow ; les métadonnées sont disponibles après analyse.',
    kind: 'script',
    mediaType: 'general',
    pluginId: 'base',
    pluginName: 'Base',
    outputs: [next, skipped, error],
    configurationSchema: {
      version: 1,
      fields: [
        {
          id: 'scriptReference',
          label: 'Script de la bibliothèque',
          type: 'script-reference',
          description:
            'Choisissez une version publiée. Son code est copié dans le flow et reste figé avec sa version.',
        },
        {
          id: 'language',
          label: 'Langage',
          type: 'select',
          defaultValue: 'javascript',
          options: [
            { value: 'javascript', label: 'JavaScript' },
            { value: 'python', label: 'Python' },
          ],
        },
        {
          id: 'code',
          label: 'Code du script',
          type: 'text',
          multiline: true,
          required: true,
          description:
            'Le script disposera du contexte média et des services du moteur. Son exécution attend le contrat des scripts.',
        },
        {
          id: 'timeoutSeconds',
          label: 'Durée maximale (secondes)',
          type: 'number',
          defaultValue: 60,
          min: 1,
          max: 86400,
        },
        {
          id: 'useTools',
          label: 'Outils média (tools)',
          type: 'boolean',
          defaultValue: false,
          description:
            "Ajoute l’aide tools au script : tools.run('ffmpeg', [...]) lance ffmpeg, ffprobe, mkvmerge, mkvextract, mkvpropedit, mediainfo ou magick dans le dossier de travail isolé du script. Exige un worker qui annonce script-tools.",
        },
      ],
    },
  },
  {
    id: 'base-prepare-processing',
    name: 'Préparer le traitement',
    description: 'Initialise un plan média sans modifier le fichier courant.',
    kind: 'preparation',
    mediaType: 'general',
    supportedFlowMediaTypes: ['video', 'music'],
    pluginId: 'base',
    pluginName: 'Base',
    outputs: [next, error],
    configurationSchema: {
      version: 1,
      fields: [
        {
          id: 'tool',
          label: 'Outil',
          type: 'select',
          defaultValue: 'ffmpeg',
          options: [
            { value: 'ffmpeg', label: 'FFmpeg' },
            { value: 'mkvtoolnix', label: 'MKVToolNix', supportedFlowMediaTypes: ['video'] },
          ],
        },
        {
          id: 'preserveUnchangedTracks',
          label: 'Conserver les pistes non modifiées',
          type: 'boolean',
          defaultValue: true,
        },
      ],
    },
  },
  {
    id: 'base-execute-processing',
    name: 'Exécuter le traitement',
    description: 'Exécute le plan sur une sortie temporaire, puis analyse et valide le résultat.',
    kind: 'execution',
    mediaType: 'general',
    supportedFlowMediaTypes: ['video', 'music'],
    pluginId: 'base',
    pluginName: 'Base',
    outputs: [next, skipped, error],
    configurationSchema: {
      version: 1,
      fields: [
        {
          id: 'forceProcessing',
          label: 'Traiter même si le média est conforme',
          type: 'boolean',
          defaultValue: false,
        },
      ],
    },
  },
  {
    id: 'base-defer-processing',
    name: 'Reporter le traitement',
    description: 'Reporte la tâche et libère le worker jusqu’à la prochaine tentative.',
    kind: 'terminal',
    mediaType: 'general',
    pluginId: 'base',
    pluginName: 'Base',
    outputs: [],
    configurationSchema: {
      version: 1,
      fields: [
        {
          id: 'delayMinutes',
          label: 'Délai (minutes)',
          type: 'number',
          defaultValue: 2,
          min: 1,
          max: 1440,
        },
        { id: 'reason', label: 'Motif du report', type: 'text', required: true },
      ],
    },
  },
  {
    id: 'base-complete-flow',
    name: 'Terminer le flow',
    description:
      'Termine ce chemin avec un résultat explicite. La publication reste contrôlée par le moteur.',
    kind: 'terminal',
    mediaType: 'general',
    pluginId: 'base',
    pluginName: 'Base',
    outputs: [],
    configurationSchema: {
      version: 1,
      fields: [
        {
          id: 'result',
          label: 'Résultat',
          type: 'select',
          defaultValue: 'unchanged',
          options: [
            { value: 'unchanged', label: 'Déjà conforme' },
            { value: 'success', label: 'Traitement terminé' },
            { value: 'failed', label: 'Échec' },
          ],
        },
        { id: 'message', label: 'Message pour l’historique', type: 'text' },
      ],
    },
  },
];
