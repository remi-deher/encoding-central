import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/** Bandeau de contexte d'une page de gestion : où l'on se trouve, et l'action qui s'y rapporte. */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-context-banner',
  template: `
    <section class="context-banner" [attr.aria-label]="label()">
      <div class="context-copy">
        <strong>{{ title() }}</strong>
        @if (description()) {
          <span>{{ description() }}</span>
        }
      </div>
      <div class="context-actions"><ng-content select="[banner-actions]" /></div>
    </section>
  `,
  styles: `
    .context-banner {
      padding: var(--space-4) var(--space-5);
      display: flex;
      align-items: center;
      justify-content: space-between;
      flex-wrap: wrap;
      gap: var(--space-4);
      border-radius: var(--radius-xl);
      background: var(--accent-soft);
      color: var(--text-primary);
    }

    .context-copy {
      min-width: 0;
      display: grid;
      gap: var(--space-1);
    }

    .context-copy strong {
      font-size: var(--font-title);
    }

    .context-copy span {
      color: var(--text-secondary);
      font-size: var(--font-body);
    }

    .context-actions {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-2);
    }
  `,
})
export class ContextBannerComponent {
  readonly label = input('Contexte');
  readonly title = input.required<string>();
  readonly description = input('');
}
