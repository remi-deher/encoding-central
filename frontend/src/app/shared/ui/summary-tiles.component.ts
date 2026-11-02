import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { MetricCardComponent } from './metric-card.component';

export interface SummaryTile {
  readonly label: string;
  readonly value: string;
  readonly detail: string;
  readonly tone?: 'default' | 'attention';
  readonly link?: string;
  readonly queryParams?: Readonly<Record<string, string | number | boolean>>;
  readonly stale?: boolean;
}

/** Rangée de tuiles récapitulatives, avec orientation facultative vers la prise en charge. */
@Component({
  selector: 'app-summary-tiles',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MetricCardComponent],
  template: `
    <section
      class="summary-tiles"
      [class.summary-tiles_compact]="density() === 'compact'"
      [attr.aria-label]="label()"
    >
      @for (tile of tiles(); track tile.label) {
        <app-metric-card
          [label]="tile.label"
          [value]="tile.value"
          [detail]="tile.detail"
          [tone]="tile.tone ?? 'default'"
          [link]="tile.link"
          [queryParams]="tile.queryParams ?? {}"
          [stale]="tile.stale ?? false"
        />
      }
    </section>
  `,
  styles: `
    .summary-tiles {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(min(100%, 12rem), 1fr));
      gap: var(--space-4);
    }
    .summary-tiles_compact {
      grid-template-columns: repeat(auto-fit, minmax(min(100%, 9rem), 1fr));
    }
  `,
})
export class SummaryTilesComponent {
  readonly density = input<'comfortable' | 'compact'>('comfortable');
  readonly label = input('Synthèse');
  readonly tiles = input.required<readonly SummaryTile[]>();
}
