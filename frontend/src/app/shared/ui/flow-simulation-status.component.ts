import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { SimulationResult } from '../models/flow-simulation';
import { FlowGraphNode } from '../models/flow-component';
import { MatTooltipModule } from '@angular/material/tooltip';
import { IconComponent } from './icon.component';
@Component({
  selector: 'app-flow-simulation-status',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[class.compact]': 'compact()' },
  imports: [IconComponent, MatTooltipModule],
  template: `@if (step(); as current) {
    <button
      type="button"
      (click)="$event.stopPropagation(); detailsRequested.emit()"
      [class.stopped]="stopped()"
      [class.assumed]="current.assumed"
      [matTooltip]="label() + (outputLabel() ? ' · ' + outputLabel() : '') + ' · ' + current.detail"
      [attr.aria-label]="label() + ' · Voir les détails du test'"
    >
      <app-icon
        [name]="waiting() ? 'clock' : stopped() ? 'warning' : current.assumed ? 'forward' : 'check'"
      /><span
        >{{ label() }}
        @if (outputLabel()) {
          · {{ outputLabel() }}
        }
      </span>
    </button>
  }`,

  styles: `
    button {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      border: 0;
      border-radius: var(--radius-pill);
      padding: var(--space-2) var(--space-3);
      min-height: 44px;
      margin: var(--space-2) var(--space-3);
      max-width: calc(100% - var(--space-5));
      background: var(--success-soft);
      color: var(--text-primary);
      font: inherit;
      font-size: var(--font-label);
      cursor: pointer;
      text-align: start;
    }
    button.stopped,
    button.assumed {
      background: var(--warning-soft);
    }
    button:focus-visible {
      outline: 2px solid var(--accent);
      outline-offset: var(--space-1);
    }
    :host.compact {
      display: contents;
    }
    :host.compact button {
      flex: none;
      width: var(--space-6);
      height: var(--space-6);
      min-height: 0;
      max-width: none;
      margin: 0;
      padding: var(--space-2);
      color: var(--success);
    }
    :host.compact button.stopped,
    :host.compact button.assumed {
      color: var(--warning);
    }
    :host.compact app-icon {
      width: var(--space-4);
      height: var(--space-4);
    }
    :host.compact span {
      display: none;
    }
  `,
})
export class FlowSimulationStatusComponent {
  readonly compact = input(false);
  readonly node = input.required<FlowGraphNode>();
  readonly result = input<SimulationResult | null>(null);
  readonly detailsRequested = output<void>();
  protected readonly step = computed(
    () =>
      this.result()?.steps?.find((step) => step.nodeId === this.node().id) ??
      (this.result()?.waitingNodeId === this.node().id
        ? { detail: this.result()?.detail ?? '', outputId: null, assumed: false }
        : undefined),
  );
  protected readonly waiting = computed(() => this.result()?.waitingNodeId === this.node().id);
  protected readonly stopped = computed(
    () =>
      this.result()?.waitingNodeId === this.node().id ||
      (this.result()?.status === 'failed' &&
        this.result()?.steps?.at(-1)?.nodeId === this.node().id),
  );
  protected readonly label = computed(() =>
    this.result()?.waitingNodeId === this.node().id
      ? 'En attente de votre choix'
      : this.stopped()
        ? 'Arrêt du test'
        : this.step()?.assumed
          ? 'Passage supposé'
          : 'Étape parcourue',
  );
  protected readonly outputLabel = computed(
    () =>
      this.node().component.outputs.find((output) => output.id === this.step()?.outputId)?.label ??
      this.step()?.outputId ??
      '',
  );
}
