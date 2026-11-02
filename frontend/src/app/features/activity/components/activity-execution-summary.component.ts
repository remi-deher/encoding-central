import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';
import { MatTooltipModule } from '@angular/material/tooltip';
import {
  ExecutionResponse,
  ExecutionPipelineStep,
  executionStateLabel,
} from '../../../shared/models/execution';
import {
  ACTIVITY_STATUS_LABELS,
  LibraryActivityFile,
} from '../../../shared/models/library-activity';
import { formatFileSize } from '../../../shared/models/file-size';
import { formatFullDateTime } from '../../../shared/models/date-format';
import { StatusBadgeComponent } from '../../../shared/ui/status-badge.component';
import { IconComponent } from '../../../shared/ui/icon.component';
import { NotificationService } from '../../../shared/services/notification.service';

function baseName(path: string): string {
  return path.split(/[\\/]/).pop() || path;
}

function parseSize(metadata: unknown): number | null {
  if (!metadata || typeof metadata !== 'object') return null;
  const format = (metadata as { format?: { size?: unknown } }).format;
  if (!format || format.size == null) return null;
  const num = Number(format.size);
  return Number.isFinite(num) && num > 0 ? num : null;
}

function executionDuration(execution: ExecutionResponse | null): string {
  if (!execution?.startedAt) return '—';
  const end = execution.finishedAt ? Date.parse(execution.finishedAt) : Date.now();
  const elapsed = Math.max(0, end - Date.parse(execution.startedAt));
  const seconds = Math.floor(elapsed / 1000);
  if (seconds < 60) return `${seconds} s`;
  const minutes = Math.floor(seconds / 60);
  const remainingSeconds = seconds % 60;
  if (minutes < 60) {
    return `${minutes} min ${remainingSeconds.toString().padStart(2, '0')} s`;
  }
  const hours = Math.floor(minutes / 60);
  const remainingMinutes = minutes % 60;
  return `${hours} h ${remainingMinutes} min`;
}

function stepDuration(step: ExecutionPipelineStep): string {
  if (!step.startedAt) return '—';
  const end = step.completedAt ? Date.parse(step.completedAt) : Date.now();
  const elapsed = Math.max(0, end - Date.parse(step.startedAt));
  if (elapsed < 1000) return `${elapsed} ms`;
  const seconds = Math.floor(elapsed / 1000);
  if (seconds < 60) {
    const fraction = ((elapsed % 1000) / 100).toFixed(0);
    return `${seconds}.${fraction} s`;
  }
  const minutes = Math.floor(seconds / 60);
  const remainingSec = seconds % 60;
  return `${minutes} min ${remainingSec} s`;
}

@Component({
  selector: 'app-activity-execution-summary',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule, MatButtonModule, MatTooltipModule, StatusBadgeComponent, IconComponent],
  template: `
    <div class="summary-container">
      @if (loading()) {
        <p class="summary-loading" role="status">Chargement des détails de l’exécution…</p>
      } @else {
        <!-- 1. Bannière de Détail / Diagnostic (mise en valeur spacieuse) -->
        @if (detailMessage(); as detail) {
          <div
            class="summary-detail-banner"
            [class.banner-danger]="statusTone() === 'danger'"
            [class.banner-warning]="statusTone() === 'warning'"
            [class.banner-accent]="statusTone() === 'accent'"
            role="region"
            aria-label="Détail du statut"
          >
            <div class="banner-header">
              <span class="banner-icon">
                <app-icon [name]="statusTone() === 'danger' ? 'warning' : 'info'" />
              </span>
              <span class="banner-label">Détail du statut / Diagnostic</span>
            </div>
            <div class="banner-body">
              <p class="banner-text">{{ detail }}</p>
              <button
                type="button"
                class="copy-chip-btn"
                (click)="copy(detail, 'Détail')"
                title="Copier le message de détail"
                aria-label="Copier le message de détail"
              >
                <app-icon name="copy" />
                <span>Copier</span>
              </button>
            </div>
          </div>
        }

        <!-- 2. Grille de fiches connectées M3 Expressive -->
        <div class="summary-cards-grid">
          <!-- Carte A : Fichier & Emplacement -->
          <section class="summary-card" aria-label="Informations du fichier">
            <header class="card-header">
              <span class="card-icon"><app-icon name="folder" /></span>
              <h4 class="card-title">Fichier & Emplacement</h4>
            </header>

            <div class="path-block">
              <span class="block-label">Chemin relatif</span>
              <div class="path-content">
                <span class="path-text" [title]="fullPath()">{{ fullPath() }}</span>
                <button
                  type="button"
                  class="copy-btn"
                  (click)="copy(fullPath(), 'Chemin relatif')"
                  title="Copier le chemin relatif"
                  aria-label="Copier le chemin relatif"
                >
                  <app-icon name="copy" />
                </button>
              </div>
            </div>

            <div class="card-fields-grid">
              <div class="field-item">
                <span class="field-label">Bibliothèque</span>
                <span class="field-value">{{ libraryName() }}</span>
              </div>
              <div class="field-item">
                <span class="field-label">Taille d’origine</span>
                <span class="field-value font-numeric">{{ sourceSize() }}</span>
              </div>
              <div class="field-item full-row">
                <span class="field-label">Dernière modification</span>
                <span class="field-value">{{ modifiedAtFormatted() }}</span>
              </div>
              @if (publishedPath(); as pub) {
                <div class="field-item full-row">
                  <span class="field-label">Emplacement publié</span>
                  <div class="path-content published-path">
                    <span class="path-text" [title]="pub">{{ pub }}</span>
                    <button
                      type="button"
                      class="copy-btn"
                      (click)="copy(pub, 'Emplacement publié')"
                      title="Copier l’emplacement publié"
                      aria-label="Copier l’emplacement publié"
                    >
                      <app-icon name="copy" />
                    </button>
                  </div>
                </div>
              }
            </div>
          </section>

          <!-- Carte B : Traitement & Exécution -->
          <section class="summary-card" aria-label="Traitement et exécution">
            <header class="card-header">
              <span class="card-icon"><app-icon name="play" /></span>
              <h4 class="card-title">Traitement & Exécution</h4>
            </header>

            <div class="card-fields-grid">
              <div class="field-item">
                <span class="field-label">Statut</span>
                <div class="field-value">
                  <app-status-badge [label]="statusLabel()" [tone]="statusTone()" />
                </div>
              </div>
              <div class="field-item">
                <span class="field-label">Nœud / Worker</span>
                <span class="field-value">{{ workerName() }}</span>
              </div>
              <div class="field-item full-row">
                <span class="field-label">Flow attribué</span>
                <span class="field-value flow-value">{{ flowDescription() }}</span>
              </div>
              <div class="field-item">
                <span class="field-label">Durée totale</span>
                <span class="field-value duration-value">{{ totalDuration() }}</span>
              </div>
              <div class="field-item">
                <span class="field-label">Progression</span>
                <span class="field-value font-numeric">
                  {{ progress() != null ? progress() + ' %' : '—' }}
                </span>
              </div>
            </div>

            @if (executionId(); as execId) {
              <div class="execution-id-block">
                <span class="block-label">Identifiant d’exécution</span>
                <div class="id-content">
                  <code class="id-code" [title]="execId">{{ execId }}</code>
                  <button
                    type="button"
                    class="copy-btn"
                    (click)="copy(execId, 'Identifiant d’exécution')"
                    title="Copier l’identifiant d’exécution"
                    aria-label="Copier l’identifiant d’exécution"
                  >
                    <app-icon name="copy" />
                  </button>
                </div>
              </div>
            }
          </section>
        </div>

        <!-- 3. Carte Stockage & Variation -->
        <section class="summary-card storage-card" aria-label="Stockage et variation">
          <header class="card-header">
            <span class="card-icon"><app-icon name="library" /></span>
            <h4 class="card-title">Stockage & Variation</h4>
          </header>

          <div class="storage-comparison">
            <div class="storage-metric">
              <span class="metric-label">Taille source</span>
              <span class="metric-value font-numeric">{{ sourceSize() }}</span>
            </div>
            <div class="storage-arrow" aria-hidden="true">→</div>
            <div class="storage-metric">
              <span class="metric-label">Taille finale</span>
              <span class="metric-value font-numeric">{{ finalSize() }}</span>
            </div>
            <div class="storage-separator" aria-hidden="true"></div>
            <div class="storage-metric storage-metric-gain">
              <span class="metric-label">Variation</span>
              @if (sizeDifference(); as diff) {
                <span
                  class="diff-badge"
                  [class.diff-reduced]="diff.reduced"
                  [class.diff-expanded]="diff.expanded"
                  [class.diff-neutral]="diff.neutral"
                >
                  {{ diff.label }} ({{ diff.percent }})
                </span>
              } @else {
                <span class="metric-value text-muted">—</span>
              }
            </div>
          </div>
        </section>

        <!-- 4. Section : Éléments de Flow exécutés -->
        <section class="summary-steps-section" aria-label="Éléments de Flow exécutés">
          <div class="steps-header">
            <h3>Éléments de Flow exécutés</h3>
            <span class="steps-count-badge">{{ steps().length }}</span>
          </div>

          @if (steps().length === 0) {
            <p class="steps-empty">Aucun élément de flow exécuté pour le moment.</p>
          } @else {
            <div class="steps-table-wrapper">
              <table class="steps-table">
                <thead>
                  <tr>
                    <th class="col-num" scope="col">#</th>
                    <th class="col-node" scope="col">Étape</th>
                    <th class="col-output" scope="col">Sortie / Décision</th>
                    <th class="col-worker" scope="col">Worker</th>
                    <th class="col-duration" scope="col">Durée</th>
                    <th class="col-status" scope="col">Statut</th>
                  </tr>
                </thead>
                <tbody>
                  @for (
                    step of steps();
                    track step.invocationId || step.nodeId + $index;
                    let idx = $index
                  ) {
                    <tr [class.is-failed]="step.status === 'failed'">
                      <td class="col-num">{{ idx + 1 }}</td>
                      <td class="col-node">
                        <div class="node-cell">
                          <span class="node-name">{{ step.nodeName || step.nodeId }}</span>
                          <span class="node-kind">{{ kindLabel(step.componentKind) }}</span>
                        </div>
                      </td>
                      <td class="col-output">
                        @if (step.outputLabel) {
                          <span class="output-pill">{{ step.outputLabel }}</span>
                        } @else if (step.outputId) {
                          <span class="output-pill">{{ step.outputId }}</span>
                        } @else {
                          <span class="text-muted">—</span>
                        }
                      </td>
                      <td class="col-worker">{{ workerName() }}</td>
                      <td class="col-duration">{{ formatStepDuration(step) }}</td>
                      <td class="col-status">
                        <app-status-badge
                          [label]="stepStatusLabel(step.status)"
                          [tone]="stepStatusTone(step.status)"
                        />
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        </section>
      }
    </div>
  `,
  styles: `
    :host {
      display: block;
      width: 100%;
    }

    .summary-container {
      display: flex;
      flex-direction: column;
      gap: var(--space-4);
      padding: var(--space-1) 0;
    }

    .summary-loading {
      color: var(--text-secondary);
      font-size: var(--font-body);
      margin: var(--space-3) 0;
    }

    /* 1. Bannière de Détail / Diagnostic */
    .summary-detail-banner {
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
      padding: var(--space-3) var(--space-4);
      border-radius: var(--radius-xl);
      background: var(--surface-container-high);
      border-inline-start: 4px solid var(--accent);
      box-shadow: var(--elevation-1);
    }

    .summary-detail-banner.banner-danger {
      background: var(--danger-soft);
      border-inline-start-color: var(--danger);
      .banner-header {
        color: var(--danger);
      }
    }

    .summary-detail-banner.banner-warning {
      background: var(--warning-soft);
      border-inline-start-color: var(--warning);
      .banner-header {
        color: var(--warning);
      }
    }

    .summary-detail-banner.banner-accent {
      background: var(--accent-soft);
      border-inline-start-color: var(--accent);
      .banner-header {
        color: var(--accent-strong);
      }
    }

    .banner-header {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      font-size: var(--font-label);
      font-weight: 700;
      text-transform: uppercase;
      letter-spacing: 0.05em;
      color: var(--text-muted);
    }

    .banner-icon {
      display: inline-flex;
      width: 18px;
      height: 18px;
    }

    .banner-body {
      display: flex;
      align-items: flex-start;
      justify-content: space-between;
      gap: var(--space-3);
    }

    .banner-text {
      margin: 0;
      color: var(--text-primary);
      font-size: var(--font-body);
      line-height: 1.5;
      font-weight: 500;
      overflow-wrap: anywhere;
      word-break: break-word;
    }

    .copy-chip-btn {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      padding: 4px 10px;
      border: 1px solid var(--border-subtle);
      border-radius: var(--radius-pill);
      background: var(--surface-container-lowest);
      color: var(--text-secondary);
      font-size: var(--font-label);
      font-weight: 600;
      cursor: pointer;
      flex-shrink: 0;
      transition: background-color var(--motion-spring-fast);

      &:hover {
        background: var(--surface-container-highest);
        color: var(--accent-strong);
      }

      app-icon {
        width: 14px;
        height: 14px;
      }
    }

    /* 2. Grille de cartes connectées M3 */
    .summary-cards-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(320px, 1fr));
      gap: var(--space-4);
    }

    .summary-card {
      display: flex;
      flex-direction: column;
      gap: var(--space-3);
      padding: var(--space-4);
      border-radius: var(--radius-xl);
      background: var(--surface-container);
      box-shadow: var(--elevation-1);
      transition: background-color var(--motion-spring-fast);

      &:hover {
        background: var(--surface-container-high);
      }
    }

    .card-header {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      color: var(--accent-strong);
    }

    .card-icon {
      display: grid;
      place-items: center;
      width: 28px;
      height: 28px;
      border-radius: var(--radius-md);
      background: var(--surface-container-lowest);
      color: var(--accent);
    }

    .card-title {
      margin: 0;
      color: var(--text-primary);
      font-size: var(--font-body);
      font-weight: 700;
      letter-spacing: 0.02em;
    }

    .path-block,
    .execution-id-block {
      display: flex;
      flex-direction: column;
      gap: 4px;
      padding: var(--space-2) var(--space-3);
      border-radius: var(--radius-lg);
      background: var(--surface-container-lowest);
      border: 1px solid var(--border-subtle);
    }

    .block-label {
      font-size: 11px;
      font-weight: 700;
      letter-spacing: 0.06em;
      text-transform: uppercase;
      color: var(--text-muted);
    }

    .path-content,
    .id-content {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-2);
    }

    .path-text {
      font-size: var(--font-body-sm);
      font-weight: 600;
      color: var(--text-primary);
      overflow-wrap: anywhere;
      word-break: break-word;
      line-height: 1.4;
    }

    .id-code {
      font-family: monospace;
      font-size: var(--font-label);
      color: var(--text-secondary);
      overflow-wrap: anywhere;
      word-break: break-all;
    }

    .copy-btn {
      display: grid;
      place-items: center;
      width: 28px;
      height: 28px;
      border: 0;
      background: transparent;
      color: var(--text-muted);
      border-radius: var(--radius-xs);
      cursor: pointer;
      flex: 0 0 28px;

      &:hover {
        color: var(--accent-strong);
        background: var(--surface-container-highest);
      }
    }

    .card-fields-grid {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: var(--space-3);
    }

    .field-item {
      display: flex;
      flex-direction: column;
      gap: 2px;
      min-width: 0;

      &.full-row {
        grid-column: 1 / -1;
      }
    }

    .field-label {
      font-size: var(--font-label);
      color: var(--text-muted);
      font-weight: 500;
    }

    .field-value {
      font-size: var(--font-body-sm);
      color: var(--text-primary);
      font-weight: 600;
      overflow-wrap: anywhere;
    }

    .flow-value {
      line-height: 1.35;
    }

    .duration-value {
      font-variant-numeric: tabular-nums;
      color: var(--accent-strong);
      font-weight: 700;
    }

    .font-numeric {
      font-variant-numeric: tabular-nums;
    }

    /* 3. Carte Stockage & Variation */
    .storage-card {
      padding: var(--space-4);
    }

    .storage-comparison {
      display: flex;
      align-items: center;
      flex-wrap: wrap;
      gap: var(--space-3);
      padding: var(--space-2) 0 0;
    }

    .storage-metric {
      display: flex;
      flex-direction: column;
      gap: 2px;
    }

    .metric-label {
      font-size: var(--font-label);
      color: var(--text-muted);
      font-weight: 500;
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }

    .metric-value {
      font-size: var(--font-title-sm);
      font-weight: 700;
      color: var(--text-primary);
    }

    .storage-arrow {
      color: var(--accent-strong);
      font-size: 20px;
      font-weight: 700;
    }

    .storage-separator {
      width: 1px;
      height: 36px;
      background: var(--border-subtle);
      margin: 0 var(--space-2);
    }

    .diff-badge {
      display: inline-flex;
      align-items: center;
      padding: 4px 10px;
      border-radius: var(--radius-pill);
      font-size: var(--font-label);
      font-weight: 700;
      font-variant-numeric: tabular-nums;
    }

    .diff-reduced {
      background: var(--success-soft);
      color: var(--success);
    }

    .diff-expanded {
      background: var(--danger-soft);
      color: var(--danger);
    }

    .diff-neutral {
      background: var(--surface-container-highest);
      color: var(--text-secondary);
    }

    .text-muted {
      color: var(--text-muted);
    }

    /* 4. Tableau des étapes de flow exécutées */
    .summary-steps-section {
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
      margin-top: var(--space-2);
    }

    .steps-header {
      display: flex;
      align-items: center;
      gap: var(--space-2);

      h3 {
        margin: 0;
        font-size: var(--font-body);
        font-weight: 700;
        color: var(--text-primary);
      }
    }

    .steps-count-badge {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      min-width: 22px;
      height: 22px;
      padding: 0 6px;
      border-radius: var(--radius-pill);
      background: var(--surface-container);
      color: var(--text-secondary);
      font-size: var(--font-label);
      font-weight: 700;
    }

    .steps-empty {
      color: var(--text-muted);
      font-size: var(--font-label);
      padding: var(--space-3);
      border-radius: var(--radius-md);
      background: var(--surface-container-low);
    }

    .steps-table-wrapper {
      overflow-x: auto;
      border-radius: var(--radius-lg);
      border: 1px solid var(--border-subtle);
      background: var(--surface-container-lowest);
    }

    .steps-table {
      width: 100%;
      border-collapse: collapse;
      font-size: var(--font-label);
      text-align: start;

      th {
        padding: var(--space-2) var(--space-3);
        background: var(--surface-container);
        color: var(--text-muted);
        font-weight: 700;
        letter-spacing: 0.04em;
        text-transform: uppercase;
        border-bottom: 1px solid var(--border-subtle);
        white-space: nowrap;
      }

      td {
        padding: var(--space-2) var(--space-3);
        border-bottom: 1px solid var(--border-subtle);
        vertical-align: middle;
      }

      tr:last-child td {
        border-bottom: 0;
      }

      tbody tr:hover {
        background: var(--surface-container-low);
      }

      tbody tr.is-failed {
        background: color-mix(in srgb, var(--danger-soft) 40%, transparent);
      }
    }

    .col-num {
      width: 44px;
      text-align: center;
      color: var(--text-muted);
      font-variant-numeric: tabular-nums;
    }

    .node-cell {
      display: flex;
      flex-direction: column;
      gap: 2px;
    }

    .node-name {
      font-weight: 600;
      color: var(--text-primary);
    }

    .node-kind {
      font-size: 0.78rem;
      color: var(--text-muted);
    }

    .col-output {
      min-width: 120px;
    }

    .output-pill {
      display: inline-flex;
      align-items: center;
      padding: 2px 8px;
      border-radius: var(--radius-pill);
      background: var(--surface-container);
      color: var(--text-secondary);
      font-weight: 600;
      font-size: 0.8rem;
    }

    .col-worker {
      color: var(--text-secondary);
      white-space: nowrap;
    }

    .col-duration {
      font-variant-numeric: tabular-nums;
      color: var(--text-primary);
      white-space: nowrap;
      font-weight: 500;
    }

    .col-status {
      width: 120px;
    }

    @media (max-width: 700px) {
      .summary-cards-grid {
        grid-template-columns: 1fr;
      }

      .card-fields-grid {
        grid-template-columns: 1fr;
      }

      .storage-separator {
        display: none;
      }
    }
  `,
})
export class ActivityExecutionSummaryComponent {
  private readonly notifications = inject(NotificationService);

  readonly execution = input<ExecutionResponse | null>(null);
  readonly file = input<LibraryActivityFile | null>(null);
  readonly steps = input<readonly ExecutionPipelineStep[]>([]);
  readonly loading = input<boolean>(false);

  protected readonly baseName = baseName;
  protected readonly executionStateLabel = executionStateLabel;

  protected readonly fileName = computed(() => {
    const media = this.execution()?.media;
    if (media) return baseName(media);
    const rel = this.file()?.relativePath;
    return rel ? baseName(rel) : '—';
  });

  protected readonly fullPath = computed(() => {
    return this.execution()?.media || this.file()?.relativePath || '';
  });

  protected readonly libraryName = computed(() => {
    return this.execution()?.library || this.file()?.library || '—';
  });

  protected readonly modifiedAtFormatted = computed(() => {
    const date = this.file()?.modifiedAt;
    return date ? formatFullDateTime(date) : '—';
  });

  protected readonly detailMessage = computed(() => {
    const fileDetail = this.file()?.detail?.trim();
    const execDetail = this.execution()?.detail?.trim();
    return fileDetail || execDetail || null;
  });

  protected readonly statusLabel = computed(() => {
    const exec = this.execution();
    if (exec) {
      return executionStateLabel(exec);
    }
    const st = this.file()?.status;
    return st && ACTIVITY_STATUS_LABELS[st] ? ACTIVITY_STATUS_LABELS[st] : 'À traiter';
  });

  protected readonly statusTone = computed<'accent' | 'neutral' | 'danger' | 'success' | 'warning'>(
    () => {
      const exec = this.execution();
      if (exec) {
        return this.stateTone(exec.state);
      }
      const st = this.file()?.status;
      if (st === 'failed') return 'danger';
      if (st === 'done') return 'success';
      if (st === 'active') return 'accent';
      if (st === 'disabled') return 'warning';
      return 'neutral';
    },
  );

  protected readonly flowDescription = computed(() => {
    const exec = this.execution();
    if (exec?.flow) {
      return `${exec.flow} · v${exec.flowVersion}`;
    }
    const f = this.file();
    if (f?.flow) {
      return `${f.flow} · v${f.flowVersion}`;
    }
    return 'Aucun flow attribué';
  });

  protected readonly workerName = computed(() => {
    return this.execution()?.node || this.file()?.node || '—';
  });

  protected readonly executionId = computed(() => {
    return this.execution()?.id || this.file()?.executionId || null;
  });

  protected readonly progress = computed(() => {
    const p = this.execution()?.progress ?? this.file()?.progress;
    return p != null ? p : null;
  });

  protected readonly publishedPath = computed(() => this.execution()?.publishedPath ?? null);

  protected readonly totalDuration = computed(() => {
    return executionDuration(this.execution());
  });

  protected readonly sourceSize = computed(() => {
    const fromMeta = parseSize(this.execution()?.beforeMetadata);
    if (fromMeta != null) return formatFileSize(fromMeta);
    const fileBytes = this.file()?.bytes;
    if (fileBytes != null && fileBytes > 0) return formatFileSize(fileBytes);
    return '—';
  });

  protected readonly finalSize = computed(() => {
    const fromMeta = parseSize(this.execution()?.afterMetadata);
    if (fromMeta != null) return formatFileSize(fromMeta);
    return '—';
  });

  protected readonly sizeDifference = computed(() => {
    const source = parseSize(this.execution()?.beforeMetadata) ?? this.file()?.bytes ?? null;
    const final = parseSize(this.execution()?.afterMetadata);
    if (source == null || final == null || source <= 0) return null;
    const diff = final - source;
    const percentNum = (diff / source) * 100;
    const sign = diff > 0 ? '+' : '';
    const percentStr = `${sign}${percentNum.toFixed(1)} %`;
    const labelStr = `${sign}${formatFileSize(diff)}`;
    return {
      diff,
      percent: percentStr,
      label: labelStr,
      reduced: diff < 0,
      expanded: diff > 0,
      neutral: diff === 0,
    };
  });

  protected formatStepDuration(step: ExecutionPipelineStep): string {
    return stepDuration(step);
  }

  protected kindLabel(kind: string): string {
    return (
      {
        condition: 'Condition',
        input: 'Entrée',
        terminal: 'Sortie',
        execution: 'Traitement',
        preparation: 'Préparation',
        action: 'Action',
        variable: 'Variable',
        script: 'Script',
        unknown: 'Bloc',
      }[kind] ?? kind
    );
  }

  protected stateTone(
    state: ExecutionResponse['state'],
  ): 'accent' | 'neutral' | 'danger' | 'success' | 'warning' {
    switch (state) {
      case 'succeeded':
        return 'success';
      case 'running':
        return 'accent';
      case 'failed':
        return 'danger';
      case 'cancelled':
      case 'cancelling':
        return 'warning';
      default:
        return 'neutral';
    }
  }

  protected stepStatusTone(
    status: ExecutionPipelineStep['status'],
  ): 'accent' | 'neutral' | 'danger' | 'success' | 'warning' {
    switch (status) {
      case 'completed':
        return 'success';
      case 'failed':
        return 'danger';
      case 'cancelled':
      case 'cancelling':
        return 'warning';
      case 'running':
        return 'accent';
      default:
        return 'neutral';
    }
  }

  protected stepStatusLabel(status: ExecutionPipelineStep['status']): string {
    switch (status) {
      case 'completed':
        return 'Terminé';
      case 'failed':
        return 'Échec';
      case 'cancelled':
        return 'Annulé';
      case 'cancelling':
        return 'Annulation';
      case 'running':
        return 'En cours';
      default:
        return status;
    }
  }

  protected copy(text: string, label: string): void {
    if (!text) return;
    void navigator.clipboard.writeText(text).then(
      () => this.notifications.show(`${label} copié dans le presse-papiers.`),
      () => this.notifications.show(`Impossible de copier dans le presse-papiers.`),
    );
  }
}
