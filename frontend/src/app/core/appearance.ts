import { Injectable, signal } from '@angular/core';

export type ThemeName = 'light' | 'dark';
export type PaletteName = 'forest' | 'ocean' | 'iris' | 'rose';

const palettes: readonly PaletteName[] = ['forest', 'ocean', 'iris', 'rose'];

@Injectable({ providedIn: 'root' })
export class Appearance {
  readonly theme = signal<ThemeName>(currentTheme());
  readonly palette = signal<PaletteName>(currentPalette());

  toggleTheme(): void {
    const next: ThemeName = this.theme() === 'dark' ? 'light' : 'dark';
    this.theme.set(next);
    document.documentElement.dataset['theme'] = next;
    localStorage.setItem('encoding-theme', next);
  }

  cyclePalette(): void {
    const index = palettes.indexOf(this.palette());
    const next = palettes[(index + 1) % palettes.length] ?? 'forest';
    this.palette.set(next);
    if (next === 'forest') delete document.documentElement.dataset['palette'];
    else document.documentElement.dataset['palette'] = next;
    localStorage.setItem('encoding-palette', next);
  }
}

function currentTheme(): ThemeName {
  return document.documentElement.dataset['theme'] === 'dark' ? 'dark' : 'light';
}

function currentPalette(): PaletteName {
  const value = document.documentElement.dataset['palette'];
  return value === 'ocean' || value === 'iris' || value === 'rose' ? value : 'forest';
}
