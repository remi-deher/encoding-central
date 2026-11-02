import { TestBed } from '@angular/core/testing';
import { ThemeService } from './theme.service';

describe('ThemeService appearance preferences', () => {
  beforeEach(() => {
    localStorage.removeItem('encoding.theme');
    localStorage.removeItem('encoding.palette');
  });
  afterEach(() => {
    localStorage.removeItem('encoding.theme');
    localStorage.removeItem('encoding.palette');
    delete document.documentElement.dataset['theme'];
    delete document.documentElement.dataset['palette'];
  });

  it('restores the two preferences independently and retains the palette when switching mode', () => {
    localStorage.setItem('encoding.theme', 'dark');
    localStorage.setItem('encoding.palette', 'iris');
    const theme = TestBed.inject(ThemeService);
    expect(theme.isDark()).toBe(true);
    expect(theme.palette()).toBe('iris');
    theme.toggle();
    expect(document.documentElement.dataset['theme']).toBe('light');
    expect(document.documentElement.dataset['palette']).toBe('iris');
    expect(localStorage.getItem('encoding.theme')).toBe('light');
    theme.setPalette('ocean');
    expect(theme.current()).toBe('light');
    expect(localStorage.getItem('encoding.palette')).toBe('ocean');
    expect(document.documentElement.dataset['palette']).toBe('ocean');
  });

  it('ignores unknown persisted palettes and survives unavailable storage', () => {
    localStorage.setItem('encoding.palette', 'unknown');
    const theme = TestBed.inject(ThemeService);
    expect(theme.palette()).toBe('forest');
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    theme.setPalette('rose');
    theme.set('dark');
    expect(document.documentElement.dataset['palette']).toBe('rose');
    expect(theme.isDark()).toBe(true);
    vi.restoreAllMocks();
  });
});
