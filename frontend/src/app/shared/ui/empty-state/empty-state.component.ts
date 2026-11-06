import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { IconComponent, IconName } from '../icon.component';

@Component({
  selector: 'app-empty-state',
  standalone: true,
  imports: [IconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="empty-state" role="status" aria-live="polite">
      <app-icon class="empty-state__icon" [name]="icon()" />
      <h2>{{ title() }}</h2>
      @if (message()) {
        <p>{{ message() }}</p>
      }
      <div class="empty-state__action"><ng-content /></div>
    </section>
  `,
  styles: `
    :host { display: block; }
    .empty-state {
      display: grid;
      justify-items: center;
      gap: var(--space-2);
      padding: var(--space-6) var(--space-4);
      text-align: center;
    }
    .empty-state__icon {
      width: 32px;
      height: 32px;
      color: var(--mat-sys-on-surface-variant);
    }
    h2 {
      margin: 0;
      color: var(--mat-sys-on-surface);
      font-size: var(--font-title-lg);
      font-weight: 500;
    }
    p {
      max-width: 42rem;
      margin: 0;
      color: var(--mat-sys-on-surface-variant);
      font-size: var(--font-body);
    }
    .empty-state__action { margin-top: var(--space-2); }
    .empty-state__action:empty { display: none; }
  `,
})
export class EmptyStateComponent {
  readonly icon = input.required<IconName>();
  readonly title = input.required<string>();
  readonly message = input('');
}
