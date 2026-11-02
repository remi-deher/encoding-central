import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { AuthService } from '../../core/auth/auth.service';
import { NotificationService } from '../../shared/services/notification.service';
import { NodeGroupResponse, NodeResponse } from '../../shared/models/cluster';
import { ConfirmationDialogComponent } from '../../shared/ui/confirmation-dialog.component';
import { ManagementCatalogTemplateComponent } from '../../shared/templates/management-catalog-template.component';
import { NodeGroupCardComponent } from './components/node-group-card.component';
import {
  NodeGroupEditorComponent,
  SaveNodeGroupPayload,
} from './components/node-group-editor.component';
import { NodeGroupDetailComponent } from './components/node-group-detail.component';
import { IconComponent } from '../../shared/ui/icon.component';

@Component({
  selector: 'app-node-groups-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ManagementCatalogTemplateComponent,
    NodeGroupCardComponent,
    NodeGroupEditorComponent,
    NodeGroupDetailComponent,
    IconComponent,
    MatButtonModule,
    RouterLink,
  ],
  templateUrl: './node-groups-page.component.html',
  styleUrl: './node-groups-page.component.scss',
})
export class NodeGroupsPageComponent {
  private readonly http = inject(HttpClient);
  private readonly notifications = inject(NotificationService);
  private readonly dialog = inject(MatDialog);
  protected readonly isAdmin = inject(AuthService).isAdmin;

  protected readonly groups = signal<readonly NodeGroupResponse[]>([]);
  protected readonly nodes = signal<readonly NodeResponse[]>([]);
  protected readonly state = signal<'loading' | 'ready' | 'error'>('loading');
  protected readonly message = signal('Chargement des groupes…');
  protected readonly searchQuery = signal('');

  // Drawer states
  protected readonly formOpen = signal(false);
  protected readonly detailOpen = signal(false);
  protected readonly busy = signal(false);
  protected readonly editingGroup = signal<NodeGroupResponse | null>(null);
  protected readonly selectedGroup = signal<NodeGroupResponse | null>(null);

  protected readonly filteredGroups = computed(() => {
    const query = this.searchQuery().trim().toLowerCase();
    const all = this.groups();
    if (!query) return all;
    return all.filter((g) => {
      const matchName = g.name.toLowerCase().includes(query);
      const matchDesc = g.description?.toLowerCase().includes(query) ?? false;
      const matchLabels = Object.entries(g.labels).some(
        ([k, v]) => k.toLowerCase().includes(query) || (v?.toLowerCase().includes(query) ?? false),
      );
      return matchName || matchDesc || matchLabels;
    });
  });

  constructor() {
    void this.load();
  }

  protected create(): void {
    this.editingGroup.set(null);
    this.detailOpen.set(false);
    this.formOpen.set(true);
  }

  protected openEdit(group: NodeGroupResponse): void {
    this.editingGroup.set(group);
    this.detailOpen.set(false);
    this.formOpen.set(true);
  }

  protected openDetail(group: NodeGroupResponse): void {
    this.selectedGroup.set(group);
    this.formOpen.set(false);
    this.detailOpen.set(true);
  }

  protected openEditFromDetail(): void {
    const group = this.selectedGroup();
    if (group) {
      this.openEdit(group);
    }
  }

  protected closeForm(): void {
    this.formOpen.set(false);
    this.editingGroup.set(null);
    this.busy.set(false);
  }

  protected closeDetail(): void {
    this.detailOpen.set(false);
    this.selectedGroup.set(null);
  }

  protected async onSave(payload: SaveNodeGroupPayload): Promise<void> {
    if (this.busy()) return;
    this.busy.set(true);

    const current = this.editingGroup();
    try {
      if (current) {
        await firstValueFrom(this.http.put(`/api/v1/node-groups/${current.id}`, payload));
      } else {
        await firstValueFrom(this.http.post('/api/v1/node-groups', payload));
      }
      await this.load();
      this.closeForm();
      this.notifications.show(
        current ? 'Groupe mis à jour avec succès.' : 'Groupe créé avec succès.',
      );
    } catch {
      this.notifications.show(
        'Le groupe n’a pas pu être enregistré. Vérifiez son nom, ses critères et ses étiquettes.',
      );
    } finally {
      this.busy.set(false);
    }
  }

  protected confirmDelete(group: NodeGroupResponse): void {
    this.dialog
      .open(ConfirmationDialogComponent, {
        data: {
          title: `Supprimer le groupe « ${group.name} » ?`,
          message:
            'Ce groupe et ses étiquettes partagées seront supprimés. Les nœuds membres et leur inventaire ne sont pas affectés. Attention : un groupe ciblé par un flow ne peut pas être supprimé.',
          confirmLabel: 'Supprimer',
          variant: 'danger',
        },
      })
      .afterClosed()
      .subscribe((confirmed: boolean) => {
        if (confirmed) {
          void this.deleteGroup(group);
        }
      });
  }

  private async deleteGroup(group: NodeGroupResponse): Promise<void> {
    try {
      await firstValueFrom(this.http.delete(`/api/v1/node-groups/${group.id}`));
      await this.load();
      if (this.selectedGroup()?.id === group.id) {
        this.closeDetail();
      }
      this.notifications.show('Groupe supprimé avec succès.');
    } catch (error) {
      const title = (error as { error?: { title?: unknown } }).error?.title;
      this.notifications.show(
        typeof title === 'string'
          ? title
          : 'Le groupe n’a pas pu être supprimé. Vérifiez qu’il n’est pas ciblé par un flow.',
      );
    }
  }

  private async load(): Promise<void> {
    this.state.set('loading');
    try {
      const [groups, nodes] = await Promise.all([
        firstValueFrom(this.http.get<readonly NodeGroupResponse[]>('/api/v1/node-groups')),
        firstValueFrom(this.http.get<readonly NodeResponse[]>('/api/v1/nodes')),
      ]);
      this.groups.set(groups);
      this.nodes.set(nodes);
      this.state.set('ready');
    } catch {
      this.state.set('error');
      this.message.set('Impossible de charger les groupes de nœuds et les membres du cluster.');
    }
  }
}
