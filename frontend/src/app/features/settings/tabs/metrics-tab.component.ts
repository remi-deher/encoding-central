import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { ConfigurationSectionComponent } from '../../../shared/ui/configuration-section.component';
import {
  SettingRowComponent,
  SettingGroupComponent,
} from '../../../shared/ui/settings/setting-row.component';
import { ClipboardService } from '../../../shared/services/clipboard.service';
import { PublicUrlService } from '../../../shared/services/public-url.service';
import { NotificationService } from '../../../shared/services/notification.service';
@Component({
  selector: 'app-metrics-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ConfigurationSectionComponent,
    MatButtonModule,
    SettingRowComponent,
    SettingGroupComponent,
  ],
  template: `
    <app-configuration-section
      title="Métriques Prometheus"
      description="Point de collecte pour votre outil de supervision."
      icon="api"
    >
      <app-setting-group>
        <app-setting-row label="Adresse de collecte">
          <code row-detail>{{ address() }}</code>
          <button matButton="tonal" (click)="copy()">Copier</button>
        </app-setting-row>
      </app-setting-group>
    </app-configuration-section>
  `,
  styles: `
    code {
      overflow-wrap: anywhere;
      font-size: var(--font-label);
    }
  `,
})
export class MetricsTabComponent {
  private readonly clipboard = inject(ClipboardService);
  private readonly notifications = inject(NotificationService);
  private readonly publicUrl = inject(PublicUrlService);
  protected readonly address = computed(() => `${this.publicUrl.origin()}/metrics`);
  protected async copy(): Promise<void> {
    this.notifications.show(
      (await this.clipboard.copy(this.address()))
        ? 'Adresse copiée.'
        : 'Impossible de copier l’adresse.',
    );
  }
}
