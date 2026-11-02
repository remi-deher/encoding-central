import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatExpansionModule } from '@angular/material/expansion';
import { ConfigurationSectionComponent } from '../../../shared/ui/configuration-section.component';
import {
  SettingRowComponent,
  SettingGroupComponent,
} from '../../../shared/ui/settings/setting-row.component';
import { ClipboardService } from '../../../shared/services/clipboard.service';
import { PublicUrlService } from '../../../shared/services/public-url.service';
import { NotificationService } from '../../../shared/services/notification.service';
@Component({
  selector: 'app-webhooks-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    MatButtonModule,
    MatExpansionModule,
    ConfigurationSectionComponent,
    SettingRowComponent,
    SettingGroupComponent,
  ],
  template: `
    <app-configuration-section
      title="Webhooks"
      description="Déclenchez la découverte de fichiers depuis Sonarr ou Radarr."
      icon="link"
    >
      <app-setting-group>
        @for (endpoint of endpoints; track endpoint.name) {
          <app-setting-row [label]="endpoint.name">
            <code row-detail>{{ baseUrl() + endpoint.path }}</code>
            <button
              matButton="tonal"
              [attr.aria-label]="'Copier l’adresse ' + endpoint.name"
              (click)="copy(endpoint.path)"
            >
              Copier
            </button>
          </app-setting-row>
        }
        <mat-expansion-panel class="advanced">
          <mat-expansion-panel-header>
            <mat-panel-title>Options avancées : authentification</mat-panel-title>
          </mat-expansion-panel-header>
          <p>
            Utilisez le secret défini dans la configuration du plugin comme mot de passe dans Sonarr
            ou Radarr, ou dans l’en-tête <code>X-Encoding-Webhook-Secret</code>.
          </p>
        </mat-expansion-panel>
      </app-setting-group>
    </app-configuration-section>
  `,

  styles: `
    code {
      overflow-wrap: anywhere;
      font-size: var(--font-label);
    }
    .mat-expansion-panel.advanced {
      background: var(--surface-container-low);
      box-shadow: none;
      border-radius: var(--radius-sm) var(--radius-sm) var(--radius-xl) var(--radius-xl);
    }
    p {
      margin: 0;
      color: var(--text-secondary);
      line-height: 1.6;
    }
  `,
})
export class WebhooksTabComponent {
  private readonly clipboard = inject(ClipboardService);
  private readonly notifications = inject(NotificationService);
  private readonly publicUrl = inject(PublicUrlService);
  protected readonly baseUrl = this.publicUrl.origin;
  protected readonly endpoints = [
    { name: 'Sonarr', path: '/api/v1/integrations/sonarr/webhook' },
    { name: 'Radarr', path: '/api/v1/integrations/radarr/webhook' },
  ];
  protected async copy(path: string): Promise<void> {
    this.notifications.show(
      (await this.clipboard.copy(this.baseUrl() + path))
        ? 'Adresse copiée.'
        : 'Impossible de copier l’adresse.',
    );
  }
}
