import { ChangeDetectionStrategy, Component, inject, signal, viewChild } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { RecordFormComponent, RecordFormData } from '../../shared/ui/record-form.component';
import { MatButtonModule } from '@angular/material/button';
import { FlowValidation } from '../../shared/services/flow-validation';
import { NotificationService } from '../../shared/services/notification.service';

@Component({
  selector: 'app-flow-guided-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RecordFormComponent, MatButtonModule],
  template: `
    <h1 class="visually-hidden">{{ definition.title }}</h1>
    @if (form(); as form) {
      <app-record-form
        [data]="form"
        [busy]="busy()"
        (submitted)="generate($event)"
        (cancelled)="cancel()"
      />
    } @else if (loadError()) {
      <p role="alert">Impossible de charger les options des composants installés.</p>
      <button matButton="tonal" (click)="loadOptions()">Réessayer</button>
    } @else {
      <p role="status">Chargement des options vidéo…</p>
    }
  `,
})
export class FlowGuidedPageComponent {
  private readonly api = inject(FlowValidation);
  private readonly router = inject(Router);
  private readonly notifications = inject(NotificationService);
  protected readonly busy = signal(false);
  private readonly id = crypto.randomUUID();
  private readonly recordForm = viewChild(RecordFormComponent);
  private saved = false;
  hasUnsavedChanges(): boolean {
    return !this.saved && (this.recordForm()?.hasUnsavedChanges() ?? false);
  }
  protected readonly form = signal<RecordFormData | null>(null);
  protected readonly loadError = signal(false);
  protected readonly definition: RecordFormData = {
    title: 'Créer un flow vidéo guidé',
    description: 'Préparez un traitement unique vers MKV, puis ouvrez son graphe dans l’éditeur.',
    submitLabel: 'Créer le brouillon et ouvrir l’éditeur',
    reviewTitle: 'Vérifier les choix avant de créer le brouillon',
    initialValues: {
      name: inject(ActivatedRoute).snapshot.queryParamMap.get('name') ?? '',
      codec: 'hevc',
      encoder: 'automatic',
      quality: 'balanced',
      audio: 'keep',
      retryWithCpu: 'false',
    },
    fields: [
      { id: 'name', label: 'Nom du flow', type: 'text', required: true, maxLength: 128 },
      {
        id: 'codec',
        label: 'Codec vidéo',
        type: 'select',
        required: true,
        options: [],
      },
      {
        id: 'encoder',
        label: 'Choix de l’encodeur',
        type: 'select',
        required: true,
        hint: 'Le profil intégré Équilibré est embarqué. Seuls les encodeurs vérifiés du worker sont utilisables.',
        options: [
          { value: 'automatic', label: 'Automatique selon le profil' },
          { value: 'cpu', label: 'CPU' },
        ],
      },
      {
        id: 'quality',
        label: 'Qualité adaptative selon la résolution',
        type: 'select',
        required: true,
        options: [],
      },
      {
        id: 'retryWithCpu',
        label: 'Après un échec matériel',
        type: 'select',
        required: true,
        hint: 'En mode automatique uniquement. Un seul nouvel essai CPU, à partir du même fichier de travail.',
        options: [
          { value: 'false', label: 'Arrêter le traitement' },
          { value: 'true', label: 'Réessayer en CPU' },
        ],
      },
      {
        id: 'audio',
        label: 'Pistes audio',
        type: 'select',
        required: true,
        hint: 'Toutes les pistes sont conservées. L’adaptation utilise les règles audio intégrées et refuse une disposition inconnue.',
        options: [
          { value: 'keep', label: 'Conserver sans conversion' },
          { value: 'adapt', label: 'Adapter : stéréo AAC, multicanal AC-3 (6 canaux maximum)' },
        ],
      },
    ],
    steps: [
      {
        id: 'general',
        label: 'Général',
        description:
          'Flow vidéo générique vers MKV. Les distinctions film et série ne changent pas le traitement média.',
        fieldIds: ['name', 'codec'],
      },
      {
        id: 'video',
        label: 'Vidéo',
        description:
          'Profondeur automatique et préservation HDR activées. Les capacités sont vérifiées lors de l’attribution.',
        fieldIds: ['encoder', 'quality', 'retryWithCpu'],
      },
      {
        id: 'tracks',
        label: 'Audio et sous-titres',
        description:
          'Les sous-titres et les pistes non modifiées sont conservés. Aucun filtrage de langue ni changement de style.',
        fieldIds: ['audio'],
      },
      {
        id: 'output',
        label: 'Résultat',
        description:
          'Les réglages alimentent un plan, exécuté en une commande FFmpeg puis analysé et validé avant publication. La bibliothèque décide seule du remplacement de la source. Ce parcours crée un brouillon sans publication ni affectation à une bibliothèque.',
        fieldIds: [],
      },
    ],
  };

  constructor() {
    void this.loadOptions();
  }

  protected async loadOptions(): Promise<void> {
    this.loadError.set(false);
    try {
      const plugins = await this.api.catalog();
      const schema = plugins
        .find((plugin) => plugin.id === 'video')
        ?.components.find((component) => component.id === 'video-transcode')?.configurationSchema;
      const fields = this.definition.fields.map((field) => {
        const source =
          field.id === 'codec' ? 'videoCodec' : field.id === 'quality' ? 'qualityTier' : null;
        if (!source) return field;
        const allowed =
          field.id === 'codec' ? ['hevc', 'h264', 'av1'] : ['balanced', 'high', 'compact'];
        const descriptor = schema?.fields.find((item) => item.id === source);
        const options =
          descriptor && 'options' in descriptor
            ? descriptor.options?.filter((option) => allowed.includes(option.value))
            : undefined;
        if (!options?.length) throw new Error('Options vidéo indisponibles');
        return { ...field, options };
      });
      this.form.set({ ...this.definition, fields });
    } catch {
      this.loadError.set(true);
    }
  }

  protected cancel(): void {
    void this.router.navigate(['/flows']);
  }

  protected async generate(values: Record<string, string>): Promise<void> {
    if (this.busy()) return;
    this.busy.set(true);
    try {
      const definition = await this.api.generateGuided({
        id: this.id,
        name: values['name'],
        codec: values['codec'],
        encoder: values['encoder'],
        quality: values['quality'],
        audio: values['audio'],
        retryWithCpu: values['retryWithCpu'] === 'true',
      });
      await this.api.saveDraft(definition, 0);
      this.saved = true;
      await this.router.navigate(['/flows', definition.id, 'edit']);
    } catch {
      this.notifications.show('Impossible de créer le brouillon guidé. Vos choix sont conservés.');
    } finally {
      this.busy.set(false);
    }
  }
}
