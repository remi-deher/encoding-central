import {
  ChangeDetectionStrategy,
  Component,
  input,
  output,
} from '@angular/core';
import { CategoryTabOption } from '../ui/category-tabs.component';
import { IconComponent } from '../ui/icon.component';
import { SubnavComponent } from '../ui/subnav.component';
import { SubnavTemplateComponent } from './subnav-template.component';

/** Cadre commun de synthèse et d’orientation pour le rôle métier Piloter. */
@Component({
  selector: 'app-monitor-template',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent, SubnavComponent, SubnavTemplateComponent],
  template: `
    <h1 class="visually-hidden">{{ title() }}</h1>
    <app-subnav-template>
      @if (metadata()) {
        <div subnav-info class="cluster-context">
          <span class="cluster-context-symbol"><app-icon name="server" /></span>
          <span class="cluster-context-copy">
            <span class="cluster-context-label">Cluster</span>
            <strong>{{ metadata() }}</strong>
          </span>
        </div>
      }
      <app-subnav
        #viewNavigation
        presentation="capsule"
        panelId="monitor-panel"
        [label]="viewsLabel()"
        [options]="views()"
        [selected]="selectedView()"
        (selectedChange)="selectedViewChange.emit($event)"
      />
      <div subnav-actions><ng-content select="[monitor-subnav-actions]" /></div>
    </app-subnav-template>

    <section
      id="monitor-panel"
      role="tabpanel"
      [attr.aria-labelledby]="viewNavigation.tabId(selectedView())"
    >
      @if (state() === 'loading') {
        <p class="monitor-feedback" role="status">{{ loadingMessage() }}</p>
      } @else if (state() === 'error') {
        <p class="monitor-feedback" role="alert">
          {{ errorMessage() }}
          @if (hasKnownData()) {
            {{ staleMessage() }}
          }
        </p>
      }

      @if (layout() === 'special') {
        <div class="monitor-special"><ng-content select="[monitor-special]" /></div>
      } @else {
        <div class="monitor-summary"><ng-content select="[monitor-summary]" /></div>
        <div
          class="monitor-grid monitor-grid_primary"
          [class.monitor-grid_single]="layout() === 'single'"
        >
          <div class="monitor-slot"><ng-content select="[monitor-primary]" /></div>
          <div class="monitor-slot"><ng-content select="[monitor-context]" /></div>
        </div>
        <div
          class="monitor-grid monitor-grid_supporting"
          [class.monitor-grid_single]="layout() === 'single'"
        >
          <div class="monitor-slot"><ng-content select="[monitor-activity]" /></div>
          <div class="monitor-slot"><ng-content select="[monitor-supporting]" /></div>
        </div>
      }
    </section>
  `,
  styles: `
    :host {
      display: block;
    }
    .cluster-context {
      display: inline-flex;
      align-items: center;
      gap: var(--space-2);
      min-width: 0;
      max-width: 100%;
    }
    .cluster-context-symbol {
      display: inline-flex;
      width: var(--touch-target);
      height: var(--touch-target);
      align-items: center;
      justify-content: center;
      flex: 0 0 auto;
      border-radius: var(--radius-lg);
      background: var(--accent-soft);
      color: var(--accent-strong);
    }
    .cluster-context-copy {
      display: grid;
      min-width: 0;
      gap: var(--space-1);
    }
    .cluster-context-label {
      color: var(--text-muted);
      font-size: var(--font-label);
      font-weight: 700;
      letter-spacing: 0.06em;
      line-height: 1;
      text-transform: uppercase;
    }
    .cluster-context-copy strong {
      max-width: 100%;
      overflow: hidden;
      color: var(--text-primary);
      font-size: var(--font-body);
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .monitor-feedback {
      margin: 0 0 var(--space-4);
      color: var(--text-secondary);
      font-size: var(--font-label);
    }
    .monitor-summary:not(:empty) {
      margin-bottom: var(--space-6);
    }
    .monitor-grid {
      display: grid;
      grid-template-columns: minmax(0, 1.65fr) minmax(0, 1fr);
      gap: var(--space-6);
      align-items: start;
    }
    .monitor-grid_single {
      grid-template-columns: minmax(0, 1fr);
    }
    .monitor-grid_supporting {
      margin-top: var(--space-6);
    }
    .monitor-slot {
      min-width: 0;
    }
    .monitor-slot:empty {
      display: none;
    }
    .monitor-special {
      min-width: 0;
    }
    @media (max-width: 1080px) {
      .monitor-grid {
        grid-template-columns: minmax(0, 1fr);
      }
    }
  `,
})
export class MonitorTemplateComponent {
  readonly title = input.required<string>();
  readonly metadata = input('');
  readonly viewsLabel = input('Vues d’ensemble');
  readonly views = input.required<readonly CategoryTabOption[]>();
  readonly selectedView = input.required<string>();
  readonly selectedViewChange = output<string>();
  readonly state = input<'ready' | 'loading' | 'error'>('ready');
  readonly hasKnownData = input(false);
  readonly loadingMessage = input('Chargement des données…');
  readonly errorMessage = input('Impossible d’actualiser les données.');
  readonly staleMessage = input(
    'Les dernières valeurs reçues sont conservées ; elles peuvent être obsolètes.',
  );
  readonly layout = input<'overview' | 'single' | 'special'>('overview');
}
