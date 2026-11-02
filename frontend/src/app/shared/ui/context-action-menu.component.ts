import {
  ChangeDetectionStrategy,
  Component,
  afterRenderEffect,
  input,
  output,
  viewChild,
} from '@angular/core';
import { MatMenuModule, MatMenuTrigger } from '@angular/material/menu';
import { ContextActionPosition } from './context-actions.directive';
export interface ContextActionItem {
  readonly id: string;
  readonly label: string;
  readonly disabled?: boolean;
}
export interface ContextActionMenuData extends ContextActionPosition {
  readonly items: readonly ContextActionItem[];
}
@Component({
  selector: 'app-context-action-menu',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatMenuModule],
  template: `@if (context(); as data) {
    <button
      class="anchor"
      [style.left.px]="data.x"
      [style.top.px]="data.y"
      [matMenuTriggerFor]="menu"
      [matMenuTriggerRestoreFocus]="false"
      aria-hidden="true"
      tabindex="-1"
    ></button>
    <mat-menu #menu="matMenu" (closed)="onClosed()">
      @for (item of data.items; track item.id) {
        <button mat-menu-item [disabled]="item.disabled" (click)="action.emit(item.id)">
          {{ item.label }}
        </button>
      }
    </mat-menu>
  }`,
  styles: `
    .anchor {
      position: fixed;
      width: 1px;
      height: 1px;
      padding: 0;
      border: 0;
      opacity: 0;
      pointer-events: none;
    }
  `,
})
export class ContextActionMenuComponent {
  readonly context = input<ContextActionMenuData | null>(null);
  readonly action = output<string>();
  readonly closed = output<void>();
  private readonly trigger = viewChild(MatMenuTrigger);
  constructor() {
    afterRenderEffect(() => {
      if (this.context() && this.trigger() && !this.trigger()!.menuOpen) this.trigger()!.openMenu();
    });
  }
  protected onClosed(): void {
    this.context()?.origin.focus({ preventScroll: true });
    this.closed.emit();
  }
}
