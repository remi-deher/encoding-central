import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { StatusBadgeComponent, StatusTone } from './status-badge.component';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-health-banner',
  imports: [StatusBadgeComponent],
  template: `
    <section class="health-banner" [class]="'health-banner tone-' + tone()" aria-live="polite">
      <app-status-badge [label]="title()" [tone]="tone()" />
      @if (issues().length) {
        <ul>
          @for (issue of issues(); track issue) {
            <li>{{ issue }}</li>
          }
        </ul>
      }
    </section>
  `,
  styles: `
    :host {
      display: block;
    }
    .health-banner {
      display: grid;
      gap: var(--space-2, 8px);
      padding: var(--space-3, 12px) var(--space-4, 16px);
      border: 1px solid var(--border-subtle);
      border-radius: var(--radius-lg, 16px);
      background: var(--surface);
      box-shadow: 0 1px 3px rgb(0 0 0 / 4%);
    }
    ul {
      margin: 0;
      padding-left: 1.2em;
    }
  `,
})
export class HealthBannerComponent {
  readonly title = input.required<string>();
  readonly tone = input<StatusTone>('neutral');
  readonly issues = input<readonly string[]>([]);
}
