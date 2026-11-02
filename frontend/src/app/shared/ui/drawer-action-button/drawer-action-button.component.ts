import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { RouterLink } from '@angular/router';

export type DrawerActionRoute = string | readonly string[];

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-drawer-action-button',
  imports: [MatButtonModule, RouterLink],
  template: `
    @if (routerLink(); as route) {
      <a
        matButton="filled"
        class="drawer-action-button"
        [class.drawer-action-button_danger]="tone() === 'danger'"
        [routerLink]="route"
        [info]="navigationInfo()"
        >{{ label() }}</a
      >
    } @else {
      <button
        [matButton]="variant()"
        class="drawer-action-button"
        [class.drawer-action-button_danger]="tone() === 'danger'"
        type="button"
        [disabled]="disabled()"
        (click)="activated.emit()"
      >
        {{ label() }}
      </button>
    }
  `,
  styleUrl: './drawer-action-button.component.scss',
})
export class DrawerActionButtonComponent {
  readonly tone = input<'default' | 'danger'>('default');
  /** `filled` : action principale du tiroir ; `outlined` : action secondaire. */
  readonly variant = input<'filled' | 'outlined'>('filled');
  readonly disabled = input(false);
  readonly label = input.required<string>();
  readonly routerLink = input<DrawerActionRoute | null>(null);
  readonly navigationInfo = input<Readonly<Record<string, unknown>> | undefined>(undefined);
  readonly activated = output<void>();
}
