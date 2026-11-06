import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { AuthService } from '../../../core/auth/auth.service';
import { IconComponent } from '../../../shared/ui/icon.component';
import { WorkerDataStore } from '../worker-data.store';
import { WorkerGroupsStore } from '../worker-groups.store';
import { WorkerGroupCardComponent } from './worker-group-card.component';

/** Onglet « Groupes de workers » : grille des groupes et état vide. */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-worker-groups-tab',
  imports: [MatButtonModule, IconComponent, WorkerGroupCardComponent],
  template: `
    <div class="groups-grid">
      @if (groups.filteredGroups().length === 0) {
        <div class="groups-empty">
          <span class="empty-icon"><app-icon name="sell" /></span>
          @if (groups.groupSearchQuery().trim()) {
            <h3 class="empty-title">Aucun résultat</h3>
            <p class="empty-desc">
              Aucun groupe ne correspond à « {{ groups.groupSearchQuery() }} ».
            </p>
          } @else {
            <h3 class="empty-title">Aucun groupe de workers</h3>
            <p class="empty-desc">
              Les groupes permettent d’organiser vos serveurs et workers, d’attribuer des étiquettes
              partagées et de contraindre l’exécution des flows.
            </p>
            @if (isAdmin()) {
              <button matButton="tonal" type="button" (click)="groups.createGroup()">
                Créer un groupe
              </button>
            }
          }
        </div>
      } @else {
        @for (group of groups.filteredGroups(); track group.id) {
          <app-worker-group-card
            [group]="group"
            [nodes]="data.nodes()"
            [isAdmin]="isAdmin()"
            (open)="groups.openGroupDetail(group)"
            (edit)="groups.editGroup(group)"
            (delete)="groups.confirmRemoveGroup(group)"
          />
        }
      }
    </div>
  `,
  styleUrl: './worker-groups-tab.component.scss',
})
export class WorkerGroupsTabComponent {
  readonly data = inject(WorkerDataStore);
  readonly groups = inject(WorkerGroupsStore);
  protected readonly isAdmin = inject(AuthService).isAdmin;
}
