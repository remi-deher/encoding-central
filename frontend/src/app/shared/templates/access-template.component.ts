import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { MatProgressBarModule } from '@angular/material/progress-bar';

/** Gabarit public partagé par la connexion et l’initialisation de l’installation. */
@Component({
  selector: 'app-access-template',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatProgressBarModule, NgTemplateOutlet],
  template: `
    <main class="access-stage" [class.access-stage_setup]="view() === 'setup'">
      <section
        class="access-frame"
        [class.access-frame_setup]="view() === 'setup'"
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
          <ng-container [ngTemplateOutlet]="brand" />
          <h1 id="access-title">{{ title() }}</h1>
          <p class="access-description">{{ description() }}</p>
        </header>
        <div class="access-context-column">
          <ng-content select="[access-context]" />
        </div>
        <section class="access-workbench" aria-label="Configuration de cette installation">
          <ng-content select="[access-content]" />
        </section>
      </section>
    </main>

    <ng-template #brand>
      <div class="access-brand">
        <span class="access-brand-mark" aria-hidden="true">
          <svg
            viewBox="0 0 24 24"
            width="20"
            height="20"
            fill="none"
            stroke="currentColor"
            stroke-width="2.2"
          >
            <polygon points="6 4 20 12 6 20 6 4" />
          </svg>
        </span>
        <span class="access-brand-name">Encoding</span>
        @if (view() === 'setup') {
          <span class="access-brand-tag">Premier démarrage</span>
        }
      </div>
    </ng-template>
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
      background: radial-gradient(ellipse at 50% 0, var(--accent-soft), transparent 65%);
    }
    .access-stage_setup {
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
    .access-brand {
      display: inline-flex;
      align-items: center;
      gap: var(--space-2);
      min-width: 0;
    }
    .access-brand-mark {
      display: grid;
      place-items: center;
      width: 40px;
      height: 40px;
      flex: 0 0 auto;
      border-radius: var(--radius-md);
      background: linear-gradient(135deg, var(--accent), var(--accent-strong));
      color: var(--on-accent);
    }
    .access-brand-name {
      color: var(--text-primary);
      font-size: var(--font-title-md);
      font-weight: 800;
      letter-spacing: -0.02em;
    }
    .access-brand-tag {
      padding: 2px var(--space-2);
      border-radius: var(--radius-pill);
      background: var(--accent-soft);
      color: var(--accent);
      font-size: var(--font-label);
      font-weight: 700;
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
    :host-context([data-theme='dark']) .access-stage_setup {
      background: radial-gradient(ellipse at 50% 0, var(--accent-soft), transparent 65%);
    }
  `,
})
export class AccessTemplateComponent {
  readonly view = input<'login' | 'setup'>('login');
  readonly title = input.required<string>();
  readonly description = input.required<string>();
  readonly busy = input(false);
  readonly progressLabel = input('Action en cours');
}
