import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  inject,
  signal,
} from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { interval } from 'rxjs';
import { AiToolsService } from '../../../shared/services/ai-tools.service';
import { NotificationService } from '../../../shared/services/notification.service';
import { AiSessionPage, AiSessionState, AiSessionSummary } from '../../../shared/models/ai-tools';
import { ConfigurationSectionComponent } from '../../../shared/ui/configuration-section.component';

@Component({
  selector: 'app-ai-session-history',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ConfigurationSectionComponent, DatePipe, DecimalPipe, MatButtonModule],
  template: `
    <app-configuration-section
      title="Sessions et traitements IA"
      description="Historique de l’application, indépendant des conversations web des fournisseurs."
      icon="terminal"
    >
      @if (page(); as result) {
        <p>
          Mise à jour automatique toutes les 30 secondes. Les changements des autres nœuds peuvent
          apparaître avec un délai.
        </p>
        @if (error()) {
          <p role="status">
            Les données précédentes sont conservées. La vérification sera retentée automatiquement.
          </p>
        }
        @for (session of result.items; track session.id) {
          <article>
            <header>
              <div>
                <strong>{{ session.provider }}</strong> · {{ statusLabel(session.status) }}
                <p>
                  {{
                    session.updatedAt ? (session.updatedAt | date: 'short') : 'Date indisponible'
                  }}
                </p>
              </div>
              <button
                matButton="text"
                type="button"
                [disabled]="loadingDetail()"
                (click)="inspect(session)"
              >
                {{
                  selected()?.id === session.id ? 'Fermer les échanges' : 'Consulter les échanges'
                }}
              </button>
            </header>
            <p>{{ session.turnCount }} échange(s) validé(s) · {{ session.attempt }} tentative(s)</p>
            @if (session.scope; as scope) {
              <p>{{ operationLabel(scope.operation) }}</p>
              @if (scope.libraryId) {
                <p>Bibliothèque : {{ scope.libraryId }}</p>
              }
              @if (scope.flowId) {
                <p>Flow : {{ scope.flowId }}</p>
              }
              @if (scope.mediaId) {
                <p>Média : {{ scope.mediaId }}</p>
              }
            }
            <p>Modèle : {{ session.model || 'Choix du fournisseur' }}</p>
            <p>
              Jetons d’entrée : {{ session.usage?.inputTokens ?? 'Indisponible' }} · Jetons de
              sortie : {{ session.usage?.outputTokens ?? 'Indisponible' }}
            </p>
            @if (session.usage?.estimatedCostUsd != null) {
              <p>Coût estimé : {{ session.usage?.estimatedCostUsd | number: '1.2-6' }} USD</p>
            }
            @if (session.lastError) {
              <p role="status">{{ session.lastError }}</p>
            }
            @if (selected(); as detail) {
              @if (detail.id === session.id) {
                <div class="exchanges">
                  <h4>Consignes</h4>
                  <pre>{{ detail.systemPrompt }}</pre>
                  @for (turn of detail.turns; track turn.requestId) {
                    <h4>Demande</h4>
                    <pre>{{ turn.userPrompt }}</pre>
                    <h4>Réponse</h4>
                    <pre>{{ turn.content }}</pre>
                  } @empty {
                    <p>Aucun échange validé.</p>
                  }
                </div>
              }
            }
          </article>
        } @empty {
          <p>Aucune session enregistrée sur ce nœud.</p>
        }
        <nav aria-label="Pages de sessions IA">
          <button
            matButton="text"
            type="button"
            [disabled]="loading() || result.offset === 0"
            (click)="refresh(result.offset - 50)"
          >
            Précédent
          </button>
          <button
            matButton="text"
            type="button"
            [disabled]="loading() || !result.hasMore"
            (click)="refresh(result.offset + 50)"
          >
            Suivant
          </button>
        </nav>
        <p>
          Consommation des échanges validés uniquement. Des tentatives interrompues peuvent aussi
          consommer du quota.
        </p>
      }
    </app-configuration-section>
  `,
  styles: `
    header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 16px;
      flex-wrap: wrap;
    }
    article {
      overflow-wrap: anywhere;
      margin-block: 4px;
      padding: 16px;
      background: var(--surface-container);
      border-radius: var(--radius-lg);
    }
    p {
      color: var(--text-secondary);
    }
    pre {
      white-space: pre-wrap;
      overflow-wrap: anywhere;
      font-size: var(--font-label);
    }
    .exchanges {
      padding: 16px;
      background: var(--surface-container-high);
      border-radius: var(--radius-md);
    }
    nav {
      display: flex;
      justify-content: flex-end;
    }
  `,
})
export class AiSessionHistoryComponent implements OnInit {
  private readonly destroyRef = inject(DestroyRef);
  private readonly api = inject(AiToolsService);
  private readonly notifications = inject(NotificationService);
  readonly page = signal<AiSessionPage | null>(null);
  readonly selected = signal<AiSessionState | null>(null);
  readonly loading = signal(false);
  readonly loadingDetail = signal(false);
  readonly error = signal(false);

  ngOnInit(): void {
    this.refresh();
    interval(30_000)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.refresh(this.page()?.offset ?? 0, true));
  }

  refresh(offset = this.page()?.offset ?? 0, silent = false): void {
    if (this.loading()) return;
    this.loading.set(true);
    this.api.listSessions(offset).subscribe({
      next: (page) => {
        this.page.set(page);
        this.error.set(false);
        this.loading.set(false);
      },
      error: () => {
        this.error.set(true);
        this.loading.set(false);
        if (!silent) this.notifications.show('Impossible de charger l’historique IA.');
      },
    });
  }

  inspect(session: AiSessionSummary): void {
    if (this.loadingDetail()) return;
    if (this.selected()?.id === session.id) {
      this.selected.set(null);
      return;
    }
    this.loadingDetail.set(true);
    this.api.getSession(session.id).subscribe({
      next: (detail) => {
        this.selected.set(detail);
        this.loadingDetail.set(false);
      },
      error: () => {
        this.loadingDetail.set(false);
        this.notifications.show('Historique de cette session indisponible.');
      },
    });
  }

  operationLabel(operation: string): string {
    return (
      (
        {
          'flow-create': 'Création de flow',
          'flow-edit': 'Modification de flow',
          'flow-explain': 'Explication de flow',
        } as Record<string, string>
      )[operation] ?? operation
    );
  }

  statusLabel(status: AiSessionSummary['status']): string {
    return {
      completed: 'Terminé',
      running: 'Tentative en cours ou bail actif',
      awaitingRetry: 'En attente de reprise',
    }[status];
  }
}
