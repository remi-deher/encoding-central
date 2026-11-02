import { Directive, DestroyRef, ElementRef, inject, output } from '@angular/core';
export interface ContextActionPosition {
  readonly x: number;
  readonly y: number;
  readonly origin: HTMLElement;
}
@Directive({
  selector: '[appContextActions]',
  host: {
    '(contextmenu)': 'context($event)',
    '(keydown)': 'keyboard($event)',
    '(pointerdown)': 'start($event)',
    '(pointermove)': 'move($event)',
    '(pointerup)': 'cancel()',
    '(pointercancel)': 'cancel()',
  },
})
export class ContextActionsDirective {
  readonly contextActions = output<ContextActionPosition>();
  private readonly element = inject<ElementRef<HTMLElement>>(ElementRef);
  private timer?: ReturnType<typeof setTimeout>;
  private point?: { x: number; y: number };
  private suppressUntil = 0;
  constructor() {
    const click = (event: Event) => {
      if (Date.now() < this.suppressUntil) {
        event.preventDefault();
        event.stopImmediatePropagation();
      }
    };
    this.element.nativeElement.addEventListener('click', click, true);
    inject(DestroyRef).onDestroy(() => {
      this.cancel();
      this.element.nativeElement.removeEventListener('click', click, true);
    });
  }
  protected context(event: MouseEvent): void {
    if (
      event.target instanceof Element &&
      event.target.closest('input,textarea,[contenteditable="true"],.cm-editor')
    )
      return;
    event.preventDefault();
    event.stopPropagation();
    this.cancel();
    this.open(event.clientX, event.clientY);
  }
  protected keyboard(event: KeyboardEvent): void {
    if (event.key !== 'ContextMenu' && !(event.shiftKey && event.key === 'F10')) return;
    event.preventDefault();
    event.stopPropagation();
    const rect = this.element.nativeElement.getBoundingClientRect();
    this.open(rect.left + 16, rect.top + 16);
  }
  protected start(event: PointerEvent): void {
    this.cancel();
    if (
      event.pointerType === 'mouse' ||
      (event.target instanceof Element &&
        event.target.closest('button,input,textarea,select,a') &&
        event.target.closest('button,input,textarea,select,a') !== this.element.nativeElement)
    )
      return;
    if (
      event.target instanceof Element &&
      event.target.closest('[appContextActions]') !== this.element.nativeElement
    )
      return;
    this.point = { x: event.clientX, y: event.clientY };
    this.timer = setTimeout(() => {
      this.suppressUntil = Date.now() + 900;
      this.open(event.clientX, event.clientY);
      this.cancel();
    }, 550);
  }
  protected move(event: PointerEvent): void {
    if (this.point && Math.hypot(event.clientX - this.point.x, event.clientY - this.point.y) > 10)
      this.cancel();
  }
  protected cancel(): void {
    clearTimeout(this.timer);
    this.timer = undefined;
    this.point = undefined;
  }
  private open(x: number, y: number): void {
    this.contextActions.emit({ x, y, origin: this.element.nativeElement });
  }
}
