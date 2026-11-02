import { ChangeDetectionStrategy, Component, TemplateRef, input, output } from '@angular/core';
import { CategoryTabOption } from '../ui/category-tabs.component';
import { DetailDrawerComponent } from '../ui/detail-drawer/detail-drawer';
import { PageHeaderComponent } from '../ui/page-header.component';
import { SubnavComponent } from '../ui/subnav.component';
import { SubnavTemplateComponent } from './subnav-template.component';

/** Cadre commun des domaines d’administration et de leur édition protégée. */
@Component({
  selector: 'app-platform-admin-template',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DetailDrawerComponent, SubnavComponent, SubnavTemplateComponent, PageHeaderComponent],
  template: `
    <app-detail-drawer
      [opened]="editorOpen()"
      [title]="editorTitle()"
      [content]="editorContent()"
      size="standard"
      [modal]="true"
      mainScroll="parent"
      [dismissible]="editorDismissible()"
      [closeGuard]="editorCloseGuard()"
      (closed)="editorClosed.emit()"
    >
      <div drawer-main>
        <app-page-header [title]="title()" [visuallyHidden]="true" />
        <app-subnav-template>
          <app-subnav
            #domainNavigation
            presentation="capsule"
            panelId="platform-admin-panel"
            label="Domaines des paramètres"
            [options]="tabs()"
            [selected]="activeTab()"
            (selectedChange)="tabChange.emit($event)"
          />
        </app-subnav-template>
        <div
          id="platform-admin-panel"
          class="platform-admin-content"
          role="tabpanel"
          [attr.aria-labelledby]="domainNavigation.tabId(activeTab())"
        >
          <ng-content select="[platform-admin-content]" />
        </div>
      </div>
    </app-detail-drawer>
  `,
  styles: `
    .platform-admin-content {
      display: grid;
      gap: var(--space-4);
      margin-top: var(--space-4);
      max-width: 1440px;
      min-width: 0;
    }
  `,
})
export class PlatformAdminTemplateComponent {
  readonly title = input('Paramètres');
  readonly description = input.required<string>();
  readonly tabs = input.required<readonly CategoryTabOption[]>();
  readonly activeTab = input.required<string>();

  readonly editorOpen = input(false);
  readonly editorTitle = input('');
  readonly editorContent = input<TemplateRef<unknown> | undefined>(undefined);
  readonly editorDismissible = input(true);
  readonly editorCloseGuard = input<(() => boolean | Promise<boolean>) | undefined>();

  readonly tabChange = output<string>();
  readonly editorClosed = output<void>();
}
