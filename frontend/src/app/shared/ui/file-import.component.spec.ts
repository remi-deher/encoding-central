import { TestBed } from '@angular/core/testing';
import { FileImportComponent } from './file-import.component';

describe('FileImportComponent', () => {
  it('opens the picker through a Material button and disables it while busy', () => {
    const fixture = TestBed.createComponent(FileImportComponent);
    fixture.componentRef.setInput('label', 'Importer');
    fixture.componentRef.setInput('accept', '.json,.zip');
    fixture.detectChanges();
    const button = fixture.nativeElement.querySelector('button') as HTMLButtonElement;
    const picker = fixture.nativeElement.querySelector('input') as HTMLInputElement;
    const click = vi.spyOn(picker, 'click').mockImplementation(() => {});
    expect(button.textContent).toContain('Importer');
    expect(picker.accept).toBe('.json,.zip');
    button.click();
    expect(click).toHaveBeenCalledOnce();
    fixture.componentRef.setInput('busy', true);
    fixture.detectChanges();
    expect(button.disabled).toBe(true);
    expect(picker.disabled).toBe(true);
    button.click();
    expect(click).toHaveBeenCalledOnce();
  });
  it('renders an upload SVG without font ligatures in the button label', () => {
    const fixture = TestBed.createComponent(FileImportComponent);
    fixture.componentRef.setInput('label', 'Importer');
    fixture.detectChanges();
    const button = (fixture.nativeElement as HTMLElement).querySelector('button')!;
    expect(button.textContent?.trim()).toBe('Importer');
    expect(button.querySelector('svg path')?.getAttribute('d')).toBeTruthy();
    expect(button.querySelector('app-icon')?.getAttribute('aria-hidden')).toBe('true');
  });

  it('disables the picker and preserves the icon while reading a file', () => {
    const fixture = TestBed.createComponent(FileImportComponent);
    fixture.componentRef.setInput('busy', true);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('button')?.disabled).toBe(true);
    expect(host.querySelector('input')?.disabled).toBe(true);
    expect(host.querySelector('button')?.textContent?.trim()).toBe('Lecture en cours…');
    expect(host.querySelector('svg')).toBeTruthy();
  });
});
