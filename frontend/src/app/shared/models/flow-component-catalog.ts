import type { FlowComponentDefinition, FlowComponentOutput } from './flow-component';
import { MEDIA_TOOL_ACTIONS } from './media-tool-actions';
import { FLOW_CONTROL_COMPONENTS } from './flow-control-components';

const output = (id: string, label: string, description: string): FlowComponentOutput => ({
  id,
  label,
  description,
});
const COMMON_OUTPUTS = [
  output('success', 'Suite', 'Le composant a terminé son traitement.'),
  output('error', 'Erreur', 'Le composant n’a pas pu terminer son traitement.'),
];
/** Only used to render and edit saved flows; this legacy block is not offered in the palette. */
export const LEGACY_FLOW_COMPONENTS: readonly FlowComponentDefinition[] = [
  {
    id: 'base-condition',
    name: 'Condition héritée',
    description: 'Bloc JavaScript conservé pour les flows existants.',
    kind: 'condition',
    mediaType: 'general',
    supportedFlowMediaTypes: ['video', 'image', 'music', 'subtitles'],
    pluginId: 'base',
    pluginName: 'Base (compatibilité)',
    outputs: [
      output('yes', 'Oui', 'La condition est satisfaite.'),
      output('no', 'Non', 'La condition n’est pas satisfaite.'),
      output('error', 'Erreur', 'La condition n’a pas pu être évaluée.'),
    ],
    configurationSchema: {
      version: 1,
      fields: [
        {
          id: 'expression',
          type: 'text',
          label: 'Expression',
          description: 'Expression JavaScript historique évaluée pour choisir la sortie.',
          required: true,
          multiline: true,
        },
      ],
    },
  },
];
export const FLOW_COMPONENT_CATALOG: readonly FlowComponentDefinition[] = [
  ...FLOW_CONTROL_COMPONENTS,
  ...MEDIA_TOOL_ACTIONS,
  {
    id: 'base-variable',
    name: 'Définir une variable',
    description: 'Crée ou met à jour une valeur réutilisable dans le flow.',
    kind: 'variable',
    mediaType: 'general',
    pluginId: 'base',
    pluginName: 'Base',
    outputs: COMMON_OUTPUTS,
    configurationSchema: {
      version: 1,
      fields: [
        { id: 'name', label: 'Nom de la variable', type: 'text', required: true },
        { id: 'value', label: 'Valeur', type: 'text', required: true },
      ],
    },
  },
];
