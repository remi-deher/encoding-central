import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/** Unique bloc d’aide ou d’avertissement des Paramètres. */
@Component({
  selector: 'app-settings-notice',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[attr.data-tone]': 'tone()', '[attr.role]': "tone() === 'warning' ? 'status' : null" },
  template: `<ng-content />`,
  styles: `
    :host {
      display: block;
      margin-bottom: var(--space-3);
      padding: var(--space-3) var(--space-4);
      border-radius: var(--radius-lg);
      background: var(--surface-container-low);
      color: var(--text-secondary);
      line-height: 1.5;
      overflow-wrap: anywhere;
    }
    :host([data-tone='info']) {
      background: var(--info-soft, var(--accent-soft));
      color: var(--text-primary);
    }
    :host([data-tone='warning']) {
      background: var(--warning-soft);
      color: var(--text-primary);
    }
    :host([data-tone='danger']) {
      background: var(--danger-soft);
      color: var(--danger);
    }
  `,
})
export class SettingsNoticeComponent {
  readonly tone = input<'neutral' | 'info' | 'warning' | 'danger'>('neutral');
}
