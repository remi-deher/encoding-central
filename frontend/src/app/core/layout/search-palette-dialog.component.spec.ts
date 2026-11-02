import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { provideRouter, Router } from '@angular/router';
import { signal } from '@angular/core';
import { SearchPaletteDialogComponent } from './search-palette-dialog.component';
import { AuthService } from '../auth/auth.service';
import { routes } from '../../app.routes';

describe('SearchPaletteDialogComponent', () => {
  let closeFn: ReturnType<typeof vi.fn>;
  let router: Router;

  beforeEach(() => {
    closeFn = vi.fn();
    TestBed.configureTestingModule({
      imports: [SearchPaletteDialogComponent],
      providers: [
        provideRouter(routes),
        { provide: AuthService, useValue: { isAdmin: signal(true) } },
        { provide: MatDialogRef, useValue: { close: closeFn } },
        { provide: MAT_DIALOG_DATA, useValue: {} },
      ],
    });
    router = TestBed.inject(Router);
    vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);
  });

  it('renders WAI-ARIA combobox and filters navigation destinations', () => {
    const fixture = TestBed.createComponent(SearchPaletteDialogComponent);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const input = host.querySelector('input[role="combobox"]') as HTMLInputElement;
    expect(input).toBeTruthy();
    expect(input.getAttribute('aria-controls')).toBe('palette-results-listbox');

    const initialOptions = host.querySelectorAll('[role="option"]');
    expect(initialOptions.length).toBeGreaterThanOrEqual(8);

    input.value = 'Profils';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    const filteredOptions = host.querySelectorAll('[role="option"]');
    expect(filteredOptions.length).toBe(1);
    expect(filteredOptions[0].textContent).toContain('Profils d’encodage');
  });

  it('navigates with ArrowDown and Enter keys and closes the dialog', () => {
    const fixture = TestBed.createComponent(SearchPaletteDialogComponent);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const input = host.querySelector('input[role="combobox"]') as HTMLInputElement;

    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
    fixture.detectChanges();

    expect(input.getAttribute('aria-activedescendant')).toBe('palette-option-1');

    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    expect(closeFn).toHaveBeenCalled();
    expect(router.navigateByUrl).toHaveBeenCalledWith('/flows');
  });
});
