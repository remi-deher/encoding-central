import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { ExecutionCardComponent } from '../../../shared/ui/execution-card.component';
import { ExecutionPipelineLogsComponent } from '../../../shared/ui/execution-pipeline-logs.component';
import { LibraryWorkflowStore } from '../library-workflow.store';

/** Onglet « Historique des exécutions » de la fiche bibliothèque (emplacement `libraryHistory`). */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-library-history-tab',
  imports: [ExecutionCardComponent, ExecutionPipelineLogsComponent],
  template: `
    @if (workflow.catalog.selectedLibrary()) {
      <h3>Historique des exécutions</h3>
      @if (workflow.executionTrackingState().status === 'error') {
        <p role="status">Actualisation des exécutions impossible.</p>
      }
      <div class="library-execution-cards">
        @for (execution of workflow.libraryExecutions(); track execution.id) {
          <app-execution-card
            [execution]="execution"
            [showPipelineAction]="true"
            (pipelineRequested)="workflow.onProcessingExecutionAction(execution.id)"
          />
        } @empty {
          <p>Aucune exécution récente pour cette bibliothèque.</p>
        }
      </div>
      @if (workflow.selectedProcessingExecutionId(); as executionId) {
        <app-execution-pipeline-logs
          [executionId]="executionId"
          [steps]="workflow.processingPipelineState().steps"
          [feedback]="workflow.processingPipelineState().message"
        />
      }
    }
  `,
  styleUrl: './library-history-tab.component.scss',
})
export class LibraryHistoryTabComponent {
  protected readonly workflow = inject(LibraryWorkflowStore);
}
