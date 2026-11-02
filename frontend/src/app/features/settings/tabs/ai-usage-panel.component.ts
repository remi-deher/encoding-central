import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  inject,
  input,
  effect,
  signal,
  untracked,
} from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { AiToolsService } from '../../../shared/services/ai-tools.service';
import { AiClusterUsage, AiUsageTotals } from '../../../shared/models/ai-tools';
import { aiTypeLabel } from '../../ai/components/ai-model-selection';
import { ConfigurationSectionComponent } from '../../../shared/ui/configuration-section.component';

/** Tokens rapportés par les fournisseurs, par nœud exécutant et au global ; jamais estimés. */
@Component({
  selector: 'app-ai-usage-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ConfigurationSectionComponent, DecimalPipe, MatButtonModule],
  template: `
    <app-configuration-section
      title="Consommation de tokens"
      description="Tokens rapportés par les fournisseurs, regroupés par nœud qui a exécuté la demande."
      icon="api"
    >
      <div section-actions>
        <div class="periods" role="group" aria-label="Période">
          @for (option of periods; track option) {
            <button
              matButton="tonal"
              type="button"
              [disabled]="loading()"
              [attr.aria-pressed]="days() === option"
              [class.selected]="days() === option"
              (click)="select(option)"
            >
              {{ option }} jours
            </button>
          }
        </div>
      </div>
      @if (error()) {
        <p role="status">{{ error() }}</p>
      }
      @if (usage(); as view) {
        <div class="total">
          <strong>Total du cluster</strong>
          <span>{{ view.total.inputTokens | number }} tokens en entrée</span>
          <span>{{ view.total.outputTokens | number }} en sortie</span>
          @if (view.total.cachedInputTokens) {
            <span>{{ view.total.cachedInputTokens | number }} en cache</span>
          }
          <span>{{ view.total.turns | number }} échange{{ view.total.turns > 1 ? 's' : '' }}</span>
          @if (view.total.costUsd != null) {
            <span>≈ {{ view.total.costUsd | number: '1.2-2' }} $ rapportés</span>
          }
        </div>
        @if (view.total.turnsWithoutUsage) {
          <p>
            {{ view.total.turnsWithoutUsage }} échange{{
              view.total.turnsWithoutUsage > 1 ? 's' : ''
            }}
            sans consommation rapportée par le fournisseur (non comptés).
          </p>
        }
        @if (view.truncated) {
          <p role="status">Seules les sessions les plus récentes sont comptées.</p>
        }
        <div class="grid">
          @for (node of view.nodes; track node.nodeId ?? node.name) {
            <article>
              <h4>{{ node.name }}</h4>
              <p class="figures">{{ line(node.total) }}</p>
              <ul>
                @for (provider of node.providers; track provider.provider) {
                  <li>
                    <span>{{ name(provider.provider) }}</span>
                    <span>{{ line(provider.totals) }}</span>
                  </li>
                }
              </ul>
            </article>
          } @empty {
            <p>Aucune consommation sur cette période.</p>
          }
        </div>
      } @else if (loading()) {
        <p role="status">Calcul de la consommation…</p>
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
    .periods {
      display: flex;
      gap: 8px;
    }
    .selected {
      outline: 2px solid var(--text-primary);
    }
    p {
      color: var(--text-secondary);
    }
    h4,
    p.figures {
      margin: 0;
    }
    .total {
      display: flex;
      flex-wrap: wrap;
      gap: 8px 16px;
      align-items: baseline;
      padding: 12px 16px;
      background: var(--surface-container-high);
      border-radius: var(--radius-lg);
      margin-block: 8px;
    }
    .grid {
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
    ul {
      margin: 8px 0 0;
      padding: 0;
      list-style: none;
    }
    li {
      display: flex;
      justify-content: space-between;
      gap: 12px;
      padding-block: 2px;
      color: var(--text-secondary);
    }
  `,
})
export class AiUsagePanelComponent {
  private readonly api = inject(AiToolsService);
  private readonly destroyRef = inject(DestroyRef);
  private request = 0;
  readonly refreshKey = input(0);
  readonly periods = [7, 30, 90] as const;
  readonly days = signal<number>(30);
  readonly usage = signal<AiClusterUsage | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  constructor() {
    effect(() => {
      this.refreshKey();
      untracked(() => this.load());
    });
  }

  select(days: number): void {
    this.days.set(days);
    this.load();
  }

  name(provider: string): string {
    return aiTypeLabel(provider);
  }

  line(totals: AiUsageTotals): string {
    const format = (value: number) => new Intl.NumberFormat('fr-FR').format(value);
    return `${format(totals.inputTokens)} entrée · ${format(totals.outputTokens)} sortie · ${format(totals.turns)} échange${totals.turns > 1 ? 's' : ''}`;
  }

  private load(): void {
    const request = ++this.request;
    this.loading.set(true);
    this.error.set(null);
    this.api
      .getUsage(this.days())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (usage) => {
          if (request !== this.request) return;
          this.usage.set(usage);
          this.loading.set(false);
        },
        error: () => {
          if (request !== this.request) return;
          this.error.set('Consommation indisponible pour le moment.');
          this.loading.set(false);
        },
      });
  }
}
