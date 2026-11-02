import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ContextActionsDirective } from './context-actions.directive';
@Component({
  imports: [ContextActionsDirective],
  template:
    '<div tabindex="0" appContextActions (contextActions)="opened = opened + 1">Étape</div>',
})
class Host {
  opened = 0;
}
describe('context actions', () => {
  afterEach(() => vi.useRealTimers());
  it('supports mouse and keyboard context menus', () => {
    const f = TestBed.createComponent(Host);
    f.detectChanges();
    const el = f.nativeElement.querySelector('div') as HTMLElement;
    el.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true }));
    expect(f.componentInstance.opened).toBe(1);
    el.dispatchEvent(new KeyboardEvent('keydown', { key: 'F10', shiftKey: true, bubbles: true }));
    expect(f.componentInstance.opened).toBe(2);
  });
  it('opens on a stationary long press, cancels when the finger moves, and prevents an accidental click', () => {
    vi.useFakeTimers();
    const f = TestBed.createComponent(Host);
    f.detectChanges();
    const el = f.nativeElement.querySelector('div') as HTMLElement;
    const pointer = (type: string, x: number) => {
      const event = new Event(type, { bubbles: true, cancelable: true });
      Object.assign(event, { pointerType: 'touch', clientX: x, clientY: 10 });
      el.dispatchEvent(event);
    };
    pointer('pointerdown', 10);
    pointer('pointermove', 30);
    vi.advanceTimersByTime(600);
    expect(f.componentInstance.opened).toBe(0);
    pointer('pointerdown', 10);
    vi.advanceTimersByTime(560);
    expect(f.componentInstance.opened).toBe(1);
    const click = new MouseEvent('click', { bubbles: true, cancelable: true });
    el.dispatchEvent(click);
    expect(click.defaultPrevented).toBe(true);
  });
});
