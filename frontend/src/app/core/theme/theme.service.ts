import { DOCUMENT } from '@angular/common';
import { Injectable, computed, inject, signal } from '@angular/core';

export type ThemeMode = 'light' | 'dark';
export type ThemePalette = 'forest' | 'ocean' | 'iris' | 'rose';
export const THEME_PALETTES: readonly { value: ThemePalette; label: string }[] = [
  { value: 'forest', label: 'Forêt' },
  { value: 'ocean', label: 'Océan' },
  { value: 'iris', label: 'Iris' },
  { value: 'rose', label: 'Rose' },
];
const PALETTE_STORAGE_KEY = 'encoding.palette';

const STORAGE_KEY = 'encoding.theme';

@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly document = inject(DOCUMENT);
  private readonly mode = signal<ThemeMode>(this.initialMode());

  private readonly paletteMode = signal<ThemePalette>(this.initialPalette());
  readonly palettes = THEME_PALETTES;
  readonly palette = this.paletteMode.asReadonly();

  readonly current = this.mode.asReadonly();
  readonly isDark = computed(() => this.mode() === 'dark');

  constructor() {
    this.apply(this.mode());
    this.document.documentElement.dataset['palette'] = this.palette();
  }

  toggle(): void {
    this.set(this.isDark() ? 'light' : 'dark');
  }

  set(mode: ThemeMode): void {
    this.mode.set(mode);
    this.apply(mode);
    try {
      localStorage.setItem(STORAGE_KEY, mode);
    } catch {
      // Stockage indisponible : le choix reste valable pour la session.
    }
  }

  setPalette(palette: ThemePalette): void {
    if (!THEME_PALETTES.some((option) => option.value === palette)) return;
    this.paletteMode.set(palette);
    this.document.documentElement.dataset['palette'] = palette;
    try {
      localStorage.setItem(PALETTE_STORAGE_KEY, palette);
    } catch {
      // La préférence reste active si le stockage est indisponible.
    }
  }

  private initialPalette(): ThemePalette {
    try {
      const stored = localStorage.getItem(PALETTE_STORAGE_KEY);
      return THEME_PALETTES.find((option) => option.value === stored)?.value ?? 'forest';
    } catch {
      return 'forest';
    }
  }

  private initialMode(): ThemeMode {
    try {
      const stored = localStorage.getItem(STORAGE_KEY);
      if (stored === 'light' || stored === 'dark') return stored;
    } catch {
      // Stockage indisponible : repli sur la préférence système.
    }
    return this.document.defaultView?.matchMedia?.('(prefers-color-scheme: dark)').matches
      ? 'dark'
      : 'light';
  }

  private apply(mode: ThemeMode): void {
    this.document.documentElement.dataset['theme'] = mode;
  }
}
