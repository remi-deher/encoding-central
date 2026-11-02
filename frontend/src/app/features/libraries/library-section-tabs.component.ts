import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { SubnavComponent, SubnavOption } from '../../shared/ui/subnav.component';
import { SubnavTemplateComponent } from '../../shared/templates/subnav-template.component';

const SECTIONS: readonly SubnavOption[] = [
  { value: 'media', label: 'Bibliothèques', route: '/libraries' },
  { value: 'scripts', label: 'Scripts', route: '/libraries/scripts' },
  { value: 'templates', label: 'Modèles de flow', route: '/libraries/templates' },
  { value: 'packs', label: 'Packs de composants', route: '/libraries/packs' },
  { value: 'encoding', label: 'Profils d’encodage', route: '/libraries/encoding-profiles' },
  { value: 'resources', label: 'Ressources', route: '/libraries/resources' },
];

/** Navigation commune entre les catalogues de ressources réutilisables. */
@Component({
  selector: 'app-library-section-tabs',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [SubnavComponent, SubnavTemplateComponent],
  styleUrl: './library-section-tabs.component.scss',
  template: `
    <app-subnav-template>
      <app-subnav
        label="Ressources"
        [options]="sections"
        [selected]="selected()"
        variant="links"
        presentation="capsule"
      />
    </app-subnav-template>
  `,
})
export class LibrarySectionTabsComponent {
  readonly selected = input.required<string>();
  protected readonly sections = SECTIONS;
}
