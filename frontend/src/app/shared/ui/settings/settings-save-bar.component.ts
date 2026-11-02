import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';

/** Barre d’enregistrement collée en bas, affichée seulement quand il y a des modifications. */
@Component({
  selector: 'app-settings-save-bar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule],
  template: `
    @if (visible()) {
      <footer class="save-bar" role="region" aria-label="Modifications non enregistrées">
        <span>Modifications non enregistrées</span>
        <div>
          <button matButton type="button" [disabled]="busy()" (click)="cancelled.emit()">
            Annuler
          </button>
          <button
            matButton="filled"
            type="button"
            [disabled]="busy() || invalid()"
            (click)="saved.emit()"
          >
            {{ busy() ? 'Enregistrement…' : 'Enregistrer' }}
          </button>
        </div>
      </footer>
    }
  `,
  styles: `
    .save-bar {
      position: sticky;
      bottom: var(--space-3);
      z-index: 10;
      display: flex;
      justify-content: space-between;
      align-items: center;
      flex-wrap: wrap;
      gap: var(--space-3);
      padding: var(--space-2) var(--space-2) var(--space-2) var(--space-5);
      background: var(--surface-container-highest);
      border-radius: var(--radius-xl);
      box-shadow: var(--elevation-2);
    }
    div {
      display: flex;
      gap: var(--space-2);
    }
  `,
})
export class SettingsSaveBarComponent {
  readonly visible = input(false);
  readonly busy = input(false);
  readonly invalid = input(false);
  readonly saved = output<void>();
  readonly cancelled = output<void>();
}
