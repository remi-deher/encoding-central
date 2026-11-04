import { computed, inject, Injectable, signal } from '@angular/core';
import { FlowValidation } from '../../../shared/services/flow-validation';
import { FlowPluginCatalog } from '../../../shared/models/flow-validation';
import {
  isFlowComponentCompatible,
  FlowComponentDefinition,
} from '../../../shared/models/flow-component';
import { MEDIA_TYPE_OPTIONS, MediaType } from '../../../shared/models/media-type';

export const TYPE_OPTIONS = [
  { value: 'video', label: 'Vidéo' },
  { value: 'image', label: 'Image' },
  { value: 'audio', label: 'Audio' },
  { value: 'subtitles', label: 'Sous-titres' },
  { value: 'general', label: 'Logique' },
] as const;

/**
 * Catalogue des composants installés (depuis le backend) et filtrage par
 * type de média du flow en cours d’édition.
 */
@Injectable()
export class FlowComponentsStore {
  private readonly serverValidation = inject(FlowValidation);

  readonly flowMediaType = signal<MediaType>('video');
  readonly installedComponents = signal<readonly FlowComponentDefinition[]>([]);

  readonly components = computed(() =>
    this.installedComponents().filter(
      (component) =>
        component.kind !== 'input' && isFlowComponentCompatible(component, this.flowMediaType()),
    ),
  );

  readonly otherMediaComponents = computed(() =>
    this.installedComponents().filter(
      (component) =>
        component.kind !== 'input' && !isFlowComponentCompatible(component, this.flowMediaType()),
    ),
  );

  readonly typeOptions = computed(() =>
    TYPE_OPTIONS.filter((option) =>
      this.components().some((component) => component.mediaType === option.value),
    ),
  );

  readonly pluginOptions = computed(() => [
    ...new Map(
      this.components().map((component) => [
        component.pluginId,
        { value: component.pluginId, label: component.pluginName },
      ]),
    ).values(),
  ]);

  /** Charge le catalogue des plugins et aplati les composants installés. */
  async loadCatalog(): Promise<readonly FlowPluginCatalog[]> {
    const plugins = await this.serverValidation.catalog();
    this.installedComponents.set(
      plugins
        .filter((plugin) => plugin.enabled !== false)
        .flatMap((plugin) =>
          plugin.components.map((component) => ({
            ...component,
            pluginId: plugin.id,
            pluginName: plugin.name,
          })),
        ),
    );
    return plugins;
  }

  mediaTypeFromParam(value: string | null): MediaType {
    return MEDIA_TYPE_OPTIONS.find((option) => option.value === value)?.value ?? 'video';
  }
}
