import { ChangeDetectionStrategy, Component } from '@angular/core';
import {
  SettingRowComponent,
  SettingGroupComponent,
} from '../../../shared/ui/settings/setting-row.component';
import { ConfigurationSectionComponent } from '../../../shared/ui/configuration-section.component';
@Component({
  selector: 'app-appearance-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ConfigurationSectionComponent, SettingRowComponent, SettingGroupComponent],
  template: `
    <app-configuration-section
      title="Apparence"
      description="Les préférences visuelles sont regroupées dans la navigation principale."
      icon="settings"
    >
      <app-setting-group>
        <app-setting-row
          label="Thème et couleur"
          hint="Ce choix s’applique dans ce navigateur uniquement."
        >
          <p>
            Ouvrez le bouton Apparence au pied de la navigation pour choisir le mode et la palette.
          </p>
        </app-setting-row>
      </app-setting-group>
    </app-configuration-section>
  `,
})
export class AppearanceTabComponent {}
