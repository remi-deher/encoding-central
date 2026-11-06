import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { IconComponent, IconName } from './icon.component';

export type StatusTone = 'success' | 'warning' | 'danger' | 'neutral' | 'accent' | 'ok';

const TONE_ICONS: Record<Exclude<StatusTone, 'ok'>, IconName> = {
  success: 'check',
  warning: 'warning',
  danger: 'error',
  neutral: 'info',
  accent: 'auto_awesome',
};

@Component({
  selector: 'app-status-badge',
  imports: [IconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <span class="status-badge" [class]="'status-badge tone-' + normalizedTone()">
      <app-icon [name]="resolvedIcon()" />
      {{ label() }}
    </span>
  `,
  styleUrl: './status-badge.component.scss',
})
export class StatusBadgeComponent {
  readonly label = input.required<string>();
  readonly tone = input<StatusTone>('neutral');
  readonly icon = input<IconName>();
  protected readonly normalizedTone = computed(() => (this.tone() === 'ok' ? 'success' : this.tone()));
  protected readonly resolvedIcon = computed(() => this.icon() ?? TONE_ICONS[this.normalizedTone()]);
}
