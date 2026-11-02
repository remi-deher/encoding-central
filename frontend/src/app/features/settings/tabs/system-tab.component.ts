import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  signal,
} from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { firstValueFrom, interval } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { ConfigurationSectionComponent } from '../../../shared/ui/configuration-section.component';
import { StatusBadgeComponent, StatusTone } from '../../../shared/ui/status-badge.component';
import {
  SettingGroupComponent,
  SettingRowComponent,
} from '../../../shared/ui/settings/setting-row.component';
import { SettingsNoticeComponent } from '../../../shared/ui/settings/settings-notice.component';
import { ClipboardService } from '../../../shared/services/clipboard.service';
import { NotificationService } from '../../../shared/services/notification.service';

interface HealthCheckEntry {
  readonly name: string;
  readonly status: 'healthy' | 'degraded' | 'unhealthy';
  readonly durationMs: number;
  readonly description?: string | null;
  readonly data?: Record<string, unknown>;
}

interface HealthDetailsResponse {
  readonly status: 'healthy' | 'degraded' | 'unhealthy';
  readonly totalDurationMs: number;
  readonly checks: readonly HealthCheckEntry[];
}

interface ClusterIdentityResponse {
  readonly id: string;
  readonly name: string;
  readonly standalone: boolean;
  readonly hasClusterToken: boolean;
}

const STATUS_ORDER = { unhealthy: 0, degraded: 1, healthy: 2 } as const;

@Component({
  selector: 'app-system-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    MatButtonModule,
    ConfigurationSectionComponent,
    SettingGroupComponent,
    SettingRowComponent,
    SettingsNoticeComponent,
    StatusBadgeComponent,
  ],
  template: `
    <app-configuration-section
      title="Santé du système"
      description="État des services sur le nœud consulté."
      icon="check"
    >
      @if (refreshError()) {
        <p role="status">
          Les dernières données sont conservées. Une nouvelle vérification est prévue.
        </p>
      }
      @if (health(); as value) {
        <div class="banner" [class]="'banner tone-' + tone(value.status)" role="status">
          <span class="dot" aria-hidden="true"></span>
          <strong>{{ healthLabel(value.status) }}</strong>
          <span class="banner-detail">{{ summary() }}</span>
          <span class="banner-spacer"></span>
          @if (lastChecked()) {
            <span class="banner-detail">Dernier contrôle : {{ lastChecked() }}</span>
          }
        </div>
        <app-setting-group>
          @for (check of checks(); track check.name) {
            <app-setting-row
              class="service"
              [class.attention]="check.status !== 'healthy'"
              [label]="formatCheckName(check.name)"
              [hint]="check.description ?? ''"
            >
              <span class="duration">{{ check.durationMs }} ms</span>
              <app-status-badge [label]="healthLabel(check.status)" [tone]="tone(check.status)" />
            </app-setting-row>
          }
        </app-setting-group>
      } @else {
        <app-settings-notice [tone]="refreshing() ? 'neutral' : 'warning'">
          {{ refreshing() ? 'Chargement des diagnostics…' : 'Diagnostics indisponibles.' }}
        </app-settings-notice>
      }
    </app-configuration-section>

    <app-configuration-section
      title="Cluster"
      description="Identité de cette installation."
      icon="server"
    >
      @if (cluster(); as value) {
        <app-setting-group>
          <app-setting-row label="Nom du cluster">
            <span>{{ value.name || 'Nœud autonome' }}</span>
          </app-setting-row>
          <app-setting-row label="Mode">
            <span>{{ value.standalone ? 'Autonome' : 'Cluster maillé' }}</span>
          </app-setting-row>
          <app-setting-row label="Identifiant">
            <code>{{ value.id }}</code>
            <button matButton="outlined" (click)="copyId(value.id)">Copier</button>
          </app-setting-row>
        </app-setting-group>
      } @else {
        <app-settings-notice tone="warning">Identité indisponible.</app-settings-notice>
      }
    </app-configuration-section>
  `,
  styles: `
    :host {
      display: contents;
    }
    .banner {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--space-3);
      padding: var(--space-3) var(--space-4);
      margin-bottom: 3px;
      border-radius: var(--radius-xl) var(--radius-xl) var(--radius-sm) var(--radius-sm);
      background: var(--success-soft);
      color: var(--success);
    }
    .banner.tone-warning {
      background: var(--warning-soft);
      color: var(--warning);
    }
    .banner.tone-danger {
      background: var(--danger-soft);
      color: var(--danger);
    }
    .banner strong {
      font-size: var(--font-title-sm, 1rem);
    }
    .banner-detail {
      font-size: var(--font-label);
    }
    .banner-spacer {
      flex: 1;
    }
    .dot {
      width: 12px;
      height: 12px;
      border-radius: 50%;
      background: currentColor;
      flex: none;
    }
    .banner + app-setting-group app-setting-row:first-child {
      border-top-left-radius: var(--radius-sm);
      border-top-right-radius: var(--radius-sm);
    }
    .service.attention {
      box-shadow: inset 4px 0 var(--warning);
    }
    .duration,
    code {
      font-family: var(--font-mono, ui-monospace, monospace);
      font-size: var(--font-label);
      color: var(--text-secondary);
      overflow-wrap: anywhere;
    }
  `,
})
export class SystemTabComponent {
  private readonly destroyRef = inject(DestroyRef);
  private readonly http = inject(HttpClient);
  protected readonly lastChecked = signal('');
  private readonly notifications = inject(NotificationService);

  protected readonly refreshing = signal(false);
  protected readonly refreshError = signal(false);
  protected readonly cluster = signal<ClusterIdentityResponse | null>(null);
  protected readonly health = signal<HealthDetailsResponse | null>(null);

  private readonly clipboard = inject(ClipboardService);

  protected readonly checks = computed(() =>
    [...(this.health()?.checks ?? [])].sort(
      (a, b) => STATUS_ORDER[a.status] - STATUS_ORDER[b.status],
    ),
  );
  protected readonly summary = computed(() => {
    const checks = this.checks();
    const attention = checks.filter((check) => check.status !== 'healthy').length;
    if (attention === 0) return `${checks.length} services contrôlés`;
    return attention === 1
      ? '1 service demande votre attention'
      : `${attention} services demandent votre attention`;
  });

  protected tone(status: string): StatusTone {
    return status === 'healthy' ? 'success' : status === 'degraded' ? 'warning' : 'danger';
  }

  protected async copyId(id: string): Promise<void> {
    this.notifications.show(
      (await this.clipboard.copy(id)) ? 'Identifiant copié.' : 'Copie impossible.',
    );
  }

  protected healthLabel(status: string): string {
    return status === 'healthy'
      ? 'Opérationnel'
      : status === 'degraded'
        ? 'Dégradé'
        : 'Indisponible';
  }

  constructor() {
    void this.refreshAll();
    interval(60_000)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => void this.refreshAll());
  }

  async refreshAll(): Promise<void> {
    if (this.refreshing()) return;
    this.refreshing.set(true);
    const outcomes = await Promise.allSettled([
      firstValueFrom(this.http.get<ClusterIdentityResponse>('/api/v1/cluster')),
      firstValueFrom(this.http.get<HealthDetailsResponse>('/health/details')),
    ]);
    const [identity, diagnostics] = outcomes;
    if (identity.status === 'fulfilled') this.cluster.set(identity.value);
    if (diagnostics.status === 'fulfilled') {
      this.health.set(diagnostics.value);
      this.lastChecked.set(new Date().toLocaleTimeString('fr-FR'));
    }
    this.refreshError.set(outcomes.some((result) => result.status === 'rejected'));
    this.refreshing.set(false);
  }

  protected formatCheckName(name: string): string {
    const map: Record<string, string> = {
      database: 'Base de données',
      migrations: 'Schéma de données',
      cluster: 'Maillage du cluster',
      'flow-backups': 'Sauvegardes de flows',
      'artifact-storage': 'Stockage temporaire',
      'backup-storage': 'Stockage des sauvegardes',
    };
    return map[name] ?? name;
  }
}
