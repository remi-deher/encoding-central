import { ContextActionsDirective, ContextActionPosition } from './context-actions.directive';
import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { IconComponent } from './icon.component';
export interface FlowLinkDrawing {
  readonly id: string;
  readonly path: string;
  readonly error: boolean;
  readonly insertion: { readonly x: number; readonly y: number };
}
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-flow-links',
  imports: [MatButtonModule, ContextActionsDirective, IconComponent],
  templateUrl: './flow-links.html',
  styleUrl: './flow-links.scss',
})
export class FlowLinks {
  readonly contextRequested = output<{ id: string; position: ContextActionPosition }>();
  readonly insertionRequested = output<string>();
  readonly size = input.required<{ readonly width: number; readonly height: number }>();
  readonly links = input.required<readonly FlowLinkDrawing[]>();
}
