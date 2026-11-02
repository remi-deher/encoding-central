import { Directive, ElementRef, Injectable, inject, output, signal } from '@angular/core';
import { DOCUMENT } from '@angular/common';
import {
  FlowComponentDefinition,
  FlowComponentOutput,
  FlowGraphPosition,
} from '../models/flow-component';

export interface FlowPaletteDrop {
  readonly component: FlowComponentDefinition;
  readonly point: FlowGraphPosition;
  readonly position?: FlowGraphPosition;
  readonly sourceNodeId?: string;
  readonly output?: FlowComponentOutput;
}

@Injectable({ providedIn: 'root' })
export class FlowPaletteDrag {
  private readonly document = inject(DOCUMENT);
  readonly component = signal<FlowComponentDefinition | null>(null);
  readonly pointer = signal<FlowGraphPosition>({ x: 0, y: 0 });
  readonly target = signal<FlowComponentDrop | null>(null);
  readonly targets = new Set<FlowComponentDrop>();

  move(point: FlowGraphPosition): void {
    this.pointer.set(point);
    const hit = this.document.elementFromPoint(point.x, point.y);
    const targets = [...this.targets].filter((target) => hit && target.element.contains(hit));
    this.target.set(
      targets.find(
        (target) =>
          !targets.some((other) => other !== target && target.element.contains(other.element)),
      ) ?? null,
    );
  }

  finish(point: FlowGraphPosition): void {
    this.move(point);
    const component = this.component(),
      target = this.target();
    this.component.set(null);
    this.target.set(null);
    if (component && target) target.componentDropped.emit({ component, point });
  }
}

@Directive({
  selector: '[appFlowComponentDrop]',
  host: {
    '[class.flow-palette-drop-active]': 'drag.component() !== null',
    '[class.flow-palette-drop-hover]': 'drag.target() === this',
    '[style.outline]': "drag.target() === this ? '2px dashed var(--accent)' : null",
    '[style.outline-offset]': "drag.target() === this ? '4px' : null",
  },
})
export class FlowComponentDrop {
  protected readonly drag = inject(FlowPaletteDrag);
  readonly element = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;
  readonly componentDropped = output<FlowPaletteDrop>();

  constructor() {
    this.drag.targets.add(this);
  }
  ngOnDestroy(): void {
    this.drag.targets.delete(this);
    if (this.drag.target() === this) this.drag.target.set(null);
  }
}
