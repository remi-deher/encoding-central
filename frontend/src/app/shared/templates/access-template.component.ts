import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { MatProgressBarModule } from '@angular/material/progress-bar';

/** Gabarit public partagé par la connexion et l’initialisation de l’installation. */
@Component({
  selector: 'app-access-template',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatProgressBarModule],
  template: `
    <main class="access-stage" [class.access-stage_setup]="view() === 'setup'">
      <section
        class="access-frame"
        [class.access-frame_setup]="view() === 'setup'"
        [class.access-frame_single]="view() === 'setup' && layout() === 'single'"
        [class.access-frame_stacked]="view() === 'setup' && layout() === 'stacked'"
        [attr.aria-label]="view() === 'setup' ? 'Assistant de premier démarrage' : null"
        aria-labelledby="access-title"
      >
        @if (busy()) {
          <mat-progress-bar
            class="access-progress"
            mode="indeterminate"
            [attr.aria-label]="progressLabel()"
          />
        }
        <div class="access-alert-slot"><ng-content select="[access-alert]" /></div>
        <header class="access-heading">
          <h1 id="access-title">{{ title() }}</h1>
          <p class="access-description">{{ description() }}</p>
        </header>
        <section class="access-workbench" aria-label="Configuration de cette installation">
          <ng-content select="[access-content]" />
        </section>
        <div class="access-context-column">
          <ng-content select="[access-context]" />
        </div>
      </section>
    </main>

  `,
  styles: `
    :host {
      display: block;
      min-height: 100%;
    }
    .access-stage {
      box-sizing: border-box;
      display: grid;
      place-items: center;
      min-height: 100%;
      padding: var(--space-5) var(--space-4);
      background: var(--page-background);
    }
    .access-frame {
      position: relative;
      width: min(100%, 400px);
      box-sizing: border-box;
      overflow: hidden;
      display: grid;
      gap: var(--space-4);
      padding: var(--space-6) 28px;
      border-radius: var(--radius-2xl);
      background: var(--surface-container);
      box-shadow: var(--elevation-2);
    }
    .access-frame_setup {
      overflow: visible;
      width: min(100%, 1060px);
      display: grid;
      grid-template-columns: minmax(290px, 340px) minmax(0, 1fr);
      grid-template-rows: auto auto auto;
      column-gap: var(--space-5);
      row-gap: var(--space-4);
      align-items: start;
      padding: 0;
      border-radius: 0;
      background: transparent;
      box-shadow: none;
    }
    .access-frame_setup .access-alert-slot {
      grid-column: 1 / -1;
    }
    .access-frame_setup .access-heading,
    .access-frame_setup .access-context-column {
      grid-column: 1;
    }
    .access-frame_setup .access-workbench {
      grid-column: 2;
      grid-row: 2 / span 2;
    }
    .access-frame_single {
      width: min(100%, 680px);
      grid-template-columns: minmax(0, 1fr);
      grid-template-rows: auto;
    }
    .access-frame_single .access-heading,
    .access-frame_single .access-workbench {
      grid-column: 1;
      grid-row: auto;
    }
    .access-frame_single .access-context-column {
      display: none;
    }
    .access-frame_stacked {
      width: min(100%, 1060px);
      grid-template-columns: minmax(0, 1fr);
      grid-template-rows: auto;
    }
    .access-frame_stacked .access-heading,
    .access-frame_stacked .access-workbench,
    .access-frame_stacked .access-context-column {
      grid-column: 1;
      grid-row: auto;
    }
    .access-context-column {
      display: grid;
      gap: var(--space-4);
      min-width: 0;
    }
    .access-heading {
      display: grid;
      gap: var(--space-3);
    }
    .access-workbench {
      min-width: 0;
    }
    .access-frame:not(.access-frame_setup) .access-alert-slot,
    .access-frame:not(.access-frame_setup) .access-context-column {
      display: none;
    }
    .access-frame:not(.access-frame_setup) .access-workbench {
      display: contents;
    }
    .access-progress {
      position: absolute;
      z-index: 1;
      inset: 0 0 auto;
      overflow: hidden;
      border-radius: var(--radius-pill);
    }
    .access-frame:not(.access-frame_setup) > .access-progress {
      border-radius: 0;
    }
    h1 {
      margin: 0;
      color: var(--text-primary);
      font-size: var(--font-headline);
      font-weight: 800;
      letter-spacing: -0.02em;
    }
    .access-frame_setup .access-heading h1 {
      font-size: var(--font-title-lg);
    }
    .access-description {
      margin: 0;
      color: var(--text-secondary);
      font-size: var(--font-body);
      line-height: 1.6;
    }
    @media (max-width: 900px) {
      .access-frame_setup {
        grid-template-columns: minmax(0, 1fr);
      }
      .access-frame_setup .access-alert-slot,
      .access-frame_setup .access-heading,
      .access-frame_setup .access-context-column,
      .access-frame_setup .access-workbench {
        grid-column: 1;
        grid-row: auto;
      }
    }
    @media (max-width: 480px) {
      .access-frame:not(.access-frame_setup) {
        padding-inline: var(--space-4);
      }
    }
  `,
})
export class AccessTemplateComponent {
  readonly view = input<'login' | 'setup'>('login');
  readonly layout = input<'split' | 'single' | 'stacked'>('split');
  readonly title = input.required<string>();
  readonly description = input.required<string>();
  readonly busy = input(false);
  readonly progressLabel = input('Action en cours');
}
