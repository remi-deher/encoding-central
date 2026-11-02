import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { interval } from 'rxjs';
import { AiToolsService } from '../../../shared/services/ai-tools.service';
import { AiNodeQuotas, AiQuotaSnapshot } from '../../../shared/models/ai-tools';
import { ConfigurationSectionComponent } from '../../../shared/ui/configuration-section.component';

@Component({
  selector: 'app-ai-quota-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ConfigurationSectionComponent, DatePipe, DecimalPipe, MatProgressBarModule],
  template: `
    <app-configuration-section
      title="Quotas des abonnements"
      description="Limites du compte fournisseur, incluant son utilisation en dehors de l’application."
      icon="api"
    >
      <p>
        Actualisation chaque minute. Plusieurs nœuds peuvent utiliser le même compte : leurs quotas
        ne s’additionnent pas.
      </p>
      @if (error()) {
        <p role="status">{{ error() }}</p>
      }
      @for (node of nodes(); track node.nodeId) {
        <h4>{{ node.name }}</h4>
        <div class="quota-grid">
          @for (quota of node.providers; track quota.provider) {
            @let outdated = isStale(quota);
            <article [class.is-stale]="outdated">
              <header>
                <strong>{{ quota.provider === 'codex-cli' ? 'Codex' : 'Claude Code' }}</strong>
                <span>{{
                  outdated
                    ? 'Donnée ancienne'
                    : quota.state === 'available'
                      ? 'Quota observé'
                      : 'Indisponible'
                }}</span>
              </header>
              @if (quota.experimental) {
                <p>
                  Lecture expérimentale — dépend de la version de Claude Code et des autorisations
                  du compte.
                </p>
              }
              @if (quota.message) {
                <p>{{ quota.message }}</p>
              }
              @for (window of quota.windows; track window.id) {
                <div class="window">
                  <strong>{{ window.name }}</strong>
                  @if (window.remainingPercent != null) {
                    <p>
                      {{ window.remainingPercent | number: '1.0-1' }} % restant{{
                        outdated ? ' lors de la dernière observation' : ''
                      }}
                    </p>
                    @if (!outdated) {
                      <mat-progress-bar
                        mode="determinate"
                        [value]="window.remainingPercent"
                        [attr.aria-label]="window.name + ' : quota restant'"
                      />
                      @if (window.remainingPercent <= 5) {
                        <p class="critical" role="status">Quota presque épuisé</p>
                      } @else if (window.remainingPercent <= 20) {
                        <p class="warning" role="status">Quota bientôt atteint</p>
                      }
                    }
                  } @else {
                    <p>Pourcentage indisponible</p>
                  }
                  <p>
                    Réinitialisation :
                    {{
                      window.resetsAt
                        ? (window.resetsAt | date: 'dd/MM/yyyy HH:mm')
                        : 'Indisponible'
                    }}
                  </p>
                </div>
              }
              <p>
                Dernière observation :
                {{ quota.observedAt ? (quota.observedAt | date: 'dd/MM/yyyy HH:mm') : 'Aucune' }}
              </p>
            </article>
          }
        </div>
      } @empty {
        @if (!loading()) {
          <p>Aucune observation de quota disponible.</p>
        }
      }
    </app-configuration-section>
  `,
  styles: `
    header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      flex-wrap: wrap;
      gap: 16px;
    }
    .quota-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(min(100%, 280px), 1fr));
      gap: 8px;
    }
    article {
      min-width: 0;
      padding: 16px;
      background: var(--surface-container);
      border-radius: var(--radius-lg);
      overflow-wrap: anywhere;
    }
    p {
      color: var(--text-secondary);
    }
    .window {
      padding-block: 8px;
    }
    .is-stale {
      background: var(--surface-container-low);
    }
    .critical {
      color: var(--danger);
      font-weight: 700;
    }
    .warning {
      color: var(--text-primary);
      font-weight: 700;
    }
  `,
})
export class AiQuotaPanelComponent {
  private readonly api = inject(AiToolsService);
  private readonly destroyRef = inject(DestroyRef);
  private revision = 0;
  readonly refreshKey = input(0);
  readonly nodes = signal<readonly AiNodeQuotas[]>([]);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly now = signal(Date.now());

  constructor() {
    effect(() => {
      this.refreshKey();
      this.revision++;
      untracked(() => {
        this.nodes.set([]);
        this.refresh();
      });
    });
    interval(60_000)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.refresh());
  }

  isStale(quota: AiQuotaSnapshot): boolean {
    return (
      quota.state === 'stale' ||
      (!!quota.observedAt &&
        (Date.parse(quota.observedAt) + 120_000 <= this.now() ||
          quota.windows.some(
            (window) => !!window.resetsAt && Date.parse(window.resetsAt) <= this.now(),
          )))
    );
  }

  refresh(): void {
    if (this.loading()) return;
    const revision = this.revision;
    this.loading.set(true);
    this.now.set(Date.now());
    this.api
      .getQuotas()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (view) => {
          if (revision !== this.revision) {
            this.loading.set(false);
            this.refresh();
            return;
          }
          this.now.set(Date.now());
          const missing = this.nodes().filter(
            (old) => !view.nodes.some((node) => node.nodeId === old.nodeId),
          );
          this.nodes.set([
            ...view.nodes,
            ...missing.map((node) => ({
              ...node,
              providers: node.providers.map((quota) => ({
                ...quota,
                state: 'stale' as const,
                message: 'Nœud indisponible. Dernière observation conservée.',
              })),
            })),
          ]);
          this.error.set(null);
          this.loading.set(false);
        },
        error: () => {
          if (revision !== this.revision) {
            this.loading.set(false);
            this.refresh();
            return;
          }
          this.now.set(Date.now());
          this.nodes.update((nodes) =>
            nodes.map((node) => ({
              ...node,
              providers: node.providers.map((quota) => ({ ...quota, state: 'stale' as const })),
            })),
          );
          this.error.set(
            'Impossible d’actualiser les quotas. Les observations conservées peuvent être anciennes.',
          );
          this.loading.set(false);
        },
      });
  }
}
