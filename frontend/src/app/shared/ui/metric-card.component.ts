import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { MatCardModule } from '@angular/material/card';
import { RouterLink } from '@angular/router';

@Component({
  selector: 'app-metric-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatCardModule, NgTemplateOutlet, RouterLink],
  template: `
    @if (link()) {
      <a class="metric-card-link" [routerLink]="link()" [queryParams]="queryParams()">
        <ng-container [ngTemplateOutlet]="cardContent" />
      </a>
    } @else {
      <ng-container [ngTemplateOutlet]="cardContent" />
    }
    <ng-template #cardContent>
      <mat-card
        appearance="outlined"
        class="metric-card"
        [class.metric-card_attention]="tone() === 'attention'"
        [class.metric-card_stale]="stale()"
        [class.metric-card_actionable]="!!link()"
      >
        <mat-card-content>
          <p class="metric-label">{{ label() }}</p>
          <p class="metric-value">{{ value() }}</p>
          <p class="metric-detail">{{ detail() }}</p>
        </mat-card-content>
      </mat-card>
    </ng-template>
  `,
  styleUrl: './metric-card.component.scss',
})
export class MetricCardComponent {
  readonly label = input.required<string>();
  readonly value = input.required<string>();
  readonly detail = input.required<string>();
  /** `attention` teinte la carte quand une décision est attendue. */
  readonly tone = input<'default' | 'attention'>('default');
  /** Une tuile peut orienter vers la prise en charge correspondante. */
  readonly link = input<string>();
  readonly queryParams = input<Readonly<Record<string, string | number | boolean>>>({});
  /** La dernière valeur reste visible mais doit être identifiée comme périmée. */
  readonly stale = input(false);
}
