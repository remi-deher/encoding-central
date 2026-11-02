import { TestBed } from '@angular/core/testing';
import { MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { NodeModeDialogComponent } from './node-mode-dialog.component';
import { NodeModeDialogData } from '../models/node-mode';

function create(status: NodeModeDialogData) {
  const close = vi.fn();
  TestBed.configureTestingModule({
    providers: [
      { provide: MAT_DIALOG_DATA, useValue: status },
      { provide: MatDialogRef, useValue: { close, addPanelClass: vi.fn(), updateSize: vi.fn() } },
    ],
  });
  const fixture = TestBed.createComponent(NodeModeDialogComponent);
  fixture.detectChanges();
  return { fixture, close, element: fixture.nativeElement as HTMLElement };
}

function apply(element: HTMLElement): HTMLButtonElement {
  return Array.from(element.querySelectorAll('button')).find((button) =>
    button.textContent?.includes('Appliquer'),
  ) as HTMLButtonElement;
}

describe('NodeModeDialogComponent', () => {
  it('disables Apply until the mode changes', () => {
    const { element } = create({ managed: true, server: true, worker: true });
    expect(apply(element).disabled).toBe(true);
  });

  it('refuses a node without any function and explains why', () => {
    const { fixture, element } = create({ managed: true, server: true, worker: false });
    const component = fixture.componentInstance as unknown as {
      server: { set(value: boolean): void };
    };
    component.server.set(false);
    fixture.detectChanges();
    expect(element.querySelector('[role="alert"]')?.textContent).toContain('au moins une fonction');
    expect(apply(element).disabled).toBe(true);
  });

  it('returns the chosen mode when applied', () => {
    const { fixture, close, element } = create({ managed: true, server: true, worker: true });
    const component = fixture.componentInstance as unknown as {
      worker: { set(value: boolean): void };
    };
    component.worker.set(false);
    fixture.detectChanges();
    apply(element).click();
    expect(close).toHaveBeenCalledWith({ server: true, worker: false });
  });

  it('keeps an unmanaged node read-only and says how to change its mode', () => {
    const { element } = create({ managed: false, server: true, worker: true });
    expect(element.textContent).toContain('Node__Roles');
    expect(apply(element).disabled).toBe(true);
  });
});

describe('NodeModeDialogComponent for a remote node', () => {
  it('names the node and explains that the change is applied at its next heartbeat', () => {
    const { element } = create({
      managed: true,
      server: false,
      worker: true,
      remoteNodeName: 'Transcode A',
    });
    expect(element.querySelector('h2')?.textContent).toContain('Transcode A');
    expect(element.querySelector('[role="note"]')?.textContent).toContain('prochain signal');
  });
});
