import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialogActions, MatDialogClose, MatDialogContent } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { DialogFrameComponent } from '../../../shared/ui/dialog-frame.component';
import { ClusterApi } from '../../../shared/services/cluster-api';
import { ClipboardService } from '../../../shared/services/clipboard.service';
import { FileDownload } from '../../../shared/services/file-download';
import { NotificationService } from '../../../shared/services/notification.service';
import { PublicUrlService } from '../../../shared/services/public-url.service';
import {
  DEFAULT_DOMAIN,
  domainOf,
  generateProxyConfig,
  parseBackends,
  PROXY_LABELS,
  ProxyKind,
} from './proxy-config';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-reverse-proxy-dialog',
  imports: [
    FormsModule,
    MatButtonModule,
    MatButtonToggleModule,
    MatCheckboxModule,
    MatDialogActions,
    MatDialogClose,
    MatDialogContent,
    MatFormFieldModule,
    MatInputModule,
    DialogFrameComponent,
  ],
  template: `
    <app-dialog-frame eyebrow="Reverse proxy" icon="◈" heading="Générer une configuration">
      <mat-dialog-content class="app-dialog-body">
        <mat-button-toggle-group
          aria-label="Reverse proxy"
          [value]="proxy()"
          (change)="proxy.set($event.value)"
        >
          @for (kind of kinds; track kind) {
            <mat-button-toggle [value]="kind">{{ labels[kind] }}</mat-button-toggle>
          }
        </mat-button-toggle-group>

        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label>Nom de domaine</mat-label>
          <input
            matInput
            autocomplete="off"
            [ngModel]="domain()"
            (ngModelChange)="domain.set($event)"
            [placeholder]="defaultDomain"
          />
        </mat-form-field>

        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label>Nœuds serveurs (une adresse par ligne)</mat-label>
          <textarea
            matInput
            rows="3"
            [ngModel]="backendsText()"
            (ngModelChange)="backendsText.set($event)"
          ></textarea>
          <mat-hint>
            Adresse de chaque nœud, pas celle du proxy. Le port 8443 entre nœuds n’est jamais
            exposé.
          </mat-hint>
        </mat-form-field>

        <div class="options">
          <mat-checkbox
            [checked]="redundancyOn()"
            [disabled]="!canRedundancy()"
            (change)="redundancy.set($event.checked)"
          >
            Redondance de l’interface web
          </mat-checkbox>
          @if (!canRedundancy()) {
            <p class="hint">Ajoutez au moins deux nœuds serveurs pour activer la redondance.</p>
          }
          @if (proxy() !== 'npm') {
            <mat-checkbox [checked]="https()" (change)="https.set($event.checked)">
              Le proxy sert l’interface en HTTPS
            </mat-checkbox>
          }
        </div>

        @if (!backends().length) {
          <p class="hint" role="alert">Saisissez au moins une adresse de nœud.</p>
        } @else {
          <pre
            class="preview"
            tabindex="0"
            [attr.aria-label]="'Configuration ' + labels[proxy()]"
            >{{ output().text }}</pre>
          <ul class="notes">
            @for (note of output().notes; track note) {
              <li>{{ note }}</li>
            }
          </ul>
        }
      </mat-dialog-content>
      <mat-dialog-actions class="app-dialog-footer" align="end">
        <button matButton="text" type="button" mat-dialog-close>Fermer</button>
        <button
          matButton="tonal"
          type="button"
          [disabled]="!backends().length"
          (click)="download()"
        >
          Télécharger
        </button>
        <button matButton="filled" type="button" [disabled]="!backends().length" (click)="copy()">
          Copier
        </button>
      </mat-dialog-actions>
    </app-dialog-frame>
  `,
  styles: `
    mat-dialog-content {
      display: grid;
      gap: var(--space-4);
      color: var(--text-secondary);
    }
    .options {
      display: grid;
      gap: var(--space-1);
    }
    .hint {
      margin: 0;
      font-size: var(--font-body);
    }
    .preview {
      margin: 0;
      padding: var(--space-3);
      max-height: 18rem;
      overflow: auto;
      border-radius: var(--radius-md);
      background: var(--surface-container-high);
      color: var(--text-primary);
      font-family: ui-monospace, 'JetBrains Mono', Consolas, monospace;
      font-size: var(--font-label);
      line-height: 1.5;
    }
    .notes {
      margin: 0;
      padding-left: var(--space-5);
      line-height: 1.55;
    }
    mat-button-toggle-group {
      flex-wrap: wrap;
    }
  `,
})
export class ReverseProxyDialogComponent {
  private readonly clipboard = inject(ClipboardService);
  private readonly downloads = inject(FileDownload);
  private readonly notifications = inject(NotificationService);
  private readonly publicUrl = inject(PublicUrlService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly kinds: readonly ProxyKind[] = ['nginx', 'caddy', 'traefik', 'npm'];
  protected readonly labels = PROXY_LABELS;
  protected readonly defaultDomain = DEFAULT_DOMAIN;
  protected readonly proxy = signal<ProxyKind>('nginx');
  protected readonly domain = signal(
    domainOf(this.publicUrl.isDeclared() ? this.publicUrl.origin() : null) ?? '',
  );
  protected readonly backendsText = signal(window.location.host);
  protected readonly redundancy = signal(true);
  protected readonly https = signal(false);

  protected readonly backends = computed(() => parseBackends(this.backendsText()));
  protected readonly canRedundancy = computed(() => this.backends().length > 1);
  protected readonly redundancyOn = computed(() => this.canRedundancy() && this.redundancy());
  protected readonly output = computed(() =>
    generateProxyConfig({
      proxy: this.proxy(),
      domain: this.domain(),
      backends: this.backends(),
      redundancy: this.redundancyOn(),
      https: this.https() && this.proxy() !== 'npm',
    }),
  );

  constructor() {
    // Les autres serveurs du cluster, tels que ce nœud les joint : une adresse par ligne.
    inject(ClusterApi)
      .reconciliationStatus()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (status) => {
          const hosts = status.peers
            .map((peer) => peer.url)
            .filter((url) => !!url)
            .map((url) => {
              try {
                return new URL(url).host;
              } catch {
                return '';
              }
            })
            .filter((host) => host && host !== window.location.host);
          if (hosts.length) {
            this.backendsText.update((text) => [text, ...hosts].join('\n'));
          }
        },
        error: () => undefined,
      });
  }

  protected async copy(): Promise<void> {
    const ok = await this.clipboard.copy(this.output().text);
    this.notifications.show(ok ? 'Configuration copiée.' : 'Copie impossible.');
  }

  protected download(): void {
    const { text, fileName } = this.output();
    this.downloads.save(new Blob([text], { type: 'text/plain;charset=utf-8' }), fileName);
  }
}
