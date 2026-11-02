import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatMenuModule } from '@angular/material/menu';
import { ThemeService } from './theme.service';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-theme-toggle',
  imports: [MatButtonModule, MatTooltipModule, MatMenuModule],
  styles: `
    :host {
      display: inline-flex;
      align-items: center;
    }
    .palette-indicator {
      display: inline-block;
      width: 16px;
      height: 16px;
      border-radius: var(--radius-pill);
      background: var(--accent);
      box-shadow: inset 0 0 0 2px var(--accent-soft);
    }
    .palette-check {
      margin-inline-start: auto;
      color: var(--accent);
    }
  `,
  template: `
    <button
      mat-icon-button
      type="button"
      [attr.aria-pressed]="theme.isDark()"
      [attr.aria-label]="theme.isDark() ? 'Passer au thème clair' : 'Passer au thème sombre'"
      [matTooltip]="theme.isDark() ? 'Thème clair' : 'Thème sombre'"
      (click)="theme.toggle()"
    >
      <svg viewBox="0 0 24 24" width="22" height="22" aria-hidden="true" fill="currentColor">
        @if (theme.isDark()) {
          <path
            d="M12 7a5 5 0 1 0 0 10 5 5 0 0 0 0-10Zm0-5h0a1 1 0 0 1 1 1v2a1 1 0 0 1-2 0V3a1 1 0 0 1 1-1Zm0 18a1 1 0 0 1 1 1v2a1 1 0 0 1-2 0v-2a1 1 0 0 1 1-1ZM4.2 4.2a1 1 0 0 1 1.4 0l1.4 1.4A1 1 0 0 1 5.6 7L4.2 5.6a1 1 0 0 1 0-1.4Zm12.8 12.8a1 1 0 0 1 1.4 0l1.4 1.4a1 1 0 0 1-1.4 1.4L17 18.4a1 1 0 0 1 0-1.4ZM2 12a1 1 0 0 1 1-1h2a1 1 0 0 1 0 2H3a1 1 0 0 1-1-1Zm17 0a1 1 0 0 1 1-1h2a1 1 0 0 1 0 2h-2a1 1 0 0 1-1-1ZM4.2 19.8a1 1 0 0 1 0-1.4L5.6 17A1 1 0 0 1 7 18.4l-1.4 1.4a1 1 0 0 1-1.4 0ZM17 7a1 1 0 0 1 0-1.4l1.4-1.4a1 1 0 1 1 1.4 1.4L18.4 7A1 1 0 0 1 17 7Z"
          />
        } @else {
          <path d="M21 14.5A9 9 0 0 1 9.5 3 9 9 0 1 0 21 14.5Z" />
        }
      </svg>
    </button>
    <button
      mat-icon-button
      type="button"
      aria-label="Choisir la palette de couleurs"
      matTooltip="Palette de couleurs"
      [matMenuTriggerFor]="paletteMenu"
    >
      <span class="palette-indicator" aria-hidden="true"></span>
    </button>
    <mat-menu #paletteMenu="matMenu" aria-label="Palette de couleurs">
      @for (option of theme.palettes; track option.value) {
        <button
          mat-menu-item
          role="menuitemradio"
          [attr.aria-checked]="theme.palette() === option.value"
          (click)="theme.setPalette(option.value)"
        >
          {{ option.label }}
          @if (theme.palette() === option.value) {
            <span class="palette-check" aria-hidden="true">✓</span>
          }
        </button>
      }
    </mat-menu>
  `,
})
export class ThemeToggleComponent {
  protected readonly theme = inject(ThemeService);
}
