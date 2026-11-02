import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HttpErrorResponse } from '@angular/common/http';
import { MatDialog } from '@angular/material/dialog';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { ConfigurationSectionComponent } from '../../../shared/ui/configuration-section.component';
import {
  SettingRowComponent,
  SettingGroupComponent,
} from '../../../shared/ui/settings/setting-row.component';
import { SettingsNoticeComponent } from '../../../shared/ui/settings/settings-notice.component';
import { SettingsSaveBarComponent } from '../../../shared/ui/settings/settings-save-bar.component';
import { NotificationService } from '../../../shared/services/notification.service';
import { ReverseProxyDialogComponent } from '../reverse-proxy/reverse-proxy-dialog.component';
import { PublicUrlService } from '../../../shared/services/public-url.service';
import {
  NetworkSettings,
  NetworkSettingsService,
} from '../../../shared/services/network-settings.service';

@Component({
  selector: 'app-network-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatSlideToggleModule,
    ConfigurationSectionComponent,
    SettingRowComponent,
    SettingGroupComponent,
    SettingsNoticeComponent,
    SettingsSaveBarComponent,
  ],
  template: `
    <app-configuration-section
      title="Adresse publique"
      description="Réglage propre à ce nœud."
      icon="link"
    >
      <app-setting-group>
        <app-setting-row
          label="URL publique"
          hint="Adresse par laquelle ce nœud est joignable. Elle remplace celle du navigateur dans les adresses à copier (webhooks, Prometheus, assistants MCP). Laissez vide pour utiliser l’adresse de la page."
          [stacked]="true"
        >
          <mat-form-field appearance="outline" class="field" subscriptSizing="dynamic">
            <mat-label>URL publique</mat-label>
            <input
              matInput
              type="url"
              inputmode="url"
              autocomplete="off"
              [ngModel]="publicUrl()"
              (ngModelChange)="publicUrl.set($event)"
              [disabled]="busy()"
              [placeholder]="browserOrigin"
            />
          </mat-form-field>
          @if (urlError()) {
            <app-settings-notice tone="danger">{{ urlError() }}</app-settings-notice>
          }
        </app-setting-row>
      </app-setting-group>
    </app-configuration-section>
    <app-configuration-section
      title="Reverse proxy"
      description="Réglage propre à ce nœud."
      icon="server"
    >
      <button section-actions matButton="tonal" type="button" (click)="openGenerator()">
        Générer une configuration
      </button>
      <app-settings-notice tone="warning">
        Une adresse déclarée par erreur permettrait à son appelant de falsifier l’adresse et le
        schéma vus par l’application.
      </app-settings-notice>
      <app-setting-group>
        <app-setting-row
          label="Faire confiance au reverse proxy"
          hint="Utilise ses en-têtes X-Forwarded-* pour les limites de requêtes, les journaux et le cookie sécurisé."
        >
          <mat-slide-toggle
            aria-label="Faire confiance au reverse proxy"
            [checked]="trustProxy()"
            [disabled]="busy()"
            (change)="trustProxy.set($event.checked)"
          />
        </app-setting-row>
        <app-setting-row
          label="Adresses ou réseaux du proxy"
          hint="Une par ligne, par exemple 192.168.1.10 ou 172.18.0.0/16."
          [stacked]="true"
        >
          <mat-form-field appearance="outline" class="field" subscriptSizing="dynamic">
            <mat-label>Adresses ou réseaux</mat-label>
            <textarea
              matInput
              rows="4"
              [ngModel]="proxiesText()"
              (ngModelChange)="proxiesText.set($event)"
              [disabled]="busy()"
              placeholder="192.168.1.10&#10;172.18.0.0/16"
            ></textarea>
          </mat-form-field>
          @if (error()) {
            <app-settings-notice tone="danger">{{ error() }}</app-settings-notice>
          }
          @if (detectionMessage()) {
            <app-settings-notice tone="info">{{ detectionMessage() }}</app-settings-notice>
          }
          <button matButton="tonal" [disabled]="busy()" (click)="detect()">
            Détecter le proxy de cette connexion
          </button>
        </app-setting-row>
      </app-setting-group>
    </app-configuration-section>
    <app-settings-save-bar
      [visible]="dirty()"
      [busy]="busy()"
      (saved)="save()"
      (cancelled)="cancel()"
    />
  `,

  styles: `
    :host {
      display: grid;
      gap: var(--space-6);
    }
    .field {
      display: block;
      width: 100%;
    }
  `,
})
export class NetworkTabComponent {
  private readonly service = inject(NetworkSettingsService);
  private readonly notifications = inject(NotificationService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly dialog = inject(MatDialog);
  private saved = { trustProxy: false, proxies: '', publicUrl: '' };
  private readonly publicUrlService = inject(PublicUrlService);
  protected readonly browserOrigin = window.location.origin;
  protected readonly publicUrl = signal('');
  protected readonly urlError = signal('');
  protected readonly trustProxy = signal(false);
  protected readonly proxiesText = signal('');
  protected readonly busy = signal(true);
  protected readonly error = signal('');
  protected readonly detectionMessage = signal('');

  constructor() {
    this.service
      .get()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (settings) => {
          this.apply(settings);
          this.busy.set(false);
        },
        error: () => {
          this.notifications.show('Impossible de lire le réglage réseau.');
          this.busy.set(false);
        },
      });
  }

  protected openGenerator(): void {
    this.dialog.open(ReverseProxyDialogComponent, { width: '720px', maxWidth: '96vw' });
  }

  protected dirty(): boolean {
    return (
      this.trustProxy() !== this.saved.trustProxy ||
      this.proxiesText().trim() !== this.saved.proxies ||
      this.publicUrl().trim() !== this.saved.publicUrl
    );
  }

  protected cancel(): void {
    this.trustProxy.set(this.saved.trustProxy);
    this.proxiesText.set(this.saved.proxies);
    this.publicUrl.set(this.saved.publicUrl);
    this.error.set('');
    this.urlError.set('');
    this.detectionMessage.set('');
  }

  protected save(): void {
    this.busy.set(true);
    this.error.set('');
    this.urlError.set('');
    this.service
      .save({
        trustProxy: this.trustProxy(),
        proxies: this.parse(),
        publicUrl: this.publicUrl().trim() || null,
      })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (settings) => {
          this.apply(settings);
          this.busy.set(false);
          this.notifications.show('Réglage réseau enregistré.');
        },
        error: (failure: HttpErrorResponse) => {
          const errors = failure.error?.errors;
          this.urlError.set(errors?.publicUrl?.[0] ?? '');
          this.error.set(
            errors?.proxies?.[0] ?? (errors?.publicUrl ? '' : 'Enregistrement refusé.'),
          );
          this.busy.set(false);
        },
      });
  }

  protected detect(): void {
    this.busy.set(true);
    this.detectionMessage.set('');
    this.service
      .detect()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (detection) => {
          this.busy.set(false);
          const address = detection.proxyAddress;
          if (!address || !detection.forwardedHeadersSeen) {
            this.detectionMessage.set(
              'Aucun en-tête X-Forwarded-* reçu : cette connexion ne passe pas par un reverse proxy, ou il ne les transmet pas.',
            );
          } else if (!detection.privateAddress) {
            this.detectionMessage.set(
              `La connexion arrive de ${address}, une adresse publique : ajoutez-la à la main si c’est bien votre proxy.`,
            );
          } else if (detection.alreadyTrusted) {
            this.detectionMessage.set(`Le proxy ${address} est déjà déclaré.`);
          } else {
            const lines = this.parse();
            this.proxiesText.set([...lines, address].join('\n'));
            this.trustProxy.set(true);
            this.detectionMessage.set(
              `Proxy détecté : ${address}. Il a été ajouté à la liste ; enregistrez pour l’appliquer.`,
            );
          }
        },
        error: () => {
          this.busy.set(false);
          this.notifications.show('Détection impossible.');
        },
      });
  }

  private parse(): string[] {
    return this.proxiesText()
      .split(/[\s,;]+/)
      .filter((item) => item.length > 0);
  }

  private apply(settings: NetworkSettings): void {
    this.saved = {
      trustProxy: settings.trustProxy,
      proxies: (settings.proxies ?? []).join('\n'),
      publicUrl: settings.publicUrl ?? '',
    };
    this.trustProxy.set(this.saved.trustProxy);
    this.proxiesText.set(this.saved.proxies);
    this.publicUrl.set(this.saved.publicUrl);
    this.publicUrlService.set(settings.publicUrl);
  }
}
