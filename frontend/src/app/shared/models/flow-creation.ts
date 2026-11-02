import type { RecordFormData } from '../ui/record-form.component';
import type { FlowTemplateSummary } from './flow-templates';

export function createFlowCreationForm(
  templates: readonly FlowTemplateSummary[] = [],
): RecordFormData {
  const options = [
    { value: 'guided', label: 'Assistant vidéo — création guidée', mediaTypes: ['video'] },
    { value: 'blank', label: 'Flow vierge' },
    ...templates.map((template) => ({
      value: `template:${template.id}`,
      label: template.name,
      mediaTypes: [template.mediaType],
    })),
  ];
  return {
    title: 'Créer un flow',
    description:
      'Choisissez un type de média, puis partez d’un guide, d’un flow vierge ou d’un vrai modèle.',
    submitLabel: 'Continuer dans l’éditeur',
    reviewTitle: 'Vérifier le nouveau flow',
    fields: [
      { id: 'name', label: 'Nom du flow', type: 'text', required: true, maxLength: 128 },
      {
        id: 'mediaType',
        label: 'Type de média',
        type: 'select',
        required: true,
        options: [
          { value: 'video', label: 'Vidéo' },
          { value: 'image', label: 'Image' },
          { value: 'music', label: 'Musique' },
          { value: 'subtitles', label: 'Sous-titres' },
        ],
      },
      {
        id: 'starter',
        label: 'Point de départ',
        type: 'select',
        required: true,
        filterOptionsByFieldId: 'mediaType',
        options,
      },
    ],
    steps: [
      { id: 'information', label: 'Informations', fieldIds: ['name', 'mediaType'] },
      {
        id: 'creation',
        label: 'Démarrage',
        description: 'Choisissez comment commencer votre flow.',
        fieldIds: ['starter'],
      },
    ],
    initialValues: { mediaType: 'video', starter: 'guided' },
  };
}

export const FLOW_CREATION_FORM: RecordFormData = createFlowCreationForm();
