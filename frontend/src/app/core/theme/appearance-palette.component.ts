import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { A11yModule } from '@angular/cdk/a11y';
import { OverlayModule, ConnectedPosition } from '@angular/cdk/overlay';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ThemePalette, ThemeService } from './theme.service';

const PALETTE_OPTIONS: readonly { value: ThemePalette; label: string }[] = [
  { value: 'forest', label: 'Forêt' },
  { value: 'ocean', label: 'Océan' },
  { value: 'iris', label: 'Iris' },
  { value: 'rose', label: 'Rose' },
];

@Component({
  selector: 'app-appearance-palette',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [A11yModule, MatButtonModule, MatButtonToggleModule, MatTooltipModule, OverlayModule],
  template: `
    <button
      mat-icon-button
      type="button"
      class="appearance-trigger"
      #appearanceTrigger
      cdkOverlayOrigin
      #appearanceOrigin="cdkOverlayOrigin"
      aria-label="Apparence : thème et palette de couleurs"
      aria-haspopup="dialog"
      [attr.aria-expanded]="opened()"
      [matTooltip]="'Apparence'"
      (click)="opened.update(toggle)"
    >
      <svg viewBox="0 0 24 24" aria-hidden="true">
        <path
          d="M12 3a9 9 0 1 0 0 18h1.1a2 2 0 0 0 1.5-3.3 1.7 1.7 0 0 1 1.3-2.8H18a3 3 0 0 0 3-3c0-5-4-8.9-9-8.9ZM7.5 12a1.4 1.4 0 1 1 0-2.8 1.4 1.4 0 0 1 0 2.8Zm3.1-3.7a1.4 1.4 0 1 1 0-2.8 1.4 1.4 0 0 1 0 2.8Zm4.2.4a1.4 1.4 0 1 1 0-2.8 1.4 1.4 0 0 1 0 2.8Zm2.7 3.9a1.4 1.4 0 1 1 0-2.8 1.4 1.4 0 0 1 0 2.8Z"
        />
      </svg>
    </button>

    <ng-template
      cdkConnectedOverlay
      [cdkConnectedOverlayOrigin]="appearanceOrigin"
      [cdkConnectedOverlayOpen]="opened()"
      [cdkConnectedOverlayHasBackdrop]="true"
      cdkConnectedOverlayBackdropClass="cdk-overlay-transparent-backdrop"
      [cdkConnectedOverlayPositions]="positions"
      [cdkConnectedOverlayPush]="true"
      [cdkConnectedOverlayViewportMargin]="12"
      (backdropClick)="close()"
      (overlayKeydown)="closeOnEscape($event)"
    >
      <section
        class="appearance-popover"
        role="dialog"
        aria-label="Apparence"
        cdkTrapFocus
        [cdkTrapFocusAutoCapture]="true"
      >
        <h2>Apparence</h2>
        <mat-button-toggle-group
          class="appearance-mode"
          aria-label="Mode de couleur"
          [value]="theme.current()"
          (change)="theme.set($event.value)"
        >
          <mat-button-toggle value="light">Clair</mat-button-toggle>
          <mat-button-toggle value="dark">Sombre</mat-button-toggle>
        </mat-button-toggle-group>

        <fieldset class="appearance-colors">
          <legend>Couleur</legend>
          <div class="appearance-color-options">
            @for (option of paletteOptions; track option.value) {
              <button
                type="button"
                class="appearance-color-option"
                [class.is-selected]="theme.palette() === option.value"
                [attr.aria-pressed]="theme.palette() === option.value"
                [attr.aria-label]="option.label"
                [attr.data-palette]="option.value"
                (click)="theme.setPalette(option.value)"
              >
                <span class="appearance-color-swatch" aria-hidden="true"></span>
                <span>{{ option.label }}</span>
              </button>
            }
          </div>
        </fieldset>
      </section>
    </ng-template>
  `,
  styles: `
    :host {
      display: block;
      width: 38px;
      height: 40px;
      flex: 0 0 38px;
    }
    .appearance-trigger {
      width: 40px;
      height: 40px;
      color: var(--sidebar-text-muted);
    }
    .appearance-trigger:hover {
      background: var(--sidebar-hover);
      color: var(--sidebar-text-strong);
    }
    .appearance-trigger:focus-visible {
      outline: 2px solid var(--sidebar-accent);
      outline-offset: 2px;
    }
    .appearance-trigger svg {
      width: 21px;
      height: 21px;
      fill: none;
      stroke: currentColor;
      stroke-width: 1.8;
      stroke-linecap: round;
      stroke-linejoin: round;
    }
    .appearance-popover {
      box-sizing: border-box;
      width: min(280px, calc(100vw - 24px));
      padding: var(--space-4);
      display: grid;
      gap: var(--space-3);
      border: 1px solid var(--border-strong);
      border-radius: var(--radius-xl);
      background: var(--surface-container-lowest);
      color: var(--text-primary);
      box-shadow: var(--elevation-3);
    }
    .appearance-popover h2 {
      margin: 0;
      font-size: var(--font-title-md);
      font-weight: 700;
    }
    .appearance-mode {
      width: 100%;
    }
    .appearance-mode mat-button-toggle {
      flex: 1;
    }
    .appearance-colors {
      min-width: 0;
      margin: 0;
      padding: 0;
      border: 0;
    }
    .appearance-colors legend {
      margin-bottom: var(--space-2);
      color: var(--text-secondary);
      font-size: var(--font-label);
      font-weight: 650;
    }
    .appearance-color-options {
      display: grid;
      grid-template-columns: repeat(4, minmax(0, 1fr));
      gap: var(--space-1);
    }
    .appearance-color-option {
      min-width: 0;
      min-height: 54px;
      padding: var(--space-1) 2px;
      display: flex;
      flex-direction: column;
      align-items: center;
      justify-content: center;
      gap: 4px;
      border: 1px solid transparent;
      border-radius: var(--radius-md);
      background: transparent;
      color: var(--text-secondary);
      font: inherit;
      font-size: var(--font-label);
      cursor: pointer;
    }
    .appearance-color-option:hover,
    .appearance-color-option.is-selected {
      background: var(--surface-container);
      color: var(--text-primary);
    }
    .appearance-color-option.is-selected {
      border-color: var(--accent-strong);
    }
    .appearance-color-option:focus-visible {
      outline: 2px solid var(--accent-strong);
      outline-offset: 1px;
    }
    .appearance-color-swatch {
      width: 20px;
      height: 20px;
      border-radius: var(--radius-pill);
      background: var(--appearance-swatch);
      box-shadow: inset 0 0 0 1px color-mix(in srgb, var(--text-primary) 15%, transparent);
    }
    .appearance-color-option[data-palette='forest'] {
      --appearance-swatch: var(--palette-swatch-forest);
    }
    .appearance-color-option[data-palette='ocean'] {
      --appearance-swatch: var(--palette-swatch-ocean);
    }
    .appearance-color-option[data-palette='iris'] {
      --appearance-swatch: var(--palette-swatch-iris);
    }
    .appearance-color-option[data-palette='rose'] {
      --appearance-swatch: var(--palette-swatch-rose);
    }
  `,
  host: { '(document:keydown)': 'closeOnEscape($event)' },
})
export class AppearancePaletteComponent {
  protected readonly theme = inject(ThemeService);
  protected readonly opened = signal(false);
  protected readonly paletteOptions = PALETTE_OPTIONS;
  protected readonly toggle = (value: boolean) => !value;
  private readonly trigger = viewChild<ElementRef<HTMLButtonElement>>('appearanceTrigger');
  protected readonly positions: ConnectedPosition[] = [
    { originX: 'end', originY: 'top', overlayX: 'end', overlayY: 'bottom', offsetY: -8 },
    { originX: 'end', originY: 'bottom', overlayX: 'end', overlayY: 'top', offsetY: 8 },
  ];

  protected closeOnEscape(event: KeyboardEvent): void {
    if (event.key !== 'Escape' || !this.opened()) return;
    event.preventDefault();
    event.stopPropagation();
    this.close();
  }

  protected close(): void {
    this.opened.set(false);
    this.trigger()?.nativeElement?.focus();
  }
}
