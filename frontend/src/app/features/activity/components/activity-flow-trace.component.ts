import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { of, catchError, firstValueFrom } from 'rxjs';
import { ExecutionPipelineStep, ExecutionState } from '../../../shared/models/execution';
import { FlowDocument, FlowPluginCatalog } from '../../../shared/models/flow-validation';
import { flowDocumentNodes } from '../../../shared/models/flow-document-graph';
import { layoutFlowCanvas } from '../../../shared/models/flow-canvas';
import { FlowGraphConnection, FlowGraphNode } from '../../../shared/models/flow-component';
import { FlowCanvasComponent } from '../../../shared/ui/flow-canvas/flow-canvas.component';
import { ExecutionTracking } from '../../../shared/services/execution-tracking';
import { FlowValidation } from '../../../shared/services/flow-validation';
import { StatusBadgeComponent } from '../../../shared/ui/status-badge.component';

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
  selector: 'app-activity-flow-trace',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule, FlowCanvasComponent, StatusBadgeComponent],
  template: `
    <div class="flow-trace-container">
      @if (loading()) {
        <div class="trace-state-msg" role="status">
          <p>Chargement du graphe d’exécution…</p>
        </div>
      } @else if (error()) {
        <div class="trace-state-msg error" role="alert">
          <p>{{ error() }}</p>
        </div>
      } @else if (!flowDocument()) {
        <div class="trace-state-msg">
          <p>La définition du flow n'est pas disponible pour cette exécution.</p>
        </div>
      } @else {
        <!-- Flow canvas with highlighted executed trace -->
        <div class="canvas-wrapper">
          <app-flow-canvas
            [readOnly]="true"
            [nodes]="nodes()"
            [connections]="connections()"
            [executedNodeIds]="executedNodeIds()"
            [executedConnectionIds]="executedConnectionIds()"
            [nodeExecutionDetails]="nodeExecutionDetails()"
            [selectedNodeId]="selectedNodeId()"
            (selectNode)="onNodeSelected($event)"
          />
        </div>

        <!-- Selected Node Inspector Footer -->
        @if (selectedStep(); as step) {
          <aside class="node-inspector-panel" aria-label="Détails du bloc sélectionné">
            <div class="inspector-header">
              <span class="inspector-title">
                <strong>{{ step.nodeName || step.nodeId }}</strong>
                <span class="inspector-kind">({{ step.componentKind }})</span>
              </span>
              <button
                type="button"
                class="inspector-close"
                (click)="selectedNodeId.set(null)"
                aria-label="Fermer les détails du bloc"
              >
                ×
              </button>
            </div>
            <div class="inspector-details">
              <div class="detail-item">
                <span class="detail-lbl">Statut :</span>
                <app-status-badge
                  [label]="step.status"
                  [tone]="
                    step.status === 'completed'
                      ? 'success'
                      : step.status === 'failed'
                        ? 'danger'
                        : 'neutral'
                  "
                />
              </div>
              <div class="detail-item">
                <span class="detail-lbl">Durée :</span>
                <span class="detail-val font-numeric">{{ formatDuration(step) }}</span>
              </div>
              @if (step.outputLabel || step.outputId) {
                <div class="detail-item">
                  <span class="detail-lbl">Sortie empruntée :</span>
                  <span class="output-tag">{{ step.outputLabel || step.outputId }}</span>
                </div>
              }
              <div class="detail-item">
                <span class="detail-lbl">Commandes :</span>
                <span class="detail-val">{{ step.commands.length }} exécutée(s)</span>
              </div>
              @if (step.diagnostic; as diag) {
                <div class="detail-item full-width diagnostic-alert">
                  <span class="diag-code">[{{ diag.code }}]</span>
                  <span class="diag-msg">{{ diag.message }}</span>
                </div>
              }
            </div>
          </aside>
        }
      }
    </div>
  `,
  styles: `
    :host {
      display: block;
      width: 100%;
      height: 100%;
      min-height: 520px;
    }

    .flow-trace-container {
      display: flex;
      flex-direction: column;
      width: 100%;
      height: 100%;
      min-height: 520px;
      position: relative;
    }

    .trace-state-msg {
      padding: var(--space-4);
      color: var(--text-secondary);
      font-size: var(--font-body);

      &.error {
        color: var(--danger);
      }
    }

    .canvas-wrapper {
      flex: 1;
      min-height: 480px;
      height: 100%;
      position: relative;
      border-radius: var(--radius-lg);
      overflow: hidden;
      border: 1px solid var(--border-subtle);
      background: var(--surface-container-lowest);
    }

    .node-inspector-panel {
      position: absolute;
      bottom: var(--space-3);
      left: var(--space-3);
      right: var(--space-3);
      max-width: 600px;
      padding: var(--space-3);
      border-radius: var(--radius-lg);
      background: var(--surface-container-high);
      border: 1px solid var(--border-subtle);
      box-shadow: var(--elevation-3);
      z-index: 10;
      animation: slideUp 0.18s cubic-bezier(0, 0, 0.2, 1);
    }

    @keyframes slideUp {
      from {
        opacity: 0;
        transform: translateY(8px);
      }
      to {
        opacity: 1;
        transform: translateY(0);
      }
    }

    .inspector-header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      margin-bottom: var(--space-2);
    }

    .inspector-title {
      display: flex;
      align-items: baseline;
      gap: var(--space-2);
      font-size: var(--font-body);
      color: var(--text-primary);
    }

    .inspector-kind {
      font-size: 0.78rem;
      color: var(--text-muted);
    }

    .inspector-close {
      border: 0;
      background: transparent;
      font-size: 1.25rem;
      line-height: 1;
      color: var(--text-muted);
      cursor: pointer;
      padding: 0 4px;

      &:hover {
        color: var(--text-primary);
      }
    }

    .inspector-details {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--space-3);
      font-size: var(--font-label);
    }

    .detail-item {
      display: flex;
      align-items: center;
      gap: var(--space-1);

      &.full-width {
        width: 100%;
      }
    }

    .detail-lbl {
      color: var(--text-muted);
    }

    .detail-val {
      color: var(--text-primary);
      font-weight: 600;
    }

    .output-tag {
      display: inline-flex;
      align-items: center;
      padding: 1px 8px;
      border-radius: var(--radius-pill);
      background: var(--surface-container-highest);
      color: var(--text-primary);
      font-weight: 700;
    }

    .font-numeric {
      font-variant-numeric: tabular-nums;
    }

    .diagnostic-alert {
      padding: var(--space-2);
      border-radius: var(--radius-sm);
      background: var(--danger-soft);
      color: var(--danger);
      gap: var(--space-1);
    }

    .diag-code {
      font-weight: 700;
    }
  `,
})
export class ActivityFlowTraceComponent {
  private readonly tracking = inject(ExecutionTracking);
  private readonly flowValidation = inject(FlowValidation);

  readonly executionId = input<string | null>(null);
  readonly steps = input<readonly ExecutionPipelineStep[]>([]);
  readonly executionState = input<ExecutionState | string | null>(null);

  protected readonly flowDocument = signal<FlowDocument | null>(null);
  protected readonly catalog = signal<readonly FlowPluginCatalog[]>([]);
  protected readonly loading = signal<boolean>(false);
  protected readonly error = signal<string | null>(null);
  protected readonly selectedNodeId = signal<string | null>(null);

  constructor() {
    effect(() => {
      const id = this.executionId();
      if (!id) {
        this.flowDocument.set(null);
        return;
      }
      this.loadFlow(id);
    });
  }

  private loadFlow(id: string): void {
    this.loading.set(true);
    this.error.set(null);

    Promise.all([
      firstValueFrom(this.tracking.flow(id).pipe(catchError(() => of(null)))),
      this.flowValidation.catalog().catch(() => []),
    ])
      .then(([flow, catalog]) => {
        this.flowDocument.set(flow);
        this.catalog.set(catalog ?? []);
        this.loading.set(false);
      })
      .catch(() => {
        this.error.set('Impossible de charger le graphe de flow.');
        this.loading.set(false);
      });
  }

  protected readonly connections = computed<readonly FlowGraphConnection[]>(() => {
    const doc = this.flowDocument();
    if (!doc) return [];
    return doc.connections.map((conn, idx) => ({
      id: `trace-conn-${idx}`,
      sourceNodeId: conn.sourceNodeId,
      sourceOutputId: conn.sourceOutputId,
      targetNodeId: conn.targetNodeId,
    }));
  });

  protected readonly nodes = computed<readonly FlowGraphNode[]>(() => {
    const doc = this.flowDocument();
    if (!doc) return [];
    const plugins = this.catalog();
    const rawNodes = flowDocumentNodes(doc, plugins);
    const conns = this.connections();
    const layout = layoutFlowCanvas(rawNodes, conns);

    return rawNodes.map((node) => ({
      ...node,
      position: layout.positions.get(node.id) ?? { x: 80, y: 80 },
    }));
  });

  protected readonly executedNodeIds = computed<ReadonlySet<string>>(() => {
    return new Set(this.steps().map((s) => s.nodeId));
  });

  protected readonly executedConnectionIds = computed<ReadonlySet<string>>(() => {
    const stepsList = this.steps();
    const conns = this.connections();
    const activeIds = new Set<string>();

    for (let i = 0; i < stepsList.length; i++) {
      const step = stepsList[i];
      const nextStep = stepsList[i + 1];
      const targetId = step.nextNodeId || nextStep?.nodeId;

      for (const edge of conns) {
        if (
          edge.sourceNodeId === step.nodeId &&
          (!step.outputId || edge.sourceOutputId === step.outputId) &&
          (!targetId || edge.targetNodeId === targetId)
        ) {
          activeIds.add(edge.id);
        }
      }
    }

    return activeIds;
  });

  protected readonly nodeExecutionDetails = computed<
    ReadonlyMap<string, { duration?: string; output?: string; status?: string }>
  >(() => {
    const map = new Map<string, { duration?: string; output?: string; status?: string }>();
    for (const step of this.steps()) {
      map.set(step.nodeId, {
        duration: stepDuration(step),
        output: step.outputLabel || step.outputId || undefined,
        status: step.status,
      });
    }
    return map;
  });

  protected readonly selectedStep = computed<ExecutionPipelineStep | null>(() => {
    const id = this.selectedNodeId();
    if (!id) return null;
    return this.steps().find((s) => s.nodeId === id) ?? null;
  });

  protected onNodeSelected(nodeId: string): void {
    if (this.selectedNodeId() === nodeId) {
      this.selectedNodeId.set(null);
    } else {
      this.selectedNodeId.set(nodeId);
    }
  }

  protected formatDuration(step: ExecutionPipelineStep): string {
    return stepDuration(step);
  }
}
