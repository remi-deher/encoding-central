import { Component, inject } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';
import { provideRouter, withDisabledInitialNavigation } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatDialogModule } from '@angular/material/dialog';
import { MatMenuModule } from '@angular/material/menu';
import { MatToolbarModule } from '@angular/material/toolbar';
import { ManagementCatalogTemplateComponent } from '../../src/app/shared/templates/management-catalog-template.component';
import { CategoryTabsComponent } from '../../src/app/shared/ui/category-tabs.component';
import { ContextBannerComponent } from '../../src/app/shared/ui/context-banner.component';
import { DetailCardGridComponent } from '../../src/app/shared/ui/detail-card-grid.component';
import { DataTableComponent } from '../../src/app/shared/ui/data-table.component';
import { StatusBadgeComponent } from '../../src/app/shared/ui/status-badge.component';
import { RecordFormComponent } from '../../src/app/shared/ui/record-form.component';
import { ThemeToggleComponent } from '../../src/app/core/theme/theme-toggle.component';
import { GuidedSetup } from '../shared/guided-setup';
import { MockClusterState } from './state';

@Component({
  selector: 'app-root',
  imports: [
    MatToolbarModule,
    MatButtonModule,
    MatMenuModule,
    MatDialogModule,
    ManagementCatalogTemplateComponent,
    CategoryTabsComponent,
    ContextBannerComponent,
    DetailCardGridComponent,
    DataTableComponent,
    StatusBadgeComponent,
    RecordFormComponent,
    GuidedSetup,
    ThemeToggleComponent,
  ],
  templateUrl: './mockup.html',
  styleUrl: './mockup.scss',
})
class NodeClusterMockup {
  readonly vm = inject(MockClusterState);
}

bootstrapApplication(NodeClusterMockup, {
  providers: [provideRouter([], withDisabledInitialNavigation())],
}).catch(console.error);
