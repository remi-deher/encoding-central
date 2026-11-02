import { ChangeDetectionStrategy, Component } from '@angular/core';

/** Gabarit commun des pages organisées autour d’une sous-navigation. */
@Component({
  selector: 'app-subnav-template',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="subnav-template">
      <div class="subnav-template-info"><ng-content select="[subnav-info]" /></div>
      <div class="subnav-template-navigation"><ng-content select="app-subnav" /></div>
      <div class="subnav-template-actions"><ng-content select="[subnav-actions]" /></div>
    </div>
  `,
  styleUrl: './subnav-template.component.scss',
})
export class SubnavTemplateComponent {}
