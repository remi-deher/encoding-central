import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, signal } from '@angular/core';
import { ExecutionResponse } from '../../../shared/models/execution';
import { WorkerRunningExecution } from '../../../shared/models/central';
import { ExecutionTelemetry, WorkerTelemetryHandle } from '../../../shared/services/execution-telemetry';
import { ExecutionTracking } from '../../../shared/services/execution-tracking';
import { formatFullDateTime } from '../../../shared/models/date-format';
import { catchError, of } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-worker-running-tasks',
  template: `
    <section class="running-tasks" aria-label="Traitements en cours sur ce worker">
      <header class="running-heading">
        <h3>Traitements en cours</h3>
        @if (connected()) {
          <span class="connection" role="status">Temps réel</span>
        }
      </header>

      @if (!connected()) {
        <p class="fallback" role="status">
          Télémétrie indisponible · {{ activityCount() }} tâche(s) active(s) selon l’activité du worker.
        </p>
      } @else if (running().length === 0) {
        <p class="empty" role="status">Aucun traitement en cours.</p>
      } @else {
        <ul class="task-list">
          @for (task of tasks(); track task.executionId) {
            <li class="task">
              <div class="task-title">
                <strong>{{ mediaName(metadata()[task.executionId]?.media) }}</strong>
                <span>{{ metadata()[task.executionId]?.flow || 'Flux en cours de chargement…' }}</span>
              </div>
              <dl class="task-facts">
                <div><dt>Étape</dt><dd>{{ metadata()[task.executionId]?.currentPhase || task.currentNodeId || 'En préparation' }}</dd></div>
                <div><dt>Durée</dt><dd>{{ elapsed(task.startedAt) }}</dd></div>
              </dl>
              @if (task.progress?.percent !== null && task.progress?.percent !== undefined) {
                <div class="progress-wrap">
                  <div class="progress-label"><span>Progression</span><span>{{ task.progress?.percent }} %</span></div>
                  <progress [value]="progressValue(task)" max="100" [attr.aria-label]="'Progression de ' + mediaName(metadata()[task.executionId]?.media)">{{ progressValue(task) }} %</progress>
                </div>
              }
              <details class="live-logs">
                <summary>Journaux en direct ({{ logsByExecution()[task.executionId]?.length ?? 0 }})</summary>
                @if (logsByExecution()[task.executionId]?.length; as logCount) {
                  <ol aria-label="Dernières lignes de journal">
                    @for (line of logsByExecution()[task.executionId]; track line.commandId + ':' + line.seq) {
                      <li><time [attr.datetime]="line.at">{{ time(line.at) }}</time> <span>{{ line.text }}</span></li>
                    }
                  </ol>
                } @else {
                  <p>Aucune ligne reçue pour le moment.</p>
                }
              </details>
              <a class="execution-link" [href]="activityUrl(task.executionId)">Ouvrir dans l’activité</a>
            </li>
          }
        </ul>
      }
    </section>
  `,
  styles: [`
    :host { display: block; }
    .running-tasks { display: grid; gap: 12px; padding: 14px 4px; }
    .running-heading, .progress-label { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
    h3 { margin: 0; font-size: 15px; color: var(--text-primary); }
    .connection { color: var(--good); font-size: 12px; }
    .empty, .fallback { margin: 0; color: var(--text-secondary); font-size: 13px; }
    .task-list { display: grid; gap: 10px; margin: 0; padding: 0; list-style: none; }
    .task { display: grid; gap: 10px; border: 1px solid var(--border-subtle); border-radius: 10px; padding: 12px; background: var(--surface-muted); min-width: 0; }
    .task-title { display: grid; gap: 3px; overflow-wrap: anywhere; }
    .task-title strong { color: var(--text-primary); }
    .task-title span, .task-facts dt, .live-logs p { color: var(--text-secondary); font-size: 12px; }
    .task-facts { display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: 10px; margin: 0; }
    .task-facts div { min-width: 0; }
    .task-facts dt { margin-bottom: 2px; }
    .task-facts dd { margin: 0; font-size: 13px; overflow-wrap: anywhere; }
    .progress-wrap { display: grid; gap: 5px; font-size: 12px; }
    progress { width: 100%; height: 8px; accent-color: var(--accent); }
    .live-logs summary { cursor: pointer; font-size: 13px; }
    .live-logs ol { display: grid; gap: 4px; max-height: 200px; overflow: auto; padding: 8px 8px 8px 28px; margin: 8px 0 0; background: var(--surface); border-radius: 6px; font: 12px/1.45 ui-monospace, monospace; }
    .live-logs li { overflow-wrap: anywhere; }
    .live-logs time { color: var(--text-secondary); white-space: nowrap; }
    .execution-link { width: fit-content; color: var(--accent); font-size: 13px; text-underline-offset: 3px; }
    .execution-link:focus-visible, summary:focus-visible { outline: 2px solid var(--accent); outline-offset: 3px; }
    @media (max-width: 520px) { .task-facts { grid-template-columns: 1fr; } }
  `],
})
export class WorkerRunningTasksComponent {
  readonly workerId = input.required<string>();
  readonly activityCount = input(0);
  private readonly destroyRef = inject(DestroyRef);
  private readonly service = inject(ExecutionTelemetry);
  private readonly tracking = inject(ExecutionTracking);
  private readonly handle = signal<WorkerTelemetryHandle | null>(null);
  readonly running = computed(() => this.handle()?.running() ?? []);
  readonly logsByExecution = computed(() => this.handle()?.logsByExecution() ?? {});
  readonly connected = computed(() => this.handle()?.connected() ?? false);
  readonly metadata = signal<Readonly<Record<string, ExecutionResponse>>>({});
  private readonly now = signal(Date.now());
  private readonly metadataRequests = new Set<string>();
  readonly tasks = computed(() => this.running());
  private readonly timer = setInterval(() => this.now.set(Date.now()), 1000);

  constructor() {
    this.destroyRef.onDestroy(() => clearInterval(this.timer));
    effect((onCleanup) => {
      const workerId = this.workerId();
      const handle = this.service.worker(workerId, this.destroyRef);
      this.handle.set(handle);
      onCleanup(() => handle.close());
    });
    effect(() => {
      const running = this.running();
      for (const task of running) {
        if (this.metadata()[task.executionId] || this.metadataRequests.has(task.executionId)) continue;
        this.metadataRequests.add(task.executionId);
        this.tracking.execution(task.executionId).pipe(
          catchError(() => of(null)),
          takeUntilDestroyed(this.destroyRef),
        ).subscribe((execution) => {
          if (execution) this.metadata.update((current) => ({ ...current, [task.executionId]: execution }));
        });
      }
    });
  }

  elapsed(startedAt: string): string {
    const start = Date.parse(startedAt);
    const seconds = Number.isNaN(start) ? 0 : Math.max(0, Math.floor((this.now() - start) / 1000));
    return `${Math.floor(seconds / 60)} min ${String(seconds % 60).padStart(2, '0')} s`;
  }

  progressValue(task: WorkerRunningExecution): number {
    return Math.max(0, Math.min(100, task.progress?.percent ?? 0));
  }

  activityUrl(executionId: string): string {
    const worker = encodeURIComponent(this.workerId());
    return `/activity?view=active&node=${worker}&execution=${encodeURIComponent(executionId)}`;
  }

  time(value: string): string {
    return formatFullDateTime(value);
  }

  mediaName(value: string | undefined): string {
    return value?.split(/[\\/]/).filter(Boolean).at(-1) || 'Traitement en cours';
  }
}
