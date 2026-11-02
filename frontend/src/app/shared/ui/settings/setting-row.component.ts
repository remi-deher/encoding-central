import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/** Pile de lignes groupées (coins extérieurs larges, intérieurs adoucis). */
@Component({
  selector: 'app-setting-group',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<ng-content />`,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: 3px;
      min-width: 0;
    }
  `,
})
export class SettingGroupComponent {}

/**
 * Ligne de réglage : libellé et aide à gauche, contrôle à droite (`ng-content`).
 * `stacked` place un champ large sous le libellé ; `lead` accueille une pastille ou une icône.
 */
@Component({
  selector: 'app-setting-row',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[class.stacked]': 'stacked()', '[class.dense]': 'dense()' },
  template: `
    <ng-content select="[row-lead]" />
    <div class="row-text">
      <strong>{{ label() }}</strong>
      @if (hint()) {
        <span class="row-hint">{{ hint() }}</span>
      }
      <ng-content select="[row-detail]" />
    </div>
    <div class="row-control"><ng-content /></div>
  `,
  styles: `
    :host {
      display: flex;
      align-items: center;
      gap: var(--space-4);
      min-width: 0;
      padding: var(--space-3) var(--space-4);
      background: var(--surface-container);
      border-radius: var(--radius-sm);
      transition: background-color 150ms var(--motion-emphasized, ease);
    }
    :host(:hover) {
      background: var(--surface-container-high);
    }
    :host(:first-child) {
      border-top-left-radius: var(--radius-xl);
      border-top-right-radius: var(--radius-xl);
    }
    :host(:last-child) {
      border-bottom-left-radius: var(--radius-xl);
      border-bottom-right-radius: var(--radius-xl);
    }
    :host(.dense) {
      padding-block: var(--space-2);
      min-height: 48px;
    }
    :host(.stacked) {
      flex-wrap: wrap;
      align-items: stretch;
    }
    :host(.stacked) .row-text {
      flex-basis: 100%;
    }
    :host(.stacked) .row-control {
      flex: 1 1 100%;
      justify-content: stretch;
    }
    .row-text {
      flex: 1 1 220px;
      display: grid;
      gap: 2px;
      min-width: 0;
    }
    strong {
      font-weight: 600;
      overflow-wrap: anywhere;
    }
    .row-hint {
      color: var(--text-secondary);
      font-size: var(--font-label);
      line-height: 1.5;
    }
    .row-control {
      display: flex;
      align-items: center;
      justify-content: flex-end;
      flex-wrap: wrap;
      gap: var(--space-2);
      min-width: 0;
    }
    .row-control:empty {
      display: none;
    }
    @media (max-width: 640px) {
      :host {
        flex-wrap: wrap;
      }
      .row-control {
        flex: 1 1 100%;
        justify-content: flex-start;
      }
    }
  `,
})
export class SettingRowComponent {
  readonly label = input.required<string>();
  readonly hint = input('');
  readonly stacked = input(false);
  readonly dense = input(false);
}
