import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, TemplateRef, input } from '@angular/core';
import { PageSystemActionsComponent } from './page-system-actions.component';

@Component({
  selector: 'app-page-header',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [NgTemplateOutlet, PageSystemActionsComponent],
  template: `
    <div class="page-header" [class.page-header_workspace]="layout() === 'workspace'">
      <div class="page-heading" [class.page-heading_empty]="!showHeading()">
        @if (showHeading()) {
          <h1 [class.visually-hidden]="visuallyHidden()">{{ title() }}</h1>
          @if (description()) {
            <p class="page-description">{{ description() }}</p>
          }
        }
        <div class="page-metadata"><ng-content select="[page-metadata]" /></div>
      </div>
      <div class="page-header-end">
        <div class="page-actions">
          <ng-content select="[page-actions]" />
          <ng-container [ngTemplateOutlet]="actions()" />
        </div>
        @if (showSystemActions()) {
          <app-page-system-actions />
        }
      </div>
      <div class="page-tools"><ng-content select="[page-tools]" /></div>
    </div>
  `,
  styleUrl: './page-header.component.scss',
})
export class PageHeaderComponent {
  readonly title = input.required<string>();
  readonly showHeading = input(true);
  readonly visuallyHidden = input(false);
  readonly layout = input<'default' | 'workspace'>('default');
  readonly description = input('');
  readonly actions = input<TemplateRef<unknown> | undefined>();
  readonly showSystemActions = input(false);
}
