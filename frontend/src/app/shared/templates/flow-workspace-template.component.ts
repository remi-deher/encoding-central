import { ChangeDetectionStrategy, Component } from '@angular/core';

/** Cadre commun des parcours de conception plein écran d'un flow. */
@Component({
  selector: 'app-flow-workspace-template',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="flow-workspace">
      <div class="flow-workspace-toolbar">
        <ng-content select="[flow-workspace-toolbar]" />
      </div>
      <div class="flow-workspace-main">
        <ng-content select="[flow-workspace-main]" />
      </div>
      <div class="flow-workspace-tray">
        <ng-content select="[flow-workspace-tray]" />
      </div>
      <ng-content select="[flow-workspace-overlay]" />
      <ng-content />
    </div>
  `,
  styles: `
    :host {
      display: block;
      height: 100%;
      min-height: 0;
      overflow: hidden;
    }

    .flow-workspace {
      display: flex;
      flex-direction: column;
      height: 100%;
      width: 100%;
      overflow: hidden;
      background: var(--surface-container-lowest);
      color: var(--text-primary);
    }

    .flow-workspace-toolbar,
    .flow-workspace-tray {
      flex: 0 0 auto;
    }

    .flow-workspace-main {
      display: flex;
      flex: 1 1 auto;
      min-height: 0;
      overflow: hidden;
      position: relative;
    }
  `,
})
export class FlowWorkspaceTemplateComponent {}
