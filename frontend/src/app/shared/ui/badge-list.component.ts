import { ChangeDetectionStrategy, Component, input } from '@angular/core';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-badge-list',
  template: `
    <span class="badges">
      @for (badge of badges(); track $index) {
        <span class="badge">{{ badge }}</span>
      } @empty {
        —
      }
    </span>
  `,
  styles: `
    .badges {
      display: inline-flex;
      flex-wrap: wrap;
      gap: var(--space-1);
    }
    .badge {
      padding: 2px var(--space-2);
      border: 1px solid var(--border-subtle);
      border-radius: var(--radius-pill);
      background: var(--surface-muted);
      color: var(--text-secondary);
      font-size: var(--font-body);
      font-weight: 600;
      white-space: nowrap;
    }
  `,
})
export class BadgeListComponent {
  readonly badges = input.required<readonly string[]>();
}
