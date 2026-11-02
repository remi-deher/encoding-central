import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { FlowDeleteDialogComponent, FlowDeleteDialogData } from './flow-delete-dialog.component';

function setup(data: FlowDeleteDialogData) {
  const close = vi.fn();
  TestBed.configureTestingModule({
    imports: [FlowDeleteDialogComponent],
    providers: [
      { provide: MAT_DIALOG_DATA, useValue: data },
      {
        provide: MatDialogRef,
        useValue: { close, addPanelClass: vi.fn(), updateSize: vi.fn() },
      },
    ],
  });
  const fixture = TestBed.createComponent(FlowDeleteDialogComponent);
  fixture.detectChanges();
  return { fixture, close, element: fixture.nativeElement as HTMLElement };
}

describe('Flow delete dialog', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('lists the libraries and requires the checkbox', async () => {
    const { fixture, close, element } = setup({
      name: 'Films',
      libraries: [{ libraryId: 'l1', libraryName: 'Cinéma', version: 2, isActive: true }],
    });
    expect(element.querySelectorAll('tbody tr')).toHaveLength(1);
    expect(element.textContent).toContain('Cinéma');
    const confirm = [...element.querySelectorAll('button')].find((b) =>
      b.textContent?.includes('Supprimer'),
    ) as HTMLButtonElement;
    expect(confirm.disabled).toBe(true);
    (element.querySelector('input[type="checkbox"]') as HTMLInputElement).click();
    fixture.detectChanges();
    expect(confirm.disabled).toBe(false);
    confirm.click();
    expect(close).toHaveBeenCalledWith(true);
  });

  it('still requires the checkbox when no library uses the flow', () => {
    const { element } = setup({ name: 'Films', libraries: [] });
    expect(element.querySelector('table')).toBeNull();
    expect(element.textContent).toContain('Aucune bibliothèque');
    expect(element.querySelector('input[type="checkbox"]')).not.toBeNull();
  });
});
