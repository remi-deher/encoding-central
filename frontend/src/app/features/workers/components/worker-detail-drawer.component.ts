import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { MatTabsModule } from '@angular/material/tabs';
import { DataTableComponent } from '../../../shared/ui/data-table.component';
import { DetailCardGridComponent } from '../../../shared/ui/detail-card-grid.component';
import { DetailFieldListComponent } from '../../../shared/ui/detail-field-list.component';
import { DrawerActionButtonComponent } from '../../../shared/ui/drawer-action-button/drawer-action-button.component';
import { RecordFormComponent } from '../../../shared/ui/record-form.component';
import { ResourceSheetComponent } from '../../../shared/ui/resource-sheet.component';
import { SystemLogViewerComponent } from '../../../shared/ui/system-log-viewer/system-log-viewer.component';
import { WeeklyScheduleFormComponent } from '../../../shared/ui/weekly-schedule-form.component';
import { WorkerDataStore } from '../worker-data.store';
import { WorkerDetailStore } from '../worker-detail.store';
import { WorkerMembersStore } from '../worker-members.store';
import { WorkerProcessingStore } from '../worker-processing.store';

/** Contenu du tiroir de fiche d’un worker membre du central (onglets Général à Journaux). */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-worker-detail-drawer',
  imports: [
    MatTabsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatSelectModule,
    ResourceSheetComponent,
    DetailCardGridComponent,
    DetailFieldListComponent,
    DataTableComponent,
    DrawerActionButtonComponent,
    RecordFormComponent,
    WeeklyScheduleFormComponent,
    SystemLogViewerComponent,
  ],
  templateUrl: './worker-detail-drawer.component.html',
  styleUrl: './worker-detail-drawer.component.scss',
})
export class WorkerDetailDrawerComponent {
  readonly data = inject(WorkerDataStore);
  readonly detail = inject(WorkerDetailStore);
  readonly members = inject(WorkerMembersStore);
  readonly processing = inject(WorkerProcessingStore);
}
